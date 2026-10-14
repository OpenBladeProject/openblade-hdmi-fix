using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenBlade.HdmiFix.Desktop;

internal sealed record RecordingRecoveryResult(
    IReadOnlyList<ConnectionRecordingResult> Recordings, int UnavailableCount, bool ScanLimitReached)
{
    internal string Message => string.Join(" ", new[]
    {
        Recordings.Count == 0 ? "" : Recordings.Count == 1
            ? "Recovered an interrupted recording. Open recording folder to get the partial ZIP."
            : $"Recovered {Recordings.Count} interrupted recordings. Open recording folder to get the partial ZIPs.",
        UnavailableCount == 0 ? "" : UnavailableCount == 1
            ? "One recording could not be recovered; its saved files were preserved."
            : $"{UnavailableCount} recordings could not be recovered; their saved files were preserved.",
        ScanLimitReached ? "Some recording folders were not checked. Open recording folder to inspect the saved files." : "",
    }.Where(value => value.Length != 0));
}

internal static class RecordingRecovery
{
    internal const int MaximumDirectories = 128;
    internal const int MaximumRecoveries = 16;
    internal const int MaximumTimelineBytes = 4 * 1024 * 1024;
    private const int MaximumReportBytes = 1024 * 1024;
    private static readonly TimeSpan MaximumScanTime = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] BundleEntries = ["report.txt", "timeline.jsonl", "summary.txt", "events.json"];

    internal static FileStream StartSession(string directory)
    {
        CheckPath(directory);
        return new FileStream(Path.Combine(directory, "session.lock"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
    }

    internal static void WriteStartMetadata(string directory, DateTimeOffset startedUtc)
    {
        byte[] report = ReadBounded(Path.Combine(directory, "report.txt"), MaximumReportBytes);
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new StartMetadata(
            1, startedUtc, Convert.ToHexString(SHA256.HashData(report))));
        using var file = new FileStream(Path.Combine(directory, "session.json"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read);
        file.Write(metadata);
        file.Flush(true);
    }

    // No device enumeration, native telemetry, or Windows event queries run here.
    // The session lock is held through publication, including across app instances.
    internal static RecordingRecoveryResult Recover(string outputRoot) => RecoverWithinScanBudget(outputRoot, MaximumScanTime);

    internal static RecordingRecoveryResult RecoverWithinScanBudget(string outputRoot, TimeSpan scanBudget)
    {
        var recovered = new List<ConnectionRecordingResult>();
        int unavailable = 0;
        bool limit = false;
        try
        {
            string root = Path.GetFullPath(outputRoot);
            if (!Directory.Exists(root)) return new(recovered, 0, false);
            CheckPath(root);
            var scanClock = Stopwatch.StartNew();
            int candidates = 0;
            int attempted = 0;
            foreach (string directory in Directory.EnumerateDirectories(root, "connection-recording-*",
                SearchOption.TopDirectoryOnly))
            {
                if (scanClock.Elapsed >= scanBudget)
                {
                    limit = true;
                    break;
                }
                if (!Regex.IsMatch(Path.GetFileName(directory),
                    @"^connection-recording-\d{8}-\d{6}-[a-f0-9]{10}$", RegexOptions.CultureInvariant)) continue;
                try
                {
                    CheckPath(directory);
                    string lockPath = Path.Combine(directory, "session.lock");
                    if (!File.Exists(lockPath)) continue;
                    // Completed and unfamiliar history must not consume the candidate
                    // budget and permanently hide a later interrupted recording.
                    if (HasPublishedBundle(directory)) continue;
                    if (++candidates > MaximumDirectories)
                    {
                        limit = true;
                        break;
                    }
                    CheckPath(lockPath);
                    FileStream sessionLock;
                    try
                    {
                        sessionLock = new FileStream(lockPath, FileMode.Open,
                            FileAccess.ReadWrite, FileShare.None);
                    }
                    catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
                    {
                        continue; // Another recorder/recovery owns this session.
                    }
                    using (sessionLock)
                    {
                        // Both names are published only after closing and flushing a complete ZIP.
                        if (HasPublishedBundle(directory)) continue;
                        if (++attempted > MaximumRecoveries)
                        {
                            limit = true;
                            break;
                        }
                        // Limit time spent walking history independently of the already
                        // bounded deep recovery work, which may need to flush an archive.
                        scanClock.Stop();
                        try { recovered.Add(RecoverSession(directory)); }
                        finally { scanClock.Start(); }
                    }
                }
                catch (Exception error) when (IsFileFailure(error))
                {
                    unavailable++;
                }
            }
        }
        catch (Exception error) when (IsFileFailure(error))
        {
            unavailable++;
        }
        return new(recovered, unavailable, limit);
    }

    private static bool HasPublishedBundle(string directory) =>
        File.Exists(Path.Combine(directory, "connection-recording.zip"))
        || File.Exists(Path.Combine(directory, "recovered-recording.zip"));

    private static ConnectionRecordingResult RecoverSession(string directory)
    {
        StartMetadata metadata = JsonSerializer.Deserialize<StartMetadata>(
            ReadBounded(Path.Combine(directory, "session.json"), 4096)) ?? throw new JsonException();
        if (metadata.Version != 1 || metadata.StartedUtc == default) throw new JsonException();
        byte[] report = ReadBounded(Path.Combine(directory, "report.txt"), MaximumReportBytes);
        if (Convert.ToHexString(SHA256.HashData(report)) != metadata.ReportSha256)
            throw new InvalidDataException("Incomplete baseline.");
        byte[] original = ReadBounded(Path.Combine(directory, "timeline.jsonl"), MaximumTimelineBytes);
        var timeline = new StringBuilder();
        int offset = 0;
        int entries = 0;
        int samples = 0;
        int blackouts = 0;
        long elapsed = 0;
        bool stopped = false;
        // Only complete newline-terminated entries belong to the flushed prefix.
        // Invalid/torn content ends the prefix; later lines are never scavenged.
        while (offset < original.Length && entries < 512 && !stopped)
        {
            int newline = Array.IndexOf(original, (byte)'\n', offset);
            if (newline < 0 || newline - offset > 256 * 1024) break;
            try
            {
                string line = Utf8.GetString(original, offset, newline - offset).TrimEnd('\r');
                string sanitized = ConnectionRecording.SanitizeRecoveredEntry(line, metadata.StartedUtc,
                    entries == 0, elapsed, out long nextElapsed, out string kind);
                timeline.Append(sanitized).Append("\r\n");
                entries++;
                elapsed = nextElapsed;
                if (kind == "sample") samples++;
                if (kind == "blackout") blackouts++;
                stopped = kind == "stop";
                offset = newline + 1;
            }
            catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException)
            {
                break;
            }
        }
        if (entries == 0) throw new InvalidDataException("No durable start entry.");
        string summary = string.Join("\r\n", new[]
        {
            "OpenBlade HDMI connection recording",
            "State: Interrupted / Recovered",
            $"Started UTC: {metadata.StartedUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss.fff}",
            $"Last saved elapsed time: {elapsed / 1000d:0.000} seconds",
            $"Timeline entries recovered: {entries}",
            $"Live samples: {samples}",
            $"Blackout markers: {blackouts}",
            $"Saved stop entry: {(stopped ? "Present (finalization was interrupted)" : "Absent")}",
            $"Incomplete or unrecognized tail omitted: {(offset < original.Length ? "Yes" : "No")}",
            "The interruption time and cause are unknown. Unsaved observations cannot be recovered.",
            "Windows event evidence: Unavailable (not collected during recovery)",
            "Private display log: Not included; any existing log remains in the original folder",
            "HDMI telemetry: only saved observations in timeline.jsonl; no recovery hardware reads",
            "SCDC configuration and lock flags do not prove a stable negotiated link",
            "Active DSC: Unknown (not exposed by the public API)",
            "Original files are preserved. The recovered ZIP contains only the sanitized baseline and saved timeline prefix.",
        });
        var contents = new Dictionary<string, byte[]>
        {
            ["report.txt"] = report,
            ["timeline.jsonl"] = Utf8.GetBytes(timeline.ToString()),
            ["summary.txt"] = Utf8.GetBytes(summary),
            ["events.json"] = Utf8.GetBytes("{\"status\":\"Unavailable:InterruptedRecovery\",\"displayLogStatus\":\"NotIncluded\",\"events\":[]}"),
        };
        string bundle = CreateBundle(directory, "recovered-recording.zip", contents);
        return new(directory, bundle, false, "Interrupted recording recovered. A partial shareable bundle is ready.");
    }

    internal static string CreateBundle(string directory, string name, IReadOnlyDictionary<string, byte[]> contents)
    {
        if (name is not ("connection-recording.zip" or "recovered-recording.zip"))
            throw new ArgumentException("Unknown bundle name.", nameof(name));
        CheckPath(directory);
        string destination = Path.Combine(directory, name);
        CheckPath(destination);
        string? temporary = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            string candidate = destination + (attempt == 0 ? "" : $".{attempt}") + ".tmp";
            CheckPath(candidate);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                temporary = candidate;
                break;
            }
        }
        if (temporary is null) throw new IOException("Temporary archive retry limit reached.");
        // Preserve unfinished archives as evidence, with a bounded number of retry slots.
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (string entryName in BundleEntries)
                {
                    byte[] bytes = contents[entryName];
                    int maximum = entryName == "timeline.jsonl" ? MaximumTimelineBytes : MaximumReportBytes;
                    if (bytes.Length > maximum) throw new InvalidDataException("Bundle entry limit exceeded.");
                    using Stream entry = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
                    entry.Write(bytes);
                }
            }
            file.Flush(true);
        }
        File.Move(temporary, destination, overwrite: false);
        return destination;
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        CheckPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw new InvalidDataException("Recording size limit exceeded.");
        byte[] bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static void CheckPath(string path)
    {
        // Reject reparse points on every existing ancestor, not just the leaf.
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recording path contains a reparse point.");
        }
    }

    private static bool IsFileFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or ArgumentException or NotSupportedException;

    private sealed record StartMetadata(int Version, DateTimeOffset StartedUtc, string ReportSha256);
}
