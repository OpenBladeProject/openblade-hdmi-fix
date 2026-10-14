using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.HdmiFix.Desktop;

internal sealed record RecordingProgress(TimeSpan Elapsed, int Samples, int Blackouts, bool IsFinalizing = false);

internal sealed record ConnectionRecordingResult(
    string DirectoryPath, string? BundlePath, bool Completed, string Message);

internal sealed class ConnectionRecording
{
    private const int MaximumTimelineEntries = 512;
    private const int MaximumMarkers = 192;
    private const int MaximumBlackouts = 64;
    private const int MaximumAutomaticMarkers = 128;
    private const long ReservedTailBytes = MaximumMarkers * 512L + 1024;
    private const long MaximumTimelineBytes = 4 * 1024 * 1024;
    private const int MaximumBaselineCharacters = 256 * 1024;
    private const int MaximumEvidenceEvents = 256;
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaximumReadWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FinalizationWait = TimeSpan.FromSeconds(10);
    private static readonly object LiveReadLock = new();
    private static ActiveLiveRead? activeLiveRead;
    private static int activeRecording;
    private static long nextReaderOwner;

    private readonly MonitorView selected;
    private readonly string baselineReport;
    private readonly Func<LiveDisplayScan> readLive;
    private readonly Func<DateTimeOffset, DateTimeOffset, EvidenceEvents> readEvents;
    private readonly Func<string, CancellationToken, Task<string>> captureDisplayLog;
    private readonly TimeSpan duration;
    private readonly TimeSpan interval;
    private readonly long readerOwner = Interlocked.Increment(ref nextReaderOwner);
    private readonly object stateLock = new();
    private readonly Queue<PendingMarker> markers = new();
    private readonly Dictionary<string, string> displayLabels = new(StringComparer.OrdinalIgnoreCase);
    private Stopwatch? clock;
    private DateTimeOffset startedUtc;
    private int state;
    private int acceptedBlackouts;
    private int acceptedDisplayChanges;
    private int acceptedDeviceChanges;

    internal ConnectionRecording(
        MonitorView selected,
        string baselineReport,
        string outputRoot,
        Func<LiveDisplayScan>? readLive = null,
        TimeSpan? duration = null,
        TimeSpan? interval = null)
        : this(selected, baselineReport, outputRoot, readLive,
            ConnectionEvidence.ReadEvents, ConnectionEvidence.CaptureDisplayLogAsync, duration, interval)
    {
    }

