using Xunit;

namespace OpenBlade.HdmiFix.Desktop.Tests;

public sealed class RecordingTargetTests
{
    private const string Old = @"DISPLAY\SAM74AF\5&123&0&UID100";
    private const string Live = @"DISPLAY\SAM74AF\5&456&0&UID101";

    [Fact]
    public void ActiveDifferentInstanceRequiresExplicitChoiceAndLeavesMutationTargetUnchanged()
    {
        var saved = Monitor(Old) with { CanPatch = true };
        var targets = RecordingTargets.FromSnapshot([saved], new([Display(Live)]));
        var active = Assert.Single(targets);
        Assert.Equal(Live, active.InstanceId);
        Assert.False(active.CanPatch);
        Assert.False(active.CanRestore);
        var choices = RecordingTargets.Choices(saved, targets);
        Assert.Equal(2, choices.Count);
        Assert.All(choices, choice => Assert.DoesNotContain("DISPLAY", choice.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Null(RecordingTargets.Default(saved, choices));
        Assert.True(saved.CanPatch);
        Assert.Equal(Old, saved.InstanceId);
        int scdcReads = 0;
        var scan = LiveDisplayDiagnosticsReader.BuildScan([
            new(1, 2, 1, "private-interface", Live, "Odyssey G95C", 5120, 1440, 240, 1, 5)],
            _ => new("inactive", ""), _ => new("No", "No", "No", "No", ""),
            recordingInstanceId: active.InstanceId,
            readScdc: _ => { scdcReads++; return new(1, 4, 4, 8, 15, true, "Scdc:Available"); });
        Assert.Equal(1, scdcReads);
        Assert.Equal<byte?>(4, Assert.Single(scan.Displays).Scdc!.FrlRateCode);
    }

    [Fact]
    public void ExistingExactActiveSelectionIsRetainedCaseInsensitively()
    {
        var saved = Monitor(Live.ToLowerInvariant());
        var active = RecordingTargets.FromSnapshot([saved], new([Display(Live)]));
        var choice = RecordingTargets.Default(saved, RecordingTargets.Choices(saved, active));
        Assert.NotNull(choice);
        Assert.True(choice.IsActive);
        Assert.Equal(Live.ToLowerInvariant(), choice.Monitor.InstanceId);
    }

    [Fact]
    public void NoActiveHdmiAllowsWaitingForTheSelectedSavedConnection()
    {
        var saved = Monitor(Old);
        var choices = RecordingTargets.Choices(saved, RecordingTargets.FromSnapshot([saved], new([])));
        Assert.False(RecordingTargets.Default(saved, choices)!.IsActive);
        Assert.Equal(Old, Assert.Single(choices).Monitor.InstanceId);
    }

    [Fact]
    public void SameNamedMonitorsRemainSeparateExplicitChoices()
    {
        var targets = RecordingTargets.FromSnapshot([], new([Display(Old), Display(Live)]));
        Assert.Equal(2, targets.Count);
        Assert.NotEqual(targets[0].InstanceLabel, targets[1].InstanceLabel);
        Assert.Null(RecordingTargets.Default(Monitor(@"DISPLAY\SAM74AF\5&third&0&UID999"),
            RecordingTargets.Choices(Monitor(@"DISPLAY\SAM74AF\5&third&0&UID999"), targets)));
    }

    [Fact]
    public void AmbiguousUnboundAndNonHdmiPathsAreNotOfferedAsActiveTargets()
    {
        var scan = new LiveDisplayScan([Display(Old), Display(Old), Display(Live) with { Error = "Binding:Ambiguous" },
            Display("") , Display(@"DISPLAY\OTHER\internal") with { Connection = "Internal" }]);
        Assert.Empty(RecordingTargets.FromSnapshot([], scan));
    }

    [Fact]
    public void OtherPathFailureDoesNotHideAnExactActiveHdmiTarget()
    {
        var target = Assert.Single(RecordingTargets.FromSnapshot([], new([Display(Live)], "BindingIncomplete", true)));
        Assert.Equal(Live, target.InstanceId);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("unknown")]
    [InlineData("UNKNOWN")]
    [InlineData("")]
    public void UnknownLiveNameRetainsExactSavedFriendlyName(string name)
    {
        var saved = Monitor(Live);
        var target = Assert.Single(RecordingTargets.FromSnapshot([saved], new([Display(Live) with { MonitorName = name }])));
        Assert.Equal(saved.Name, target.Name);
    }

    [Fact]
    public void AUsableLiveNameIsStillDisplayed()
    {
        var target = Assert.Single(RecordingTargets.FromSnapshot([Monitor(Live)],
            new([Display(Live) with { MonitorName = "Current display name" }])));
        Assert.Equal("Current display name", target.Name);
    }

    [Theory]
    [InlineData("LiveCapture:Busy", false, "LiveCapture:Busy")]
    [InlineData("Topology:Unavailable", false, "Topology:Unavailable")]
    [InlineData("", true, "BindingIncomplete")]
    [InlineData("", false, "")]
    public void SnapshotFailureIsDistinctFromAnEmptyHealthyTopology(string failure, bool unresolved, string expected)
    {
        Assert.Equal(expected, RecordingTargets.ReadFailure(new([], failure, unresolved)));
    }

    [Fact]
    public void FailedQueryHintOffersRefreshInsteadOfAnUnavailableSelection()
    {
        string hint = RecordingTargets.Hint(Monitor(Old), [], null, "LiveCapture:Busy");
        Assert.Contains("still finishing", hint);
        Assert.Contains("Refresh", hint);
        Assert.DoesNotContain("Choose", hint);
        Assert.Contains("couldn't check", RecordingTargets.Hint(null, [], null, "Topology:Unavailable"));
    }

    [Fact]
    public void EmptyAndActiveOnlyHintsDoNotInventASavedSelection()
    {
        var active = new RecordingTargetChoice(Monitor(Live), true);
        string empty = RecordingTargets.Hint(null, [], null, "");
        Assert.Contains("No monitor connections", empty);
        Assert.DoesNotContain("saved selection", empty);
        Assert.DoesNotContain("Choose", empty);
        Assert.Equal("Choose a connected HDMI monitor to record.", RecordingTargets.Hint(null, [active], null, ""));
        Assert.DoesNotContain("saved", RecordingTargets.Hint(null, [active], active, ""));
    }

    [Fact]
    public void AnUnrelatedPathFailureDoesNotOverrideTheVerifiedActiveTargetHint()
    {
        var active = new RecordingTargetChoice(Monitor(Live), true);
        Assert.DoesNotContain("couldn't check", RecordingTargets.Hint(Monitor(Old), [active], active, "BindingIncomplete"));
        Assert.DoesNotContain("Refresh", RecordingTargets.Hint(Monitor(Old), [active], active, "BindingIncomplete"));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void WaitingRetainsTheRequestedSavedInstance(bool explicitChoice, bool activeAlternatives, bool allowed)
    {
        var requested = new RecordingTargetChoice(Monitor(Old), false);
        Assert.Equal(allowed, RecordingTargets.CanWaitForSaved(requested, requested, explicitChoice, activeAlternatives));
    }

    [Fact]
    public void ActiveOrDifferentTargetsCannotSilentlyBecomeASavedWaitTarget()
    {
        var saved = new RecordingTargetChoice(Monitor(Old), false);
        Assert.False(RecordingTargets.CanWaitForSaved(saved with { IsActive = true }, saved, true, false));
        Assert.False(RecordingTargets.CanWaitForSaved(saved, new(Monitor(Live), false), true, false));
    }

    [Fact]
    public void PartialQueryHintsOfferVerifiedChoicesOrTheSelectedSavedWait()
    {
        var active = new RecordingTargetChoice(Monitor(Live), true);
        Assert.Contains("listed connected", RecordingTargets.Hint(Monitor(Old), [active], null, "BindingIncomplete"));
        var saved = new RecordingTargetChoice(Monitor(Old), false);
        Assert.Contains("start recording to wait", RecordingTargets.Hint(Monitor(Old), [active, saved], saved, "BindingIncomplete"));
    }

    [Fact]
    public void SavedScanErrorsDoNotClaimToBeAnHdmiQueryFailure()
    {
        Assert.Contains("saved monitor", RecordingTargets.FailureMessage("Scan:Unavailable"));
        Assert.DoesNotContain("HDMI", RecordingTargets.FailureMessage("Scan:Unavailable"));
        var saved = new RecordingTargetChoice(Monitor(Old), false);
        Assert.DoesNotContain("start recording", RecordingTargets.Hint(Monitor(Old), [saved], saved, "Scan:Unavailable"));
    }

    private static MonitorView Monitor(string id) => new(id, "Odyssey G95C", "Previously connected", "Connection OLD",
        "Saved entry", "", false, false, null);
    private static LiveDisplayDiagnostics Display(string id) => new(id, "Odyssey G95C", "5120 x 1440, 240 Hz",
        "HDMI", "inactive", "No", "No", "No", "No");
}
