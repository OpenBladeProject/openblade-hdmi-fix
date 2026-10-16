using OpenBlade.HdmiFix.Core;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace OpenBlade.HdmiFix.Desktop.Tests;

[Collection("Connection recording")]
public sealed class EdidComparisonDiagnosticsTests
{
    private const string Instance = @"DISPLAY\SAM74AF\private-comparison-instance";

    [Fact]
    public void CopiedComparisonIdentifiesFullPatchedCacheWithoutEnablingRestore()
    {
        var backup = Backup();
        var state = PatchedCache(backup);
        var inventory = new BackupInventory([backup], false);
        Assert.True(inventory.HasChangedEdid(state));
        Assert.False(OverrideOperations.CanRestore(state, backup));
        string comparison = SavedEntryInspection.DescribeComparison(state, inventory, true);
        var view = View(comparison);
        string report = PatcherBackend.FormatDiagnostics(view);
        Assert.Contains("Saved EDID comparison: exact expected patch", report);
        Assert.Contains("Current override matches saved patch: yes", report);
        Assert.Contains("changed blocks none; changed bytes 0", report);
        Assert.Contains("Current EDID bytes: 512", report);
        Assert.False(view.CanPatch);
        Assert.False(view.CanRestore);
        Assert.True(inventory.HasChangedEdid(state));
        Assert.False(OverrideOperations.CanRestore(state, backup));
        AssertPrivate(report, backup);
    }

    [Fact]
    public void FailedBackupVerificationAndPrivateBackupsHaveExplicitUnavailableReasons()
    {
        var backup = Backup();
        var state = PatchedCache(backup);
        string untrusted = SavedEntryInspection.DescribeComparison(state, new([backup], false), false);
        Assert.Contains("unavailable", untrusted);
        Assert.Contains("backup folder could not be verified", untrusted);
        Assert.DoesNotContain("exact expected patch", untrusted);
        string privateBackup = SavedEntryInspection.DescribeComparison(state, new([], true), true);
        Assert.Contains("another Windows account", privateBackup);
        string own = SavedEntryInspection.DescribeComparison(state, new([backup], true), true);
        Assert.Contains("exact expected patch", own);
        string unrelated = SavedEntryInspection.DescribeComparison(state,
            new([backup with { InstanceId = @"DISPLAY\SAM74AF\another-private-instance" }], false), true);
        Assert.Contains("NoEligibleBackup", unrelated);
        Assert.DoesNotContain("exact expected patch", unrelated);
        AssertPrivate(untrusted + privateBackup + own + unrelated, backup);
    }

    [Fact]
    public void UnreadableSavedEntryExplainsWhyComparisonIsUnavailable()
    {
        var view = PatcherBackend.UnavailableMonitor(Instance, "Previously connected", "Connection TEST",
            new IOException(@"C:\Users\private-account\private-backup.json"));
        var report = PatcherBackend.FormatDiagnostics(view);
        Assert.Contains("Saved EDID comparison: unavailable", report);
        Assert.Contains("saved entry could not be fully read and validated", report);
        Assert.DoesNotContain("private-account", report);
        Assert.False(view.CanRestore);
    }

    [Fact]
    public async Task RecordingZipRetainsTheSameSanitizedComparisonAsCopyDetails()
    {
        var backup = Backup();
        var comparison = SavedEntryInspection.DescribeComparison(PatchedCache(backup), new([backup], false), true);
        var view = View(comparison);
        string report = PatcherBackend.FormatDiagnostics(view);
        string parent = Path.Combine(Path.GetTempPath(), "OpenBlade-ComparisonDiagnosticsTests");
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource();
        try
        {
            var recording = new ConnectionRecording(view, report, root, () => new([]),
                (_, _) => new("Events:Unavailable", []), (_, _) => Task.FromResult("DisplayLog:Unavailable"),
                TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10));
            var result = await recording.RunAsync(stop.Token, new StopAfterSample(stop)).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(result.BundlePath);
            using var zip = ZipFile.OpenRead(result.BundlePath);
            using var reader = new StreamReader(zip.GetEntry("report.txt")!.Open());
            string bundled = await reader.ReadToEndAsync();
            Assert.Equal(report, bundled);
            Assert.Contains(comparison, bundled);
            AssertPrivate(bundled, backup);
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, absolute);
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }

    private sealed class StopAfterSample(CancellationTokenSource stop) : IProgress<RecordingProgress>
    {
        public void Report(RecordingProgress value) { if (value.Samples > 0) stop.Cancel(); }
    }

    private static MonitorView View(string comparison) => new(Instance, "Synthetic monitor", "Connected", "Connection TEST",
        RecoveryMessages.ChangedStatus, RecoveryMessages.ChangedDetails, false, false, null, InspectionDetails: comparison);

    private static void AssertPrivate(string report, PatchBackup backup)
    {
        Assert.DoesNotContain(Instance, report);
        Assert.DoesNotContain("private-comparison-instance", report);
        Assert.DoesNotContain("PRIVATE-SERIAL", report);
        Assert.DoesNotContain(backup.Id, report);
        Assert.DoesNotContain(Convert.ToHexString(backup.OriginalEdid), report);
        Assert.DoesNotContain(new MonitorState(Instance, backup.OriginalEdid, backup.OriginalOverrides).EdidHash, report);
    }

    private static MonitorState PatchedCache(PatchBackup backup) => new(Instance,
        EdidPatchPlanner.Plan(backup.OriginalEdid).Patched, backup.PatchedOverrides.Copy());

    private static PatchBackup Backup()
    {
        var bytes = new byte[512];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
        bytes[8] = 0x4C; bytes[9] = 0x2D; bytes[10] = 0xAF; bytes[11] = 0x74;
        bytes[18] = 1; bytes[19] = 3; bytes[126] = 1;
        bytes[57] = 0xFF;
        Encoding.ASCII.GetBytes("PRIVATE-SERIAL").CopyTo(bytes, 59);
        bytes[128] = 2; bytes[129] = 3; bytes[130] = 21;
        new byte[] { 0xE2, 0x78, 3, 0x6D, 0xD8, 0x5D, 0xC4, 1, 0x78, 0xC8, 0x5B, 7, 0, 0, 0x8F, 0x5A, 0 }.CopyTo(bytes, 132);
        bytes[256] = 0x70; bytes[384] = 0x70;
        for (int block = 0; block < 4; block++)
            bytes[block * 128 + 127] = unchecked((byte)-bytes.Skip(block * 128).Take(127).Sum(value => (int)value));
        var plan = EdidPatchPlanner.Plan(bytes);
        Assert.Equal(PatchDisposition.Ready, plan.Disposition);
        var backup = new PatchBackup(1, Guid.NewGuid().ToString("N"), Instance, bytes, OverrideState.Empty,
            OverrideOperations.BuildPatchedState(OverrideState.Empty, plan), "Applied");
        backup.Validate();
        return backup;
    }
}
