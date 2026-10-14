using OpenBlade.HdmiFix.Core;
using Xunit;

namespace OpenBlade.HdmiFix.Desktop.Tests;

public sealed class StaleInternalPanelTests
{
    private const string Stale = @"DISPLAY\SAM74AF\private-stale";
    private const string Active = @"DISPLAY\SAM74AF\private-active";
    private const string Other = @"DISPLAY\SAM74AF\private-other";

    [Fact]
    public void ExactBaseMatchToActiveInternalPanelChangesOnlyPresentation()
    {
        var edid = Edid();
        var originalBytes = edid.ToArray();
        var fingerprint = StaleInternalPanel.BaseFingerprint(Stale, edid[..128]);
        Assert.NotNull(fingerprint);
        Assert.True(Matches(fingerprint, edid));
        var original = PatcherBackend.UnavailableMonitor(Stale, "Previously connected", "Connection PREVIEW",
            new SavedMonitorReadException(SavedMonitorReadFailure.EdidIncomplete, "Override: absent", fingerprint));
        var view = StaleInternalPanel.Describe(original);
        Assert.Equal("Stale built-in display entry", view.Status);
        Assert.Contains("built-in screen", view.Details);
        Assert.DoesNotContain("unplug", view.Details + view.ActionHint);
        Assert.False(view.CanPatch);
        Assert.False(view.CanRestore);
        Assert.Null(view.BackupPath);
        Assert.Equal(original.ReadFailure, view.ReadFailure);
        Assert.Equal(original.InspectionDetails, view.InspectionDetails);
        var report = PatcherBackend.FormatDiagnostics(view);
        Assert.Contains(view.Details, report);
        Assert.Contains("EdidIncomplete", report);
        Assert.DoesNotContain(fingerprint, report);
        Assert.DoesNotContain("private-", report);
        Assert.Equal(originalBytes, edid);
    }

    [Theory]
    [InlineData("HDMI")]
    [InlineData("DisplayPort")]
    [InlineData("Unknown")]
    public void MatchingExternalOrUnknownTargetKeepsOriginalGuidance(string connection)
    {
        var edid = Edid();
        Assert.False(Matches(StaleInternalPanel.BaseFingerprint(Stale, edid[..128])!, edid, connection));
        var view = PatcherBackend.UnavailableMonitor(Stale, "Previously connected", "Connection PREVIEW",
            new SavedMonitorReadException(SavedMonitorReadFailure.EdidIncomplete));
        Assert.Contains("Keep the monitor unplugged", view.Details);
        Assert.Contains("unplugged", view.ActionHint);
        Assert.False(view.IsStaleInternalPanel);
    }

    [Fact]
    public void SameModelWithDifferentBaseDoesNotIdentifyInternalPanel()
    {
        var edid = Edid();
        var fingerprint = StaleInternalPanel.BaseFingerprint(Stale, edid[..128])!;
        edid[12] = 1;
        Checksum(edid, 0);
        Assert.False(Matches(fingerprint, edid));
    }

    [Fact]
    public void CorruptOrMismatchedBaseCannotSupplyEvidence()
    {
        var edid = Edid();
        edid[12]++;
        Assert.Null(StaleInternalPanel.BaseFingerprint(Stale, edid));
        Assert.Null(StaleInternalPanel.BaseFingerprint(Stale, new byte[127]));
        Assert.Null(StaleInternalPanel.BaseFingerprint(@"DISPLAY\SDC4226\private", Edid()));
        edid = Edid(); edid[0] = 1; Checksum(edid, 0);
        Assert.Null(StaleInternalPanel.BaseFingerprint(Stale, edid));
    }

