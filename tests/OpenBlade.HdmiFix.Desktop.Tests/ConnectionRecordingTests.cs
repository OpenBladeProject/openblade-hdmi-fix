using System.IO.Compression;
using System.Text.Json;
using System.IO;

namespace OpenBlade.HdmiFix.Desktop.Tests;

using Xunit;

[Collection("Connection recording")]
public sealed class ConnectionRecordingTests
{
    [Fact]
    public async Task FirstReadyProgressRequiresDurableBaselineAndStartAndAnExclusiveSessionLock()
    {
        using var output = new TemporaryDirectory();
        using var stop = new CancellationTokenSource();
        int reads = 0;
        var recording = Create(output.Path, Monitor("selected", "Previously connected"),
            () => { reads++; return new([]); }, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10));
        bool ready = false;
        Exception? readinessError = null;
        var progress = new InlineProgress
        {
            OnReport = value =>
            {
                if (value.IsFinalizing) return;
                try
                {
                    Assert.Equal("Sanitized disconnected baseline", File.ReadAllText(Path.Combine(recording.DirectoryPath, "report.txt")));
                    Assert.True(File.Exists(Path.Combine(recording.DirectoryPath, "session.json")));
                    using var file = new FileStream(Path.Combine(recording.DirectoryPath, "timeline.jsonl"),
                        FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(file);
                    Assert.Equal("start", JsonDocument.Parse(reader.ReadToEnd()).RootElement.GetProperty("kind").GetString());
                    var recovery = RecordingRecovery.Recover(output.Path);
                    Assert.Empty(recovery.Recordings);
                    Assert.Equal(0, recovery.UnavailableCount);
                    ready = true;
                }
                catch (Exception error) { readinessError = error; }
                stop.Cancel();
            },
        };
        var result = await recording.RunAsync(stop.Token, progress);
        Assert.Null(readinessError);
        Assert.True(ready);
        Assert.Equal(0, reads);
        Assert.Contains("never captured", result.Warning);
        Assert.Contains("Selected monitor captured samples: 0", File.ReadAllText(Path.Combine(result.DirectoryPath, "summary.txt")));
        Assert.NotNull(result.BundlePath);
        Assert.Empty(RecordingRecovery.Recover(output.Path).Recordings);
    }

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

        using var stop = new CancellationTokenSource();
        var progress = new InlineProgress
        {
            OnReport = value => { if (value.Samples >= 3) stop.Cancel(); },
        };
        var recording = Create(output.Path, selected, Read,
            duration: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(10));

