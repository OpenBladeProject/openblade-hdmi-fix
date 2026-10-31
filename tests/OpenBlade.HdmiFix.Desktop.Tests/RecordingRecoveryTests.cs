using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OpenBlade.HdmiFix.Desktop.Tests;

[Collection("Connection recording")]
public sealed class RecordingRecoveryTests
{
    [Fact]
    public void AbandonedDurablePrefixPreservesMarkersAndTelemetryWithoutInventingTail()
    {
        using var output = new RecoveryDirectory();
        string session = output.Abandon();
        string extra = "{\"kind\":\"sample\",\"utc\":\"2026-10-14T00:00:01Z\",\"elapsedMilliseconds\":1000,"
            + "\"status\":\"Available\",\"selectedConnected\":true,\"instanceId\":\"private-instance\",\"displays\":[{"
            + "\"label\":\"Selected display\",\"selected\":true,\"name\":\"Monitor\",\"mode\":\"120 Hz\","
            + "\"connection\":\"HDMI\",\"hdr\":\"Yes\",\"vrrCapability\":\"Yes\",\"vrrRequested\":\"Yes\","
            + "\"vrrEnabled\":\"Yes\",\"vrrActive\":\"Unknown\",\"error\":\"\",\"rawEdid\":\"private-edid\","
            + "\"scdc\":{\"frlRateCode\":4,\"laneLockMask\":15,\"status\":\"Scdc:Available\"}}]}\r\n"
            + Entry("blackout", 1500) + "{\"kind\":\"sample\",\"utc\":\"torn";
        File.AppendAllText(Path.Combine(session, "timeline.jsonl"), extra);
        Directory.CreateDirectory(Path.Combine(session, "private"));
        File.WriteAllText(Path.Combine(session, "private", "display-log.dat"), "private raw log");
        File.WriteAllText(Path.Combine(session, "raw-edid.bin"), "private edid");
        byte[] original = File.ReadAllBytes(Path.Combine(session, "timeline.jsonl"));

        var result = RecordingRecovery.Recover(output.Path);
        var recovered = Assert.Single(result.Recordings);
        Assert.False(recovered.Completed);
        Assert.Equal(0, result.UnavailableCount);
        Assert.False(result.ScanLimitReached);
        var files = ReadBundle(recovered.BundlePath!);
        Assert.Equal(new[] { "events.json", "report.txt", "summary.txt", "timeline.jsonl" }, files.Keys.Order());
        Assert.Contains("Interrupted / Recovered", files["summary.txt"]);
        Assert.Contains("Unsaved observations cannot be recovered", files["summary.txt"]);
        Assert.Contains("Incomplete or unrecognized tail omitted: Yes", files["summary.txt"]);
        Assert.Contains("Unavailable:InterruptedRecovery", files["events.json"]);
        var entries = files["timeline.jsonl"].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.Equal(new[] { "start", "sample", "blackout" }, entries.Select(e => e.GetProperty("kind").GetString()));
        Assert.Equal(4, entries[1].GetProperty("displays")[0].GetProperty("scdc").GetProperty("frlRateCode").GetInt32());
        Assert.DoesNotContain("private", files["timeline.jsonl"]);
        Assert.DoesNotContain("rawEdid", files["timeline.jsonl"]);
        Assert.DoesNotContain("instanceId", files["timeline.jsonl"]);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(session, "timeline.jsonl")));
        Assert.Equal("private raw log", File.ReadAllText(Path.Combine(session, "private", "display-log.dat")));
        byte[] archive = File.ReadAllBytes(recovered.BundlePath!);
        Assert.Empty(RecordingRecovery.Recover(output.Path).Recordings);
        Assert.Equal(archive, File.ReadAllBytes(recovered.BundlePath!));
    }

    [Fact]
    public void ActiveSessionIsSkippedUntilItsExclusiveLockIsReleased()
    {
        using var output = new RecoveryDirectory();
        string session = output.Abandon();
        using (var active = new FileStream(Path.Combine(session, "session.lock"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            var skipped = RecordingRecovery.Recover(output.Path);
            Assert.Empty(skipped.Recordings);
            Assert.Equal(0, skipped.UnavailableCount);
        }
        Assert.Single(RecordingRecovery.Recover(output.Path).Recordings);
    }

    [Fact]
    public void InterruptedFinalizationAndRecoveryRetryPreserveOriginalFiles()
    {
        using var output = new RecoveryDirectory();
        string session = output.Abandon();
        File.AppendAllText(Path.Combine(session, "timeline.jsonl"), Entry("stop", 2000));
        File.WriteAllText(Path.Combine(session, "connection-recording.zip.tmp"), "unfinished normal zip");
        File.WriteAllText(Path.Combine(session, "recovered-recording.zip.tmp"), "unfinished recovery zip");
        File.WriteAllText(Path.Combine(session, "events.json"), "torn private event payload");
        File.WriteAllText(Path.Combine(session, "summary.txt"), "torn completed summary");
        var result = Assert.Single(RecordingRecovery.Recover(output.Path).Recordings);
        var files = ReadBundle(result.BundlePath!);
        Assert.Contains("finalization was interrupted", files["summary.txt"]);
        Assert.Contains("\"kind\":\"stop\"", files["timeline.jsonl"]);
        Assert.DoesNotContain("torn", string.Join("\n", files.Values));
        Assert.Equal("unfinished normal zip", File.ReadAllText(Path.Combine(session, "connection-recording.zip.tmp")));
        Assert.Equal("torn private event payload", File.ReadAllText(Path.Combine(session, "events.json")));
        Assert.Equal("torn completed summary", File.ReadAllText(Path.Combine(session, "summary.txt")));
        Assert.Equal("unfinished recovery zip", File.ReadAllText(Path.Combine(session, "recovered-recording.zip.tmp")));
        Assert.False(File.Exists(Path.Combine(session, "recovered-recording.zip.1.tmp")));
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("baseline")]
    [InlineData("timeline-limit")]
    [InlineData("missing-start")]
    public void UnrecoverableInputIsBoundedAndPreserved(string scenario)
    {
        using var output = new RecoveryDirectory();
        string session = output.Abandon();
        switch (scenario)
        {
            case "metadata": File.WriteAllText(Path.Combine(session, "session.json"), "{"); break;
            case "baseline": File.AppendAllText(Path.Combine(session, "report.txt"), "torn"); break;
            case "missing-start": File.WriteAllText(Path.Combine(session, "timeline.jsonl"), "{}"); break;
            default:
                using (var file = File.OpenWrite(Path.Combine(session, "timeline.jsonl")))
                    file.SetLength(RecordingRecovery.MaximumTimelineBytes + 1L);
                break;
        }
        var result = RecordingRecovery.Recover(output.Path);
        Assert.Empty(result.Recordings);
        Assert.Equal(1, result.UnavailableCount);
        Assert.False(File.Exists(Path.Combine(session, "recovered-recording.zip")));
        Assert.True(File.Exists(Path.Combine(session, "timeline.jsonl")));
    }

    [Fact]
    public void InvalidMiddleEntryStopsThePrefixRatherThanScavengingLaterObservations()
    {
        using var output = new RecoveryDirectory();
        string session = output.Abandon();
        File.AppendAllText(Path.Combine(session, "timeline.jsonl"), "{torn}\r\n" + Entry("blackout", 1000));
        var result = Assert.Single(RecordingRecovery.Recover(output.Path).Recordings);
        var files = ReadBundle(result.BundlePath!);
        Assert.Single(files["timeline.jsonl"].Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("Blackout markers: 0", files["summary.txt"]);
    }

    [Fact]
    public void MissingElapsedTimeCannotBecomeAnInventedZeroTimestamp()
    {
        using var output = new RecoveryDirectory();
        string session = output.Abandon();
        File.AppendAllText(Path.Combine(session, "timeline.jsonl"),
            "{\"kind\":\"blackout\",\"utc\":\"2026-10-14T00:00:00Z\"}\r\n");
        var result = Assert.Single(RecordingRecovery.Recover(output.Path).Recordings);
        Assert.DoesNotContain("blackout", ReadBundle(result.BundlePath!)["timeline.jsonl"]);
    }

    [Fact]
    public void ExhaustedTemporaryArchiveSlotsArePreservedAndReported()
    {
        using var output = new RecoveryDirectory();
        string session = output.Abandon();
        for (int i = 0; i < 4; i++) File.WriteAllText(Path.Combine(session,
            "recovered-recording.zip" + (i == 0 ? "" : $".{i}") + ".tmp"), "interrupted archive");
        var result = RecordingRecovery.Recover(output.Path);
        Assert.Empty(result.Recordings);
        Assert.Equal(1, result.UnavailableCount);
        Assert.Equal(4, Directory.GetFiles(session, "*.tmp").Length);
        Assert.All(Directory.GetFiles(session, "*.tmp"), path => Assert.Equal("interrupted archive", File.ReadAllText(path)));
    }

    [Fact]
    public void PerLaunchRecoveryCountIsBoundedAndLaterRunContinues()
    {
        using var output = new RecoveryDirectory();
        for (int i = 0; i < RecordingRecovery.MaximumRecoveries + 1; i++) output.Abandon();
        var first = RecordingRecovery.Recover(output.Path);
        Assert.Equal(RecordingRecovery.MaximumRecoveries, first.Recordings.Count);
        Assert.True(first.ScanLimitReached);
        Assert.Single(RecordingRecovery.Recover(output.Path).Recordings);
    }

    [Fact]
    public void EntryCountIsBoundedAndUnfamiliarHistoryDoesNotConsumeCandidateBudget()
    {
        using var output = new RecoveryDirectory();
        string session = output.Abandon();
        File.AppendAllText(Path.Combine(session, "timeline.jsonl"),
            string.Concat(Enumerable.Range(1, 600).Select(index => Entry("blackout", index))));
        var result = Assert.Single(RecordingRecovery.Recover(output.Path).Recordings);
        Assert.Equal(512, ReadBundle(result.BundlePath!)["timeline.jsonl"].Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        for (int i = 0; i <= RecordingRecovery.MaximumDirectories; i++)
            Directory.CreateDirectory(Path.Combine(output.Path, "connection-recording-unfamiliar-" + i));
        Assert.False(RecordingRecovery.Recover(output.Path).ScanLimitReached);
    }

    [Fact]
    public void CompletedHistoryDoesNotPermanentlyHideALaterInterruptedRecording()
    {
        using var output = new RecoveryDirectory();
        for (int i = 0; i < RecordingRecovery.MaximumDirectories; i++)
        {
            string completed = output.Abandon(i.ToString("x10"));
            RecordingRecovery.CreateBundle(completed, "connection-recording.zip", new Dictionary<string, byte[]>
            {
                ["report.txt"] = File.ReadAllBytes(Path.Combine(completed, "report.txt")),
                ["timeline.jsonl"] = File.ReadAllBytes(Path.Combine(completed, "timeline.jsonl")),
                ["summary.txt"] = Encoding.UTF8.GetBytes("State: Completed"),
                ["events.json"] = Encoding.UTF8.GetBytes("{\"events\":[]}"),
            });
        }
        string interrupted = output.Abandon("ffffffffff");
        var result = RecordingRecovery.Recover(output.Path);
        var recovered = Assert.Single(result.Recordings);
        Assert.Equal(interrupted, recovered.DirectoryPath);
        Assert.True(File.Exists(recovered.BundlePath));
        Assert.False(result.ScanLimitReached);
        Assert.Equal(0, result.UnavailableCount);
        Assert.Empty(RecordingRecovery.Recover(output.Path).Recordings);
    }

    [Fact]
    public void EligibleCandidateCountRemainsBoundedWhenSessionsAreActive()
    {
        using var output = new RecoveryDirectory();
        var locks = new List<FileStream>();
        try
        {
            for (int i = 0; i <= RecordingRecovery.MaximumDirectories; i++)
            {
                string session = output.Abandon();
                locks.Add(new FileStream(Path.Combine(session, "session.lock"), FileMode.Open,
                    FileAccess.ReadWrite, FileShare.None));
            }
            var result = RecordingRecovery.Recover(output.Path);
            Assert.Empty(result.Recordings);
            Assert.True(result.ScanLimitReached);
            Assert.Equal(0, result.UnavailableCount);
        }
        finally { foreach (var sessionLock in locks) sessionLock.Dispose(); }
    }

    [Fact]
    public void ElapsedScanBudgetReportsUnvisitedFoldersWithoutChangingThem()
    {
        using var output = new RecoveryDirectory();
        string interrupted = output.Abandon();
        var result = RecordingRecovery.RecoverWithinScanBudget(output.Path, TimeSpan.Zero);
        Assert.Empty(result.Recordings);
        Assert.True(result.ScanLimitReached);
        Assert.Contains("Some recording folders were not checked", result.Message);
        Assert.False(File.Exists(Path.Combine(interrupted, "recovered-recording.zip")));
        Assert.Single(RecordingRecovery.Recover(output.Path).Recordings);
    }

    [Fact]
    public void JunctionSessionCannotReadOrWriteOutsideTheRecordingRoot()
    {
        using var output = new RecoveryDirectory();
        using var outside = new RecoveryDirectory();
        string session = outside.Abandon();
        Directory.CreateDirectory(output.Path);
        string link = Path.Combine(output.Path, Path.GetFileName(session));
        // Junctions do not require Developer Mode or symbolic-link privilege.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{session}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var result = RecordingRecovery.Recover(output.Path);
            Assert.Empty(result.Recordings);
            Assert.Equal(1, result.UnavailableCount);
            Assert.False(File.Exists(Path.Combine(session, "recovered-recording.zip")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void RecoveredCoverageCountsOnlyTheSanitizedDurablePrefixAndPreservesItsWarning()
    {
        using var output = new RecoveryDirectory();
        string session = output.Abandon();
        File.AppendAllText(Path.Combine(session, "timeline.jsonl"), SavedSample(false, 1000)
            + SavedSample(null, 2000) + SavedSample(true, 3000) + "{torn}\r\n" + SavedSample(true, 4000));
        var recovery = RecordingRecovery.Recover(output.Path);
        var result = Assert.Single(recovery.Recordings);
        string summary = ReadBundle(result.BundlePath!)["summary.txt"];
        Assert.Contains("Live samples: 3", summary);
        Assert.Contains("Selected monitor captured samples: 1", summary);
        Assert.Contains("Selected monitor not captured samples: 1", summary);
        Assert.Contains("Selected monitor unverified samples: 1", summary);
        Assert.Contains("gaps", result.Warning);
        Assert.Contains(result.Warning, summary);
        Assert.Contains(result.Warning, recovery.Warning);
        Assert.Contains("Recovered recording:", recovery.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void RecoveryDoesNotClaimSelectedCaptureFromMissingOrUnknownSamples(bool? connected)
    {
        using var output = new RecoveryDirectory();
        string session = output.Abandon();
        File.AppendAllText(Path.Combine(session, "timeline.jsonl"), SavedSample(connected, 1000));
        var result = Assert.Single(RecordingRecovery.Recover(output.Path).Recordings);
        string summary = ReadBundle(result.BundlePath!)["summary.txt"];
        Assert.Contains("Selected monitor captured samples: 0", summary);
        Assert.Contains("never captured", result.Warning);
        Assert.DoesNotContain("every saved sample", summary);
    }

    [Fact]
    public void RecoveryWithNoSavedSamplesWarnsWithoutInventingCoverage()
    {
        using var output = new RecoveryDirectory();
        output.Abandon();
        var result = Assert.Single(RecordingRecovery.Recover(output.Path).Recordings);
        string summary = ReadBundle(result.BundlePath!)["summary.txt"];
        Assert.Contains("Live samples: 0", summary);
        Assert.Contains("Selected monitor captured samples: 0", summary);
        Assert.Contains("Selected monitor not captured samples: 0", summary);
        Assert.Contains("Selected monitor unverified samples: 0", summary);
        Assert.Contains("never captured", result.Warning);
        Assert.DoesNotContain("every saved sample", summary);
    }

    private static string SavedSample(bool? connected, int elapsed) => JsonSerializer.Serialize(new
    {
        kind = "sample",
        utc = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(elapsed),
        elapsedMilliseconds = elapsed,
        status = connected is null ? "Unavailable" : "Available",
        selectedConnected = connected,
        displays = Array.Empty<object>(),
    }) + "\r\n";

    private static string Entry(string kind, int elapsed) => JsonSerializer.Serialize(new
    {
        kind,
        utc = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(elapsed),
        elapsedMilliseconds = elapsed,
    }) + "\r\n";

    private static Dictionary<string, string> ReadBundle(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        return archive.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        });
    }

    private sealed class RecoveryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "OpenBlade-RecoveryTests", Guid.NewGuid().ToString("N"));

        internal string Abandon(string? suffix = null)
        {
            string directory = System.IO.Path.Combine(Path, "connection-recording-20261014-000000-"
                + (suffix ?? Guid.NewGuid().ToString("N")[..10]));
            Directory.CreateDirectory(directory);
            using var sessionLock = RecordingRecovery.StartSession(directory);
            File.WriteAllText(System.IO.Path.Combine(directory, "report.txt"), "Sanitized disconnected baseline", new UTF8Encoding(false));
            RecordingRecovery.WriteStartMetadata(directory, new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero));
            using var file = new FileStream(System.IO.Path.Combine(directory, "timeline.jsonl"), FileMode.CreateNew);
            file.Write(Encoding.UTF8.GetBytes(Entry("start", 0)));
            file.Flush(true);
            return directory;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