    internal ConnectionRecording(
        MonitorView selected,
        string baselineReport,
        string outputRoot,
        Func<LiveDisplayScan>? readLive,
        Func<DateTimeOffset, DateTimeOffset, EvidenceEvents> readEvents,
        Func<string, CancellationToken, Task<string>> captureDisplayLog,
        TimeSpan? duration = null,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(baselineReport);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(readEvents);
        ArgumentNullException.ThrowIfNull(captureDisplayLog);

        this.selected = selected;
        this.baselineReport = BoundText(baselineReport, MaximumBaselineCharacters);
        this.readLive = readLive ?? (() => LiveDisplayDiagnosticsReader.Read(selected.InstanceId));
        this.readEvents = readEvents;
        this.captureDisplayLog = captureDisplayLog;
        this.duration = duration ?? DefaultDuration;
        this.interval = interval ?? DefaultInterval;
        if (this.duration <= TimeSpan.Zero || this.duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        if (this.interval < TimeSpan.FromMilliseconds(5) || this.interval > this.duration)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        string name = $"connection-recording-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..47];
        DirectoryPath = Path.Combine(Path.GetFullPath(outputRoot), name);
    }

    internal string DirectoryPath { get; }

    internal bool MarkBlackout() => AddMarker("blackout");

    internal bool NotifyDisplayChange() => AddMarker("display-change");

    internal bool NotifyDeviceChange() => AddMarker("device-change");

    internal async Task<ConnectionRecordingResult> RunAsync(
        CancellationToken token, IProgress<RecordingProgress>? progress = null)
    {
        if (Interlocked.CompareExchange(ref activeRecording, 1, 0) != 0)
        {
            return new(DirectoryPath, null, false, "Another connection recording is already running.");
        }

        string? bundlePath = null;
        bool completed = false;
        string message;
        int samples = 0;
        int timelineEntries = 0;
        bool timelineFull = false;
        TimeSpan recordedElapsed = TimeSpan.Zero;
        startedUtc = DateTimeOffset.UtcNow;
        clock = Stopwatch.StartNew();
        lock (stateLock)
        {
            state = 1;
            displayLabels[selected.InstanceId] = "Selected display";
        }

        try
        {
            Report(progress, 0);
            Directory.CreateDirectory(DirectoryPath);
            WriteDurableText(Path.Combine(DirectoryPath, "report.txt"), baselineReport);
            string timelinePath = Path.Combine(DirectoryPath, "timeline.jsonl");
            using var file = new FileStream(timelinePath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 4096, FileOptions.None);
            using var writer = new StreamWriter(file, new UTF8Encoding(false), 4096, leaveOpen: true);

            AppendTimeline(writer, file, RecordingEntry.Start(startedUtc), ref timelineEntries, ref timelineFull);
            while (!token.IsCancellationRequested && clock.Elapsed < duration && !timelineFull)
            {
                DrainMarkers(writer, file, ref timelineEntries, ref timelineFull);
                if (timelineFull)
                {
                    break;
                }

                TimeSpan remaining = duration - clock.Elapsed;
                LiveReadResult capture;
                try
                {
                    capture = await ReadLiveAsync(readerOwner, readLive,
                        Min(MaximumReadWait, remaining), token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                if (token.IsCancellationRequested)
                {
                    break;
                }

                // Acquire marker timestamps and the sample timestamp together, then
                // release the lock before disk I/O so UI notifications stay responsive.
                PendingMarker[] beforeSample;
                RecordingEntry sample;
                lock (stateLock)
                {
                    beforeSample = markers.ToArray();
                    markers.Clear();
                    sample = CreateSample(capture);
                }
                foreach (PendingMarker marker in beforeSample)
                    AppendTimeline(writer, file, RecordingEntry.Marker(marker), ref timelineEntries, ref timelineFull, final: true);
                if (timelineFull) break;
                if (AppendTimeline(writer, file, sample, ref timelineEntries, ref timelineFull))
                {
                    samples++;
                    Report(progress, samples);
                }

                TimeSpan next = TimeSpan.FromTicks(interval.Ticks
                    * Math.Max(1, (long)Math.Floor(clock.Elapsed.TotalMilliseconds / interval.TotalMilliseconds) + 1));
                TimeSpan delay = Min(next - clock.Elapsed, duration - clock.Elapsed);
                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
            completed = !token.IsCancellationRequested && clock.Elapsed >= duration;
            recordedElapsed = clock.Elapsed;

            PendingMarker[] finalMarkers;
            lock (stateLock)
            {
                state = 2;
                finalMarkers = markers.ToArray();
                markers.Clear();
            }
            Report(progress, samples, finalizing: true);
            foreach (PendingMarker marker in finalMarkers)
            {
                AppendTimeline(writer, file, RecordingEntry.Marker(marker), ref timelineEntries, ref timelineFull, final: true);
            }
            AppendTimeline(writer, file, RecordingEntry.Stop(DateTimeOffset.UtcNow, clock.Elapsed,
                completed ? "duration-complete" : token.IsCancellationRequested ? "requested-stop" : "timeline-limit"),
                ref timelineEntries, ref timelineFull, final: true);
            writer.Flush();
            file.Flush(flushToDisk: true);
            writer.Dispose();
            file.Dispose();

            EvidenceSummary evidence = await CollectEvidenceAsync(startedUtc, DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
            WriteJsonDurably(Path.Combine(DirectoryPath, "events.json"), evidence);
            WriteDurableText(Path.Combine(DirectoryPath, "summary.txt"), BuildSummary(
                completed, recordedElapsed, samples, timelineEntries, timelineFull, evidence));
            bundlePath = CreateBundle();
            message = completed
                ? "Connection recording completed. The shareable bundle is ready."
                : "Connection recording stopped. A partial shareable bundle was saved.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // This path is only reachable before the independent finalization phase.
            message = "Connection recording stopped before the partial bundle could be finalized.";
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            message = "Connection recording stopped: " + SafeError(error);
        }
        finally
        {
            lock (stateLock)
            {
                state = 3;
            }
            Interlocked.Exchange(ref activeRecording, 0);
        }

        return new(DirectoryPath, bundlePath, completed, message);
    }

    private bool AddMarker(string kind)
    {
        lock (stateLock)
        {
            if (state != 1 || clock is null
                || (kind == "blackout" ? acceptedBlackouts >= MaximumBlackouts
                    : acceptedDisplayChanges + acceptedDeviceChanges >= MaximumAutomaticMarkers))
            {
                return false;
            }

            markers.Enqueue(new(kind, DateTimeOffset.UtcNow, clock.Elapsed));
            if (kind == "blackout")
            {
                acceptedBlackouts++;
            }
            else if (kind == "display-change")
            {
                acceptedDisplayChanges++;
            }
            else
            {
                acceptedDeviceChanges++;
            }
            return true;
        }
    }

    private RecordingEntry CreateSample(LiveReadResult capture)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan elapsed = clock?.Elapsed ?? TimeSpan.Zero;
        if (capture.Scan is null)
        {
            return RecordingEntry.Sample(now, elapsed, capture.Status, null, []);
        }

        LiveDisplayScan scan = capture.Scan;
        var displays = new List<RecordedDisplay>(Math.Min(scan.Displays.Count, 64));
        int unresolved = 0;
        foreach (LiveDisplayDiagnostics display in scan.Displays.Take(64))
        {
            string label;
            bool selectedDisplay = display.InstanceId.Equals(selected.InstanceId, StringComparison.OrdinalIgnoreCase);
            if (selectedDisplay)
            {
                label = "Selected display";
            }
            else if (display.InstanceId.Length == 0)
            {
                label = $"Unresolved display {++unresolved}";
            }
            else
            {
                lock (stateLock)
                {
                    if (!displayLabels.TryGetValue(display.InstanceId, out label!))
                    {
                        label = $"Display {displayLabels.Count + 1}";
                        displayLabels.Add(display.InstanceId, label);
                    }
                }
            }

            displays.Add(new(
                label,
                selectedDisplay,
                DiagnosticValues.Text(display.MonitorName),
                DiagnosticValues.Text(display.Mode),
                DiagnosticValues.Text(display.Connection),
                DiagnosticValues.Text(display.Hdr),
                DiagnosticValues.Text(display.VrrCapability),
                DiagnosticValues.Text(display.VrrRequested),
                DiagnosticValues.Text(display.VrrEnabled),
                DiagnosticValues.Text(display.VrrActive),
                display.Error.Length == 0 ? "" : DiagnosticValues.Status(display.Error),
                SafeScdc(display.Scdc)));
        }

        string status = scan.Failure.Length != 0 ? DiagnosticValues.Status(scan.Failure)
            : scan.HasUnresolvedPaths ? "BindingIncomplete" : capture.Status;
        int selectedPaths = displays.Count(display => display.Selected);
        bool ambiguousBinding = displays.Any(display => display.Selected
            && display.Error.Contains("Binding:Ambiguous", StringComparison.Ordinal));
        bool? selectedConnected = selectedPaths == 1 && !ambiguousBinding ? true
            : selectedPaths > 1 || ambiguousBinding || scan.HasUnresolvedPaths || scan.Failure.Length != 0 ? null : false;
        return RecordingEntry.Sample(now, elapsed, status, selectedConnected, displays);
    }

    private static ScdcObservation SafeScdc(ScdcObservation? observation)
    {
        observation ??= ScdcObservation.Unknown("Scdc:Unavailable");
        return observation with { Status = DiagnosticValues.Status(observation.Status) };
    }

    private void DrainMarkers(StreamWriter writer, FileStream file,
        ref int entries, ref bool timelineFull)
    {
        PendingMarker[] pending;
        lock (stateLock)
        {
            pending = markers.ToArray();
            markers.Clear();
        }
        foreach (PendingMarker marker in pending)
        {
            AppendTimeline(writer, file, RecordingEntry.Marker(marker), ref entries, ref timelineFull, final: true);
        }
    }

    private static bool AppendTimeline(StreamWriter writer, FileStream file, RecordingEntry entry,
        ref int entries, ref bool timelineFull, bool final = false)
    {
        int entryLimit = final ? MaximumTimelineEntries : MaximumTimelineEntries - MaximumMarkers - 1;
        if ((!final && timelineFull) || entries >= entryLimit)
        {
            timelineFull = true;
            return false;
        }

        string line = JsonSerializer.Serialize(entry, JsonOptions);
        long bytes = Encoding.UTF8.GetByteCount(line) + 2L;
        long byteLimit = final ? MaximumTimelineBytes : MaximumTimelineBytes - ReservedTailBytes;
        if (file.Position + bytes > byteLimit)
        {
            timelineFull = true;
            return false;
        }

        writer.WriteLine(line);
        writer.Flush();
        file.Flush(flushToDisk: true);
        entries++;
        return true;
    }

    private async Task<EvidenceSummary> CollectEvidenceAsync(DateTimeOffset from, DateTimeOffset to)
    {
        using var timeout = new CancellationTokenSource(FinalizationWait);
        Task<EvidenceEvents> eventsTask = Task.Run(() => readEvents(from, to));
        string privateDirectory = Path.Combine(DirectoryPath, "private");
        Task<string> logTask;
        try
        {
            logTask = captureDisplayLog(privateDirectory, timeout.Token);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            logTask = Task.FromResult("Unavailable:" + SafeError(error));
        }

        Task all = Task.WhenAll(eventsTask, logTask);
        Task winner = await Task.WhenAny(all, Task.Delay(FinalizationWait)).ConfigureAwait(false);
        if (winner != all)
        {
            timeout.Cancel();
        }

        ThrowIfFatal(eventsTask);
        ThrowIfFatal(logTask);
        EvidenceEvents? evidence = null;
        string eventStatus = "TimedOut";
        if (eventsTask.IsCompletedSuccessfully)
        {
            evidence = eventsTask.Result;
            eventStatus = DiagnosticValues.Status(evidence.Status);
        }
        else if (eventsTask.IsFaulted)
        {
            eventStatus = "Unavailable";
        }

        string logStatus = logTask.IsCompletedSuccessfully
            ? DiagnosticValues.Status(logTask.Result)
            : logTask.IsFaulted ? "Unavailable" : "TimedOut";
        var events = evidence?.Events.Take(MaximumEvidenceEvents).Select(item => new RecordedEvidenceEvent(
            item.Time.UtcDateTime,
            DiagnosticValues.Text(item.Provider),
            item.Id,
            item.Level)).ToArray() ?? [];
        return new(eventStatus, logStatus, events);
    }

    private string CreateBundle()
    {
        string bundlePath = Path.Combine(DirectoryPath, "connection-recording.zip");
        string temporaryPath = bundlePath + ".tmp";
        using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
        {
            foreach (string name in new[] { "report.txt", "timeline.jsonl", "summary.txt", "events.json" })
            {
                string source = Path.Combine(DirectoryPath, name);
                if (File.Exists(source))
                {
                    archive.CreateEntryFromFile(source, name, CompressionLevel.Optimal);
                }
            }
        }
        File.Move(temporaryPath, bundlePath, overwrite: true);
        return bundlePath;
    }

    private string BuildSummary(bool completed, TimeSpan recordedElapsed, int samples, int timelineEntries,
        bool timelineFull, EvidenceSummary evidence)
    {
        int blackouts;
        int displayChanges;
        int deviceChanges;
        lock (stateLock)
        {
            blackouts = acceptedBlackouts;
            displayChanges = acceptedDisplayChanges;
            deviceChanges = acceptedDeviceChanges;
        }
        return string.Join(Environment.NewLine, new[]
        {
            "OpenBlade HDMI connection recording",
            $"Started UTC: {startedUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss.fff}",
            $"Elapsed: {recordedElapsed.TotalSeconds:0.000} seconds",
            $"State: {(completed ? "Completed" : "Stopped")}",
            $"Live samples: {samples}",
            $"Blackout markers: {blackouts}",
            $"Display-change markers: {displayChanges}",
            $"Device-change markers: {deviceChanges}",
            $"Timeline entries: {timelineEntries}",
            $"Timeline limit reached: {(timelineFull ? "Yes" : "No")}",
            $"Windows event evidence: {evidence.Status}",
            $"Private display log: {evidence.DisplayLogStatus}",
            "HDMI telemetry: see selected-display SCDC availability in timeline.jsonl",
            "SCDC configuration and lock flags do not prove a stable negotiated link",
            "Active DSC: Unknown (not exposed by the public API)",
            "The ZIP is sanitized for sharing. The private display log remains outside the ZIP.",
        });
    }

    private static async Task<LiveReadResult> ReadLiveAsync(
        long owner, Func<LiveDisplayScan> reader, TimeSpan wait, CancellationToken token)
    {
        if (wait <= TimeSpan.Zero)
        {
            return new(null, "TimedOut");
        }

        Task<LiveDisplayScan> task;
        lock (LiveReadLock)
        {
            if (activeLiveRead is null || activeLiveRead.Task.IsCompleted)
            {
                activeLiveRead = new(owner, Task.Run(reader));
            }
            else if (activeLiveRead.Owner != owner)
            {
                return new(null, "Busy");
            }
            task = activeLiveRead.Task;
        }

        Task winner = await Task.WhenAny(task, Task.Delay(wait, token)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (winner != task)
        {
            return new(null, "TimedOut");
        }
        try
        {
            return new(await task.ConfigureAwait(false), "Available");
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            return new(null, "Unavailable");
        }
    }

    private static void WriteJsonDurably<T>(string path, T value) =>
        WriteDurableBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    private static void WriteDurableText(string path, string value) =>
        WriteDurableBytes(path, new UTF8Encoding(false).GetBytes(value));

    private static void WriteDurableBytes(string path, byte[] value)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.Read, 4096, FileOptions.None);
        stream.Write(value);
        stream.Flush(flushToDisk: true);
    }

    private void Report(IProgress<RecordingProgress>? progress, int samples, bool finalizing = false)
    {
        if (progress is null)
        {
            return;
        }
        try
        {
            progress.Report(new(clock?.Elapsed ?? TimeSpan.Zero, samples, acceptedBlackouts, finalizing));
        }
        catch (Exception error) when (IsRecoverable(error))
        {
        }
    }

    private static string BoundText(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters] + Environment.NewLine + "[truncated]";

    private static string SafeError(Exception error) => error is IOException or UnauthorizedAccessException
        ? "the recording files could not be written"
        : "optional diagnostic evidence was unavailable";

    private static bool IsRecoverable(Exception error) => error is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static void ThrowIfFatal(Task task)
    {
        if (!task.IsFaulted || task.Exception is null)
        {
            return;
        }
        foreach (Exception error in task.Exception.Flatten().InnerExceptions)
        {
            if (!IsRecoverable(error))
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }
        }
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record PendingMarker(string Kind, DateTimeOffset Utc, TimeSpan Elapsed);
    private sealed record ActiveLiveRead(long Owner, Task<LiveDisplayScan> Task);
    private sealed record LiveReadResult(LiveDisplayScan? Scan, string Status);
    private sealed record RecordedDisplay(
        string Label,
        bool Selected,
        string Name,
        string Mode,
        string Connection,
        string Hdr,
        string VrrCapability,
        string VrrRequested,
        string VrrEnabled,
        string VrrActive,
        string Error,
        ScdcObservation Scdc);
    private sealed record RecordedEvidenceEvent(DateTime Utc, string Provider, int Id, int Level);
    private sealed record EvidenceSummary(
        string Status, string DisplayLogStatus, IReadOnlyList<RecordedEvidenceEvent> Events);
    private sealed record RecordingEntry(
        string Kind,
        DateTime Utc,
        long ElapsedMilliseconds,
        string? Status = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? SelectedConnected = null,
        IReadOnlyList<RecordedDisplay>? Displays = null)
    {
        internal static RecordingEntry Start(DateTimeOffset utc) =>
            new("start", utc.UtcDateTime, 0, "recording");
        internal static RecordingEntry Stop(DateTimeOffset utc, TimeSpan elapsed, string status) =>
            new("stop", utc.UtcDateTime, (long)elapsed.TotalMilliseconds, status);
        internal static RecordingEntry Marker(PendingMarker marker) =>
            new(marker.Kind, marker.Utc.UtcDateTime, (long)marker.Elapsed.TotalMilliseconds);
        internal static RecordingEntry Sample(DateTimeOffset utc, TimeSpan elapsed, string status,
            bool? selectedConnected, IReadOnlyList<RecordedDisplay> displays) =>
            new("sample", utc.UtcDateTime, (long)elapsed.TotalMilliseconds,
                status, selectedConnected, displays);
    }
}