        ConnectionRecordingResult result = await recording.RunAsync(stop.Token, progress)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Completed);
        Assert.True(result.BundlePath is not null, result.Message);
        Assert.True(File.Exists(result.BundlePath));
        Assert.Equal(1, maximumActiveReads);
        string timeline = File.ReadAllText(Path.Combine(result.DirectoryPath, "timeline.jsonl"));
        Assert.DoesNotContain(selectedId, timeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(otherId, timeline, StringComparison.OrdinalIgnoreCase);

        JsonElement[] samples = ReadTimeline(result.DirectoryPath)
            .Where(entry => entry.GetProperty("kind").GetString() == "sample").ToArray();
        Assert.Equal(3, samples.Length);
        Assert.Equal(JsonValueKind.False, samples[0].GetProperty("selectedConnected").ValueKind);
        Assert.Empty(samples[0].GetProperty("displays").EnumerateArray());
        Assert.Equal("Unavailable", samples[1].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, samples[1].GetProperty("selectedConnected").ValueKind);
        Assert.Equal(JsonValueKind.True, samples[2].GetProperty("selectedConnected").ValueKind);
        Assert.Contains(samples[2].GetProperty("displays").EnumerateArray(), display =>
                display.GetProperty("selected").GetBoolean()
                && display.GetProperty("label").GetString() == "Selected display"
                && display.GetProperty("mode").GetString() == "120 Hz");
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
        var readerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstCalls = 0;
        LiveDisplayScan StuckRead()
        {
            Interlocked.Increment(ref firstCalls);
            entered.TrySetResult();
            try
            {
                return release.Task.GetAwaiter().GetResult();
            }
            finally
            {
                readerExited.TrySetResult();
            }
        }

        var first = Create(output.Path, Monitor("selected", "Previously connected"), StuckRead,
            duration: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(20));
        using var firstStop = new CancellationTokenSource();
        Task<ConnectionRecordingResult> firstRun = first.RunAsync(firstStop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            firstStop.Cancel();

            ConnectionRecordingResult firstResult = await firstRun.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(firstResult.Completed);
            Assert.True(firstResult.BundlePath is not null, firstResult.Message);
            Assert.True(File.Exists(firstResult.BundlePath));
            Assert.Equal(1, firstCalls);

            int secondCalls = 0;
            var second = Create(output.Path, Monitor("second", "Previously connected"),
                () => { Interlocked.Increment(ref secondCalls); return new([]); },
                duration: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(20));
            using var secondStop = new CancellationTokenSource();
            var progress = new InlineProgress
            {
                OnReport = value => { if (value.Samples > 0) secondStop.Cancel(); },
            };
            ConnectionRecordingResult secondResult = await second.RunAsync(secondStop.Token, progress)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(secondResult.Completed);
            Assert.NotNull(secondResult.BundlePath);
            Assert.Equal(0, secondCalls);
        }
        finally
        {
            release.TrySetResult(new([]));
            firstStop.Cancel();
            await Task.WhenAll(firstRun, readerExited.Task).WaitAsync(TimeSpan.FromSeconds(30));
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
            duration: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(20));
        using var stop = new CancellationTokenSource();
        Task<ConnectionRecordingResult> run = recording.RunAsync(stop.Token);
        ConnectionRecordingResult result;
        int accepted = 0;
        try
        {
            await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Parallel.For(0, 600, index =>
            {
                bool markerAccepted = (index % 3) switch
                {
                    0 => recording.MarkBlackout(),
                    1 => recording.NotifyDisplayChange(),
                    _ => recording.NotifyDeviceChange(),
                };
                if (markerAccepted)
                {
                    Interlocked.Increment(ref accepted);
                }
            });
            stop.Cancel();
            result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(30));
        }

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
        using var stop = new CancellationTokenSource();
        var progress = new InlineProgress
        {
            OnReport = value => { if (value.Samples > 0) stop.Cancel(); },
        };
        var recording = Create(output.Path, selected, Read,
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10));
        var result = await recording.RunAsync(stop.Token, progress).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.Completed);
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
        using var stop = new CancellationTokenSource();
        var progress = new InlineProgress
        {
            OnReport = value => { if (value.Samples > 0) stop.Cancel(); },
        };
        var recording = new ConnectionRecording(Monitor("selected", "Previously connected"), "Safe baseline", output.Path,
            () => new([]), (_, _) => throw new UnauthorizedAccessException("private event path"),
            (_, _) => Task.FromException<string>(new IOException("private display-log path")),
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10));
        var result = await recording.RunAsync(stop.Token, progress).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.Completed);
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
        string maximumText = new('A', 160);
        LiveDisplayDiagnostics[] displays = Enumerable.Range(0, 64).Select(index => Display(
            index == 0 ? "selected" : $"display-{index}",
            maximumText,
            maximumText) with
        {
            Connection = maximumText,
            Hdr = maximumText,
            VrrCapability = maximumText,
            VrrRequested = maximumText,
            VrrEnabled = maximumText,
            VrrActive = maximumText,
            Error = maximumText,
            Scdc = new(1, 4, 4, 8, 15, true, maximumText),
        }).ToArray();
        var recording = Create(output.Path, Monitor("selected", "Previously connected"),
            () => { started.TrySetResult(); return new(displays); },
            TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(5));
        using var stop = new CancellationTokenSource();
        var run = recording.RunAsync(stop.Token);
        int automatic = 0;
        int blackouts = 0;
        ConnectionRecordingResult result;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            automatic = Enumerable.Range(0, 300).Count(_ => recording.NotifyDeviceChange());
            blackouts = Enumerable.Range(0, 100).Count(_ => recording.MarkBlackout());
            result = await run.WaitAsync(TimeSpan.FromSeconds(75));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(30));
        }
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
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(10));
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
        Assert.Contains("Monitor Adaptive Sync menu setting: On", MainWindow.FormatRecordingContext("", "", "", "On"));
        Assert.Contains("Monitor Adaptive Sync menu setting: Unknown", MainWindow.FormatRecordingContext("", "", "", @"C:\private-setting"));
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
        var readerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recording = Create(output.Path, Monitor("selected", "Previously connected"),
            () =>
            {
                entered.TrySetResult();
                try
                {
                    return release.Task.GetAwaiter().GetResult();
                }
                finally
                {
                    readerExited.TrySetResult();
                }
            },
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20));
        using var stop = new CancellationTokenSource();
        var progress = new InlineProgress
        {
            OnReport = value => { if (release.Task.IsCompleted && value.Samples > 0) stop.Cancel(); },
        };
        var run = recording.RunAsync(stop.Token, progress);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(recording.MarkBlackout());
            release.SetResult(new([Display("selected", "Monitor", "240 Hz")]));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
            var entries = ReadTimeline(result.DirectoryPath);
            long[] times = entries.Select(e => e.GetProperty("elapsedMilliseconds").GetInt64()).ToArray();
            Assert.Equal(times.Order(), times);
            int marker = Array.FindIndex(entries, e => e.GetProperty("kind").GetString() == "blackout");
            int laterSample = Array.FindIndex(entries, e => e.GetProperty("kind").GetString() == "sample"
                && e.GetProperty("selectedConnected").ValueKind == JsonValueKind.True);
            Assert.True(marker > 0 && laterSample > marker);
        }
        finally
        {
            release.TrySetResult(new([]));
            stop.Cancel();
            await Task.WhenAll(run, readerExited.Task).WaitAsync(TimeSpan.FromSeconds(30));
        }
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
        using var stop = new CancellationTokenSource();
        var progress = new InlineProgress
        {
            OnReport = value => { if (value.Samples > 0) stop.Cancel(); },
        };
        var recording = Create(output.Path, selected,
            () => new([Display("selected", "Monitor", "240 Hz") with { Scdc = scdc }]),
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10));
        var result = await recording.RunAsync(stop.Token, progress).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.Completed);
        Assert.NotNull(result.BundlePath);
        var samples = ReadTimeline(result.DirectoryPath).Where(e => e.GetProperty("kind").GetString() == "sample"
            && e.GetProperty("displays").GetArrayLength() != 0).ToArray();
        var sample = Assert.Single(samples);
        var display = Assert.Single(sample.GetProperty("displays").EnumerateArray());
        Assert.True(display.GetProperty("selected").GetBoolean());
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
        Assert.DoesNotContain("private", File.ReadAllText(Path.Combine(result.DirectoryPath, "timeline.jsonl")));
        Assert.Contains("Active DSC: Unknown", File.ReadAllText(Path.Combine(result.DirectoryPath, "summary.txt")));
    }

    [Fact]
    public async Task ConsolidatedAmbiguousBindingKeepsRecordedConnectionUnknown()
    {
        using var output = new TemporaryDirectory();
        using var stop = new CancellationTokenSource();
        var progress = new InlineProgress
        {
            OnReport = value => { if (value.Samples > 0) stop.Cancel(); },
        };
        var recording = Create(output.Path, Monitor("selected", "Previously connected"),
            () => new([Display("selected", "Monitor", "Unknown") with { Error = "Binding:Ambiguous" }]),
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10));
        var result = await recording.RunAsync(stop.Token, progress).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.Completed);
        var samples = ReadTimeline(result.DirectoryPath).Where(e => e.GetProperty("kind").GetString() == "sample").ToArray();
        Assert.NotEmpty(samples);
        Assert.All(samples, e => Assert.Equal(JsonValueKind.Null, e.GetProperty("selectedConnected").ValueKind));
    }


    [Fact]
    public async Task SlowReadMayExceedSamplingIntervalWithoutLosingItsObservations()
    {
        using var output = new TemporaryDirectory();
        using var stop = new CancellationTokenSource();
        var progress = new InlineProgress
        {
            OnReport = value => { if (value.Samples > 0) stop.Cancel(); },
        };
        var recording = Create(output.Path, Monitor("selected", "Previously connected"),
            () =>
            {
                Thread.Sleep(100);
                return new([Display("selected", "Monitor", "240 Hz") with
                    { Scdc = new(1, 4, 4, 8, 15, true, "Scdc:Available") }]);
            }, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20));
        var result = await recording.RunAsync(stop.Token, progress).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.Completed);
        Assert.NotNull(result.BundlePath);
        var samples = ReadTimeline(result.DirectoryPath).Where(e => e.GetProperty("kind").GetString() == "sample").ToArray();
        var sample = Assert.Single(samples);
        var display = Assert.Single(sample.GetProperty("displays").EnumerateArray());
        Assert.Equal("240 Hz", display.GetProperty("mode").GetString());
        Assert.Equal(4, display.GetProperty("scdc").GetProperty("frlRateCode").GetInt32());
    }

    [Fact]
    public async Task OneSessionPreservesSignalMetadataAndOnlySafeProblemClassification()
    {
        using var output = new TemporaryDirectory();
        using var stop = new CancellationTokenSource();
        var timing = new SignalTimingObservation(1900000000, 350000, 1, 239760, 1000,
            5120, 1440, 5500, 1500, 239760, 1000, "Progressive", "SignalTiming:Available");
        var display = Display("selected", "Monitor", "239.76 Hz") with
        {
            SignalTiming = timing,
            ColorEncoding = "YCbCr444",
            BitsPerColorChannel = 10,
            DrivingAdapter = "NVIDIA GeForce RTX 5080 Laptop GPU",
        };
        var recording = new ConnectionRecording(Monitor("selected", "Connected"), "Safe baseline", output.Path,
            () => new([display]),
            (_, _) => new("Events:Complete", [
                new(DateTimeOffset.UtcNow, "Windows Error Reporting", 1001, 2, "LiveKernelEvent", "141"),
                new(DateTimeOffset.UtcNow, "Windows Error Reporting", 1001, 2, "BlueScreen", "0x00000116"),
                new(DateTimeOffset.UtcNow, "Windows Error Reporting", 1001, 2, "APPCRASH", @"C:\private"),
            ]),
            (_, _) => Task.FromResult("DisplayLog:Unavailable"),
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10));
        var progress = new InlineProgress { OnReport = value => { if (value.Samples > 0) stop.Cancel(); } };
        var result = await recording.RunAsync(stop.Token, progress).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(result.BundlePath);
        var entries = ReadTimeline(result.DirectoryPath);
        var sample = Assert.Single(entries, e => e.GetProperty("kind").GetString() == "sample");
        var observed = Assert.Single(sample.GetProperty("displays").EnumerateArray());
        Assert.Equal("YCbCr444", observed.GetProperty("colorEncoding").GetString());
        Assert.Equal(10, observed.GetProperty("bitsPerColorChannel").GetInt32());
        Assert.Equal(display.DrivingAdapter, observed.GetProperty("drivingAdapter").GetString());
        Assert.Equal(1900000000UL, observed.GetProperty("signalTiming").GetProperty("pixelClockHz").GetUInt64());
        Assert.Equal(1000, observed.GetProperty("signalTiming").GetProperty("verticalSyncDenominator").GetInt32());
        var recovered = JsonDocument.Parse(ConnectionRecording.SanitizeRecoveredEntry(sample.GetRawText(),
            entries[0].GetProperty("utc").GetDateTimeOffset(), false, 0, out _, out _)).RootElement;
        Assert.Equal(observed.GetRawText(), recovered.GetProperty("displays")[0].GetRawText());
        string events = File.ReadAllText(Path.Combine(result.DirectoryPath, "events.json"));
        var metadata = JsonDocument.Parse(events).RootElement.GetProperty("events");
        Assert.Equal("LiveKernelEvent", metadata[0].GetProperty("problemEventName").GetString());
        Assert.Equal("141", metadata[0].GetProperty("problemCode").GetString());
        Assert.Equal("0x00000116", metadata[1].GetProperty("problemCode").GetString());
        Assert.False(metadata[2].TryGetProperty("problemEventName", out _));
        Assert.DoesNotContain("private", events);
        Assert.DoesNotContain("APPCRASH", events);
    }

    [Fact]
    public void RecoveredSignalMetadataDropsPrivateTextAndKeepsIndependentValidFields()
    {
        string line = """
            {"kind":"sample","utc":"2026-10-16T00:00:01Z","elapsedMilliseconds":1000,
            "selectedConnected":true,"status":"Available","displays":[{"label":"Selected display","selected":true,
            "name":"Monitor","mode":"240 Hz","connection":"HDMI","hdr":"Unknown",
            "vrrCapability":"Unknown","vrrRequested":"Unknown","vrrEnabled":"Unknown","vrrActive":"Unknown",
            "scdc":{"status":"Scdc:NotRequested"},"colorEncoding":"C:\private","bitsPerColorChannel":999,"drivingAdapter":"C:\private",
            "signalTiming":{"pixelClockHz":1900000000,"activeWidth":5120,"activeHeight":1440,
            "verticalSyncNumerator":240,"verticalSyncDenominator":0,"scanLineOrdering":"C:\private","status":"SignalTiming:Available"}}]}
            """;
        // JSON paths must be escaped before entering the recovery reader.
        line = line.Replace(@"C:\private", @"C:\\private", StringComparison.Ordinal);
        string safe = ConnectionRecording.SanitizeRecoveredEntry(line,
            DateTimeOffset.Parse("2026-10-16T00:00:00Z"), false, 0, out _, out _);
        var display = JsonDocument.Parse(safe).RootElement.GetProperty("displays")[0];
        Assert.Equal("Unknown", display.GetProperty("colorEncoding").GetString());
        Assert.False(display.TryGetProperty("bitsPerColorChannel", out _));
        Assert.Equal("unknown", display.GetProperty("drivingAdapter").GetString());
        var timing = display.GetProperty("signalTiming");
        Assert.Equal(5120, timing.GetProperty("activeWidth").GetInt32());
        Assert.Equal(1900000000UL, timing.GetProperty("pixelClockHz").GetUInt64());
        Assert.False(timing.TryGetProperty("verticalSyncNumerator", out _));
        Assert.DoesNotContain("private", safe);
    }

    [Fact]
    public async Task ProgressTracksOnlySavedSamplesAndWarningsFollowExactCaptureTransitions()
    {
        using var output = new TemporaryDirectory();
        using var stop = new CancellationTokenSource();
        int reads = 0;
        LiveDisplayScan Read() => ++reads switch
        {
            1 or 3 => new([]),
            2 => new([Display("selected", "Monitor", "120 Hz")]),
            4 => new([Display("selected", "Monitor", "Unknown") with { Error = "Binding:Ambiguous" }]),
            _ => throw new InvalidOperationException("query failed"),
        };
        var recording = Create(output.Path, Monitor("selected", "Previously connected"), Read,
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10));
        var observations = new List<RecordingProgress>();
        Exception? progressError = null;
        var progress = new InlineProgress { OnReport = value =>
        {
            try
            {
                var saved = ReadTimeline(recording.DirectoryPath)
                    .Where(entry => entry.GetProperty("kind").GetString() == "sample").ToArray();
                Assert.Equal(saved.Length, value.Samples);
                Assert.Equal(saved.Count(entry => entry.GetProperty("selectedConnected").ValueKind == JsonValueKind.True), value.SelectedCapturedSamples);
                Assert.Equal(saved.Count(entry => entry.GetProperty("selectedConnected").ValueKind == JsonValueKind.False), value.SelectedMissingSamples);
                Assert.Equal(saved.Count(entry => entry.GetProperty("selectedConnected").ValueKind == JsonValueKind.Null), value.SelectedUnverifiedSamples);
            }
            catch (Exception error) { progressError = error; }
            observations.Add(value);
            if (value.Samples == 5) stop.Cancel();
        } };
        var result = await recording.RunAsync(stop.Token, progress).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(progressError);
        Assert.Contains("Waiting", observations[0].Warning);
        var samples = observations.Where(value => value.Samples > 0 && !value.IsFinalizing).ToArray();
        Assert.Equal(5, samples.Length);
        Assert.False(samples[0].SelectedConnected);
        Assert.Contains("No matching active connection", samples[0].Warning);
        Assert.True(samples[1].SelectedConnected);
        Assert.Empty(samples[1].Warning);
        Assert.False(samples[2].SelectedConnected);
        Assert.Equal(samples[0].Warning, samples[2].Warning);
        Assert.Null(samples[3].SelectedConnected);
        Assert.Null(samples[4].SelectedConnected);
        Assert.Equal(samples[3].Warning, samples[4].Warning);
        Assert.Contains("could not be verified", samples[4].Warning);
        Assert.DoesNotContain("No matching active connection", samples[4].Warning);
        Assert.True(observations[^1].IsFinalizing);
        Assert.Contains("gaps", observations[^1].Warning);
        Assert.Contains("gaps", result.Warning);
        string summary = File.ReadAllText(Path.Combine(result.DirectoryPath, "summary.txt"));
        Assert.Contains("Selected monitor captured samples: 1", summary);
        Assert.Contains("Selected monitor not captured samples: 2", summary);
        Assert.Contains("Selected monitor unverified samples: 2", summary);
        Assert.Contains(result.Warning, summary);
    }

    [Fact]
    public async Task UnrelatedPathFailurePreservesVerifiedSelectedCapture()
    {
        using var output = new TemporaryDirectory();
        using var stop = new CancellationTokenSource();
        var recording = Create(output.Path, Monitor("selected", "Previously connected"),
            () => new([Display("selected", "Monitor", "120 Hz")], "BindingIncomplete; BindingOpen:13", true),
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10));
        var progress = new InlineProgress { OnReport = value => { if (value.Samples > 0) stop.Cancel(); } };
        var result = await recording.RunAsync(stop.Token, progress).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, progress.Last!.SelectedCapturedSamples);
        Assert.Equal(0, progress.Last.SelectedUnverifiedSamples);
        Assert.Empty(result.Warning);
        Assert.Contains("every saved sample", File.ReadAllText(Path.Combine(result.DirectoryPath, "summary.txt")));
    }

    [Fact]
    public void SavedSampleCoverageDoesNotClaimContinuousCoverageOrSuccessWithoutSamples()
    {
        var empty = new SelectedMonitorCoverage();
        Assert.Contains("never captured", empty.Warning);
        Assert.Contains("unverified samples: 0", empty.Summary);
        Assert.DoesNotContain("every saved sample", empty.Summary);
        var captured = empty.Add(true);
        Assert.Empty(captured.Warning);
        Assert.Contains("does not establish continuous coverage", captured.Summary);
        Assert.Contains("gaps", captured.Add(null).Warning);
        Assert.Contains("gaps", captured.Add(false).Warning);
    }

    private sealed class InlineProgress : IProgress<RecordingProgress>
    {
        internal RecordingProgress? Last { get; private set; }
        internal Action<RecordingProgress>? OnReport { get; init; }
        public void Report(RecordingProgress value)
        {
            Last = value;
            OnReport?.Invoke(value);
        }
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

    private static JsonElement[] ReadTimeline(string directory)
    {
        using var file = new FileStream(Path.Combine(directory, "timeline.jsonl"), FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
    }

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
