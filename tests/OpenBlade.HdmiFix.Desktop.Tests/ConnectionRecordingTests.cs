using System.IO.Compression;
using System.Text.Json;
using System.IO;

namespace OpenBlade.HdmiFix.Desktop.Tests;

using Xunit;

public sealed class ConnectionRecordingTests
{
    [Fact]
    public async Task Recording_UsesExactInstanceAfterDisconnectedBaselineAndCreatesSanitizedBundle()
    {
        using var output = new TemporaryDirectory();
        const string selectedId = @"DISPLAY\SAM74AF\private-selected-instance";
        const string otherId = @"DISPLAY\SAM74AF\private-other-instance";
        var selected = Monitor(selectedId, "Previously connected");
        int reads = 0;
        int activeReads = 0;
        int maximumActiveReads = 0;
        LiveDisplayScan Read()
        {
            int active = Interlocked.Increment(ref activeReads);
            maximumActiveReads = Math.Max(maximumActiveReads, active);
            try
            {
                int read = Interlocked.Increment(ref reads);
                if (read == 1)
                {
                    return new([]);
                }
                if (read == 2)
                {
                    throw new InvalidOperationException("recoverable reader failure");
                }
                return new([
                    Display(otherId, "Same model", "60 Hz"),
                    Display(selectedId, "Same model", "120 Hz"),
                ]);
            }
            finally
            {
                Interlocked.Decrement(ref activeReads);
            }
        }

        var recording = Create(output.Path, selected, Read,
            duration: TimeSpan.FromMilliseconds(90), interval: TimeSpan.FromMilliseconds(10));

        ConnectionRecordingResult result = await recording.RunAsync(CancellationToken.None);

        Assert.True(result.Completed, result.Message);
        Assert.True(result.BundlePath is not null, result.Message);
        Assert.True(File.Exists(result.BundlePath));
        Assert.Equal(1, maximumActiveReads);
        string timeline = File.ReadAllText(Path.Combine(result.DirectoryPath, "timeline.jsonl"));
        Assert.DoesNotContain(selectedId, timeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(otherId, timeline, StringComparison.OrdinalIgnoreCase);

        JsonElement[] samples = ReadTimeline(result.DirectoryPath)
            .Where(entry => entry.GetProperty("kind").GetString() == "sample").ToArray();
        Assert.Contains(samples, sample => sample.GetProperty("selectedConnected").ValueKind == JsonValueKind.True
            && sample.GetProperty("displays").EnumerateArray().Any(display =>
                display.GetProperty("selected").GetBoolean()
                && display.GetProperty("label").GetString() == "Selected display"
                && display.GetProperty("mode").GetString() == "120 Hz"));
        Assert.Contains(samples, sample => sample.GetProperty("status").GetString() == "Unavailable");
        Assert.True(samples.Select(sample => sample.GetProperty("elapsedMilliseconds").GetInt64())
            .SequenceEqual(samples.Select(sample => sample.GetProperty("elapsedMilliseconds").GetInt64()).Order()));
        Assert.All(samples, sample => Assert.EndsWith("Z", sample.GetProperty("utc").GetString()));

        using ZipArchive archive = ZipFile.OpenRead(result.BundlePath!);
        Assert.Equal(
            ["events.json", "report.txt", "summary.txt", "timeline.jsonl"],
            archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RequestedStop_FinalizesPartialBundleWithoutWaitingForStuckNativeRead()
    {
        using var output = new TemporaryDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<LiveDisplayScan>(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstCalls = 0;
        LiveDisplayScan StuckRead()
        {
            Interlocked.Increment(ref firstCalls);
            entered.TrySetResult();
            return release.Task.GetAwaiter().GetResult();
        }

        var first = Create(output.Path, Monitor("selected", "Previously connected"), StuckRead,
            duration: TimeSpan.FromSeconds(2), interval: TimeSpan.FromMilliseconds(20));
        using var firstStop = new CancellationTokenSource();
        Task<ConnectionRecordingResult> firstRun = first.RunAsync(firstStop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        firstStop.Cancel();

        ConnectionRecordingResult firstResult = await firstRun.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(firstResult.Completed);
        Assert.True(firstResult.BundlePath is not null, firstResult.Message);
        Assert.True(File.Exists(firstResult.BundlePath));
        Assert.Equal(1, firstCalls);

        int secondCalls = 0;
        var second = Create(output.Path, Monitor("second", "Previously connected"),
            () => { Interlocked.Increment(ref secondCalls); return new([]); },
            duration: TimeSpan.FromSeconds(1), interval: TimeSpan.FromMilliseconds(20));
        using var secondStop = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        ConnectionRecordingResult secondResult = await second.RunAsync(secondStop.Token)
            .WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            Assert.False(secondResult.Completed);
            Assert.NotNull(secondResult.BundlePath);
            Assert.Equal(0, secondCalls);
        }
        finally
        {
            release.TrySetResult(new([]));
            Assert.True(SpinWait.SpinUntil(() => release.Task.IsCompleted, TimeSpan.FromSeconds(1)));
        }
    }

    [Fact]
    public async Task ConcurrentMarkers_AreBoundedThreadSafeAndPreservedOnStop()
    {
        using var output = new TemporaryDirectory();
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LiveDisplayScan Read()
        {
            firstRead.TrySetResult();
            return new([]);
        }
        var recording = Create(output.Path, Monitor("selected", "Previously connected"), Read,
            duration: TimeSpan.FromSeconds(2), interval: TimeSpan.FromMilliseconds(20));
        using var stop = new CancellationTokenSource();
        Task<ConnectionRecordingResult> run = recording.RunAsync(stop.Token);
        await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(1));

        int accepted = 0;
        Parallel.For(0, 600, index =>
        {
            bool result = (index % 3) switch
            {
                0 => recording.MarkBlackout(),
                1 => recording.NotifyDisplayChange(),
                _ => recording.NotifyDeviceChange(),
            };
            if (result)
            {
                Interlocked.Increment(ref accepted);
            }
        });
        stop.Cancel();

        ConnectionRecordingResult result = await run.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(result.Completed);
        Assert.InRange(accepted, 1, 192);
        JsonElement[] markers = ReadTimeline(result.DirectoryPath).Where(entry =>
            entry.GetProperty("kind").GetString() is "blackout" or "display-change" or "device-change").ToArray();
        Assert.Equal(accepted, markers.Length);
        Assert.All(markers, marker =>
        {
            Assert.True(marker.GetProperty("elapsedMilliseconds").GetInt64() >= 0);
            Assert.EndsWith("Z", marker.GetProperty("utc").GetString());
        });
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("incomplete")]
    [InlineData("ambiguous")]
    public async Task UncertainReadDoesNotClaimTheSelectedDisplayDisconnected(string scenario)
    {
        using var output = new TemporaryDirectory();
        var selected = Monitor("selected", "Previously connected");
        LiveDisplayScan Read() => scenario switch
        {
            "failure" => throw new UnauthorizedAccessException(@"C:\Users\private\reader"),
            "incomplete" => new([], "BindingOpen:13", true),
            _ => new([Display("selected", "One", "60 Hz"), Display("selected", "Two", "60 Hz")]),
        };
        var recording = Create(output.Path, selected, Read,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        var result = await recording.RunAsync(CancellationToken.None);
        Assert.NotNull(result.BundlePath);
        var samples = ReadTimeline(result.DirectoryPath).Where(e => e.GetProperty("kind").GetString() == "sample").ToArray();
        Assert.NotEmpty(samples);
        Assert.All(samples, e => Assert.Equal(JsonValueKind.Null, e.GetProperty("selectedConnected").ValueKind));
        Assert.DoesNotContain("private", File.ReadAllText(Path.Combine(result.DirectoryPath, "timeline.jsonl")));
    }

    [Fact]
    public async Task OptionalEvidenceFailureKeepsTheTimelineAndPartialBundle()
    {
        using var output = new TemporaryDirectory();
        var recording = new ConnectionRecording(Monitor("selected", "Previously connected"), "Safe baseline", output.Path,
            () => new([]), (_, _) => throw new UnauthorizedAccessException("private event path"),
            (_, _) => Task.FromException<string>(new IOException("private display-log path")),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        var result = await recording.RunAsync(CancellationToken.None);
        Assert.NotNull(result.BundlePath);
        Assert.True(File.Exists(Path.Combine(result.DirectoryPath, "timeline.jsonl")));
        string events = File.ReadAllText(Path.Combine(result.DirectoryPath, "events.json"));
        Assert.Contains("Unavailable", events);
        Assert.DoesNotContain("private", events);
    }

    [Fact]
    public async Task AutomaticNotificationBurstLeavesBlackoutCapacityAndPreservesTerminalRecord()
    {
        using var output = new TemporaryDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recording = Create(output.Path, Monitor("selected", "Previously connected"),
            () => { started.TrySetResult(); return new([]); }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(5));
        var run = recording.RunAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        int automatic = Enumerable.Range(0, 300).Count(_ => recording.NotifyDeviceChange());
        int blackouts = Enumerable.Range(0, 100).Count(_ => recording.MarkBlackout());
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(128, automatic);
        Assert.Equal(64, blackouts);
        var entries = ReadTimeline(result.DirectoryPath);
        Assert.Equal(64, entries.Count(e => e.GetProperty("kind").GetString() == "blackout"));
        Assert.Equal(128, entries.Count(e => e.GetProperty("kind").GetString() == "device-change"));
        Assert.Equal("stop", entries[^1].GetProperty("kind").GetString());
        Assert.Equal("timeline-limit", entries[^1].GetProperty("status").GetString());
        Assert.True(entries.Length <= 512);
        Assert.False(result.Completed);
        Assert.NotNull(result.BundlePath);
    }

    [Fact]
    public async Task CompletionReportsSavingAndPrivateFolderFailureDoesNotLoseTheBundle()
    {
        using var output = new TemporaryDirectory();
        ConnectionRecording? recording = null;
        var progress = new InlineProgress();
        recording = new ConnectionRecording(Monitor("selected", "Previously connected"), "Safe baseline", output.Path,
            () => { File.WriteAllText(Path.Combine(recording!.DirectoryPath, "private"), "blocked optional folder"); return new([]); },
            (_, _) => new("Events:Complete", []),
            (directory, _) => { Directory.CreateDirectory(directory); return Task.FromResult("DisplayLog:Captured"); },
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        var result = await recording.RunAsync(CancellationToken.None, progress);
        Assert.NotNull(result.BundlePath);
        Assert.True(result.Completed);
        Assert.True(progress.Last!.IsFinalizing);
        Assert.False(recording.MarkBlackout());
        Assert.Contains("Unavailable", File.ReadAllText(Path.Combine(result.DirectoryPath, "events.json")));
    }

    [Fact]
    public void UserProvidedContextRetainsOrdinaryPunctuationAndBoundsControlText()
    {
        string report = MainWindow.FormatRecordingContext("LS49CG954", "M2T101 v1:02", "USB-C/HDMI 2.1 dock @ 120 Hz");
        Assert.Contains("M2T101 v1:02", report);
        Assert.Contains("USB-C/HDMI 2.1 dock @ 120 Hz", report);
        Assert.Contains("not provided", MainWindow.FormatRecordingContext("", "", ""));
        string bounded = MainWindow.FormatRecordingContext(new string('m', 200), "x\nprivate-field: value", "Cable\u200b");
        Assert.DoesNotContain(new string('m', 81), bounded);
        Assert.DoesNotContain("\u200b", bounded, StringComparison.Ordinal);
        Assert.DoesNotContain("x\nprivate-field", bounded);
    }

    [Fact]
    public async Task MarkerDuringLiveReadPrecedesTheLaterSample()
    {
        using var output = new TemporaryDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<LiveDisplayScan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recording = Create(output.Path, Monitor("selected", "Previously connected"),
            () => { entered.TrySetResult(); return release.Task.GetAwaiter().GetResult(); },
            TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(10));
        var run = recording.RunAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(recording.MarkBlackout());
        release.SetResult(new([]));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        var entries = ReadTimeline(result.DirectoryPath);
        long[] times = entries.Select(e => e.GetProperty("elapsedMilliseconds").GetInt64()).ToArray();
        Assert.Equal(times.Order(), times);
        Assert.Equal("blackout", entries[1].GetProperty("kind").GetString());
    }


    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecordingSerializesScdcAndKeepsActiveDscUnknown(bool readable)
    {
        using var output = new TemporaryDirectory();
        var selected = Monitor("selected", "Previously connected");
        var scdc = readable ? new ScdcObservation(1, 4, 4, 8, 15, true, "Scdc:Available")
            : ScdcObservation.Unknown(@"C:\Users\private\read-failure");
        var recording = Create(output.Path, selected,
            () => new([Display("selected", "Monitor", "240 Hz") with { Scdc = scdc }]),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        var result = await recording.RunAsync(CancellationToken.None);
        Assert.NotNull(result.BundlePath);
        var samples = ReadTimeline(result.DirectoryPath).Where(e => e.GetProperty("kind").GetString() == "sample"
            && e.GetProperty("displays").GetArrayLength() != 0).ToArray();
        Assert.NotEmpty(samples);
        Assert.All(samples, sample =>
        {
            var display = Assert.Single(sample.GetProperty("displays").EnumerateArray());
            var value = display.GetProperty("scdc");
            Assert.Equal("Unknown (not exposed by the public API)", value.GetProperty("activeDsc").GetString());
            Assert.Equal(readable ? "Scdc:Available" : "unknown", value.GetProperty("status").GetString());
            if (readable)
            {
                Assert.Equal(4, value.GetProperty("frlRateCode").GetInt32());
                Assert.Equal(8, value.GetProperty("gbpsPerLane").GetInt32());
                Assert.Equal(15, value.GetProperty("laneLockMask").GetInt32());
                Assert.True(value.GetProperty("trainingReady").GetBoolean());
            }
            else Assert.False(value.TryGetProperty("frlRateCode", out _));
        });
        Assert.DoesNotContain("private", File.ReadAllText(Path.Combine(result.DirectoryPath, "timeline.jsonl")));
        Assert.Contains("Active DSC: Unknown", File.ReadAllText(Path.Combine(result.DirectoryPath, "summary.txt")));
    }

    [Fact]
    public async Task ConsolidatedAmbiguousBindingKeepsRecordedConnectionUnknown()
    {
        using var output = new TemporaryDirectory();
        var recording = Create(output.Path, Monitor("selected", "Previously connected"),
            () => new([Display("selected", "Monitor", "Unknown") with { Error = "Binding:Ambiguous" }]),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        var result = await recording.RunAsync(CancellationToken.None);
        var samples = ReadTimeline(result.DirectoryPath).Where(e => e.GetProperty("kind").GetString() == "sample").ToArray();
        Assert.NotEmpty(samples);
        Assert.All(samples, e => Assert.Equal(JsonValueKind.Null, e.GetProperty("selectedConnected").ValueKind));
    }

    private sealed class InlineProgress : IProgress<RecordingProgress>
    {
        internal RecordingProgress? Last { get; private set; }
        public void Report(RecordingProgress value) => Last = value;
    }

    private static ConnectionRecording Create(string outputRoot, MonitorView selected,
        Func<LiveDisplayScan> read, TimeSpan duration, TimeSpan interval) => new(
            selected,
            "Sanitized disconnected baseline",
            outputRoot,
            read,
            (_, _) => new("Events:Complete", [new(DateTimeOffset.UtcNow, "Display", 4101, 2)]),
            (directory, _) =>
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "display-log.dat"), "private raw data");
                return Task.FromResult("DisplayLog:Captured");
            },
            duration,
            interval);

    private static MonitorView Monitor(string instanceId, string connectionLabel) => new(
        instanceId,
        "Odyssey G95SC",
        connectionLabel,
        "Connection TEST",
        "Ready",
        "",
        false,
        false,
        null);

    private static LiveDisplayDiagnostics Display(string instanceId, string name, string mode) => new(
        instanceId,
        name,
        mode,
        "HDMI",
        "active, supported, user setting enabled",
        "Yes",
        "Yes",
        "Yes",
        "No");

    private static JsonElement[] ReadTimeline(string directory) => File
        .ReadAllLines(Path.Combine(directory, "timeline.jsonl"))
        .Select(line => JsonDocument.Parse(line).RootElement.Clone())
        .ToArray();

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "OpenBlade-ConnectionRecordingTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