    [Fact]
    public void MissingAmbiguousOrIncompleteActiveEvidenceCannotClassify()
    {
        var edid = Edid();
        var fingerprint = StaleInternalPanel.BaseFingerprint(Stale, edid[..128])!;
        var states = new Dictionary<string, MonitorState>(StringComparer.OrdinalIgnoreCase)
        {
            [Active] = new(Active, edid, OverrideState.Empty),
            [Other] = new(Other, edid, OverrideState.Empty),
        };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Active, Other };
        var live = new LiveDisplayScan([Display(Active, "Internal")]);
        Assert.False(StaleInternalPanel.Matches(Stale, fingerprint, null, live, states));
        present.Add(Stale);
        Assert.False(StaleInternalPanel.Matches(Stale, fingerprint, present, live, states));
        present.Remove(Stale);
        Assert.False(StaleInternalPanel.Matches(Stale, fingerprint, present, live with { HasUnresolvedPaths = true }, states));
        Assert.False(StaleInternalPanel.Matches(Stale, fingerprint, present, live with { Failure = "Topology:Unavailable" }, states));
        Assert.False(StaleInternalPanel.Matches(Stale, fingerprint, present, new([]), states));
        Assert.False(StaleInternalPanel.Matches(Stale, fingerprint, present,
            new([Display(Active, "Internal"), Display(Other, "HDMI")]), states));
        Assert.False(StaleInternalPanel.Matches(Stale, fingerprint, present,
            new([Display(Active, "Internal"), Display(Active, "Internal")]), states));
        states.Remove(Active);
        Assert.False(StaleInternalPanel.Matches(Stale, fingerprint, present, live, states));
        Assert.False(Matches(fingerprint, edid[..128]));
    }

    [Fact]
    public void ConnectedInternalPanelHasPlainExplanationWithoutUnplugAdvice()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Active };
        var live = new LiveDisplayScan([Display(Active, "Internal")]);
        Assert.True(StaleInternalPanel.IsActiveInternal(Active, present, live));
        var original = new MonitorView(Active, "Panel", "Connected", "Connection PREVIEW",
            "Unsupported configuration", "HDMI Forum block absent", false, false, null);
        var view = StaleInternalPanel.DescribeConnected(original);
        Assert.Equal("Built-in display", view.Status);
        Assert.Contains("internal screen", view.Details);
        Assert.DoesNotContain("unplug", view.Details + view.ActionHint);
        Assert.False(view.CanPatch);
        Assert.False(view.CanRestore);
        Assert.Contains(view.Details, PatcherBackend.FormatDiagnostics(view));
        Assert.False(StaleInternalPanel.IsActiveInternal(Stale, present, live));
        Assert.False(StaleInternalPanel.IsActiveInternal(Active, null, live));
        Assert.False(StaleInternalPanel.IsActiveInternal(Active, present, live with { HasUnresolvedPaths = true }));
        Assert.False(StaleInternalPanel.IsActiveInternal(Active, present, live with { Failure = "Topology:Unavailable" }));
        Assert.False(StaleInternalPanel.IsActiveInternal(Active, present, new([Display(Active, "HDMI")])));
        Assert.False(StaleInternalPanel.IsActiveInternal(Active, present, new([Display(Active, "Internal"), Display(Active, "Internal")])));
    }

    private static bool Matches(string fingerprint, byte[] edid, string connection = "Internal") =>
        StaleInternalPanel.Matches(Stale, fingerprint,
            new(StringComparer.OrdinalIgnoreCase) { Active },
            new([Display(Active, connection)]),
            new Dictionary<string, MonitorState>(StringComparer.OrdinalIgnoreCase) { [Active] = new(Active, edid, OverrideState.Empty) });

    private static LiveDisplayDiagnostics Display(string id, string connection) =>
        new(id, "Panel", "Unknown", connection, "Unknown", "Unknown", "Unknown", "Unknown", "Unknown");

    private static byte[] Edid()
    {
        var bytes = new byte[256];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
        bytes[8] = 0x4C; bytes[9] = 0x2D; bytes[10] = 0xAF; bytes[11] = 0x74;
        bytes[18] = 1; bytes[19] = 4; bytes[126] = 1;
        bytes[128] = 2; bytes[129] = 3;
        Checksum(bytes, 0); Checksum(bytes, 128);
        return bytes;
    }

    private static void Checksum(byte[] bytes, int offset) =>
        bytes[offset + 127] = unchecked((byte)-bytes.Skip(offset).Take(127).Sum(x => (int)x));
}
