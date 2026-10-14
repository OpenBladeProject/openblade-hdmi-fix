namespace OpenBlade.HdmiFix.Desktop.Tests;

using Xunit;

public sealed class LiveDisplayDiagnosticsTests
{
    [Fact]
    public void NativeLayouts_MatchPublishedWindowsAndNvApiAbi()
    {
        Assert.Equal((72, 64, 420, 36, 548, 24), LiveDisplayDiagnosticsReader.AbiSizes);
        Assert.Equal((0x00010224u, 0x00010018u), LiveDisplayDiagnosticsReader.NvApiVersions);
    }

    [Fact]
    public void BuildScan_PreservesExactInterfaceToInstanceBinding()
    {
        var first = Candidate("monitor-path-1", @"DISPLAY\SAM1111\first", 1, "Odyssey G95SC");
        var second = Candidate("monitor-path-2", @"DISPLAY\SAM1111\second", 2, "Odyssey G95SC");

        LiveDisplayScan result = LiveDisplayDiagnosticsReader.BuildScan([first, second],
            _ => new("active, supported, user setting enabled", ""),
            _ => new("Yes", "Yes", "Yes", "No", ""));

        Assert.Collection(result.Displays,
            display => Assert.Equal(@"DISPLAY\SAM1111\first", display.InstanceId),
            display => Assert.Equal(@"DISPLAY\SAM1111\second", display.InstanceId));
        Assert.All(result.Displays, display =>
        {
            Assert.Equal("5120 x 1440, 120 Hz", display.Mode);
            Assert.Equal("HDMI", display.Connection);
            Assert.Equal("Odyssey G95SC", display.MonitorName);
        });
    }

    [Fact]
    public void BuildScan_CloneTargetsRemainSeparateWhenExactInstancesDiffer()
    {
        var left = Candidate("clone-path-left", @"DISPLAY\AAA0001\left", 7, "Left");
        var right = Candidate("clone-path-right", @"DISPLAY\BBB0002\right", 8, "Right");

        LiveDisplayScan result = LiveDisplayDiagnosticsReader.BuildScan([left, right],
            _ => new("inactive, supported, user setting disabled", ""),
            _ => new("Yes", "No", "Yes", "No", ""));

        Assert.Equal(2, result.Displays.Count);
        Assert.DoesNotContain(result.Displays, display => display.Error.Contains("Ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildScan_DuplicateInstanceFailsClosedWithoutPickingAPath()
    {
        var first = Candidate("path-a", @"DISPLAY\SAM1111\same", 3, "First");
        var second = Candidate("path-b", @"DISPLAY\SAM1111\same", 4, "Second");

        LiveDisplayScan result = LiveDisplayDiagnosticsReader.BuildScan([first, second],
            _ => throw new InvalidOperationException("must not query an ambiguous target"),
            _ => throw new InvalidOperationException("must not query an ambiguous target"));

        LiveDisplayDiagnostics display = Assert.Single(result.Displays);
        Assert.Equal("Unknown", display.Mode);
        Assert.Equal("Unknown", display.Hdr);
        Assert.Equal("Unknown", display.VrrEnabled);
        Assert.Equal("Binding:Ambiguous", display.Error);
    }

    [Fact]
    public void BuildScan_HdrFailurePreservesModeAndVrrFields()
    {
        LiveDisplayScan result = LiveDisplayDiagnosticsReader.BuildScan(
            [Candidate("path", @"DISPLAY\SAM1111\one", 1, "Odyssey")],
            _ => new("Unknown", "HdrQuery:50"),
            _ => new("Yes", "No", "Yes", "No", ""));

        LiveDisplayDiagnostics display = Assert.Single(result.Displays);
        Assert.Equal("Unknown", display.Hdr);
        Assert.Equal("5120 x 1440, 120 Hz", display.Mode);
        Assert.Equal("Yes", display.VrrCapability);
        Assert.Equal("Yes", display.VrrEnabled);
        Assert.Equal("HdrQuery:50", display.Error);
    }

    [Fact]
    public void DecodeHdr_SeparatesCapabilityUserSettingAndActiveMode()
    {
        Assert.Equal("active, supported, user setting enabled",
            LiveDisplayDiagnosticsReader.DecodeHdr(0x32, 2).Value);
        Assert.Equal("inactive, supported, user setting enabled",
            LiveDisplayDiagnosticsReader.DecodeHdr(0x30, 0).Value);
        Assert.Equal("inactive, supported, user setting disabled",
            LiveDisplayDiagnosticsReader.DecodeHdr(0x10, 0).Value);
        Assert.Equal("inactive, not supported, user setting disabled",
            LiveDisplayDiagnosticsReader.DecodeHdr(0, 0).Value);

        LiveDisplayDiagnosticsReader.HdrCapture invalid = LiveDisplayDiagnosticsReader.DecodeHdr(0x20, 0);
        Assert.Equal("Unknown", invalid.Value);
        Assert.Equal("Hdr:InvalidData", invalid.Error);
    }

    [Fact]
    public void DecodeVrr_SeparatesCapabilityPolicyEnabledAndTransientActivity()
    {
        LiveDisplayDiagnosticsReader.VrrCapture result = LiveDisplayDiagnosticsReader.DecodeVrr(0, 1, 0x17);

        Assert.Equal("Yes", result.Capability);
        Assert.Equal("Yes", result.Requested);
        Assert.Equal("Yes", result.Enabled);
        Assert.Equal("Yes", result.Active);
    }

    [Theory]
    [InlineData(-6, 0, "NvapiTarget:-6")]
    [InlineData(0, 0, "NvapiTargetCount")]
    [InlineData(0, 2, "NvapiTargetCount:2")]
    public void DecodeVrr_NonNvidiaFailureAndAmbiguityReturnUnknown(
        int status, uint displayCount, string error)
    {
        LiveDisplayDiagnosticsReader.VrrCapture result = LiveDisplayDiagnosticsReader.DecodeVrr(status, displayCount, 0x1F);

        Assert.Equal("Unknown", result.Capability);
        Assert.Equal("Unknown", result.Requested);
        Assert.Equal("Unknown", result.Enabled);
        Assert.Equal("Unknown", result.Active);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void BuildScan_FractionalRefreshUsesBoundedInvariantText()
    {
        var candidate = Candidate("path", @"DISPLAY\SAM1111\one", 1, "Odyssey") with
        {
            RefreshNumerator = 120000,
            RefreshDenominator = 1001,
        };

        LiveDisplayScan result = LiveDisplayDiagnosticsReader.BuildScan([candidate],
            _ => new("Unknown", "HdrQuery:50"),
            _ => LiveDisplayDiagnosticsReader.VrrCapture.Unknown("Nvapi:Unavailable"));

        Assert.Equal("5120 x 1440, 119.88 Hz", Assert.Single(result.Displays).Mode);
    }

    [Theory]
    [InlineData("Odyssey G95SC", "Odyssey G95SC")]
    [InlineData("Odyssey\r\nG95SC\t", "Odyssey G95SC")]
    [InlineData("\r\n\t", "Unknown")]
    public void SanitizeName_RemovesDiagnosticLineInjection(string value, string expected)
    {
        Assert.Equal(expected, LiveDisplayDiagnosticsReader.SanitizeName(value));
    }

    [Fact]
    public void UnresolvedPathPreservesNumericBindingFailureWithoutAttributingItsSettings()
    {
        var unresolved = Candidate("private-interface-path", "", 1, "Unbound") with { Error = "BindingOpen:13" };
        var scan = LiveDisplayDiagnosticsReader.BuildScan([unresolved],
            _ => new("active", ""), _ => new("Yes", "Yes", "Yes", "Yes", ""));
        var selected = new MonitorView(@"DISPLAY\SAM74AF\fixture", "Odyssey", "Connected", "Connection TEST",
            "Patch installed", "", false, false, null);
        string report = DiagnosticReport.FormatLiveDisplay(selected, scan);
        Assert.True(scan.HasUnresolvedPaths);
        Assert.Contains("active display matching was incomplete", report);
        Assert.Contains("BindingOpen:13", report);
        Assert.DoesNotContain("no active path matches", report);
        Assert.DoesNotContain("5120 x 1440", report);
        Assert.DoesNotContain("private-interface-path", report);
    }

    [Fact]
    public void PartialBindingFailuresKeepTheIndependentExactMatchReadable()
    {
        var resolved = Candidate("private-valid-path", @"DISPLAY\SAM74AF\fixture", 1, "Odyssey");
        var unresolved = Candidate("private-invalid-path", "", 2, "Unbound") with { Error = "BindingDetail:2" };
        var scan = LiveDisplayDiagnosticsReader.BuildScan([resolved, unresolved],
            _ => new("active", ""), _ => new("Yes", "Yes", "Yes", "No", ""));
        var selected = new MonitorView(resolved.InstanceId, "Odyssey", "Connected", "Connection TEST",
            "Patch installed", "", false, false, null);
        string report = DiagnosticReport.FormatLiveDisplay(selected, scan);
        Assert.Contains("Current mode: 5120 x 1440, 120 Hz", report);
        Assert.Contains("HDR: active", report);
        Assert.Contains("Other active display reads: BindingIncomplete; BindingDetail:2", report);
        Assert.DoesNotContain("private-", report);
    }

    [Fact]
    public void UnresolvedFailureSummaryIsBoundedAndRejectsPrivateText()
    {
        var candidates = Enumerable.Range(1, 64).Select(i => Candidate("private-path", "", (uint)i, "Unbound") with
            { Error = i == 1 ? @"C:\Users\private-owner" : $"BindingOpen:{i}" }).ToArray();
        var scan = LiveDisplayDiagnosticsReader.BuildScan(candidates,
            _ => new("Unknown", ""), _ => LiveDisplayDiagnosticsReader.VrrCapture.Unknown(""));
        Assert.True(scan.Failure.Length < 160);
        Assert.DoesNotContain("private", scan.Failure);
        Assert.Contains("BindingIncomplete", scan.Failure);
    }

    [Theory]
    [InlineData(18, "DisplayPort USB tunnel")]
    [InlineData(10, "DisplayPort")]
    [InlineData(11, "Internal")]
    [InlineData(13, "Internal")]
    [InlineData(4, "DVI")]
    [InlineData(15, "Miracast")]
    [InlineData(16, "Indirect wired")]
    [InlineData(17, "Virtual display")]
    [InlineData(-1, "Other")]
    [InlineData(123, "Unknown (technology 123)")]
    public void RecognizedConnectionTypesHaveAccurateLabels(int technology, string expected)
    {
        var candidate = Candidate("path", @"DISPLAY\SAM74AF\fixture", 1, "Odyssey") with { OutputTechnology = technology };
        var scan = LiveDisplayDiagnosticsReader.BuildScan([candidate],
            _ => new("Unknown", ""), _ => LiveDisplayDiagnosticsReader.VrrCapture.Unknown(""));
        Assert.Equal(expected, Assert.Single(scan.Displays).Connection);
    }


    [Fact]
    public void ScdcIsRequestedOnlyForTheSelectedInstanceDuringRecording()
    {
        const string selectedId = @"DISPLAY\SAM1111\selected";
        var selected = Candidate("selected-path", selectedId, 1, "Selected");
        var other = Candidate("other-path", @"DISPLAY\SAM1111\other", 2, "Same model");
        int reads = 0;
        ScdcObservation Read(LiveDisplayDiagnosticsReader.DisplayCandidate candidate)
        {
            Assert.Equal(selectedId, candidate.InstanceId);
            reads++;
            return new(1, 4, 4, 8, 15, true, "Scdc:Available");
        }
        var refresh = LiveDisplayDiagnosticsReader.BuildScan([selected, other],
            _ => new("active", ""), _ => new("Yes", "No", "Yes", "No", ""), readScdc: Read);
        Assert.Equal(0, reads);
        Assert.All(refresh.Displays, d => Assert.Equal("Scdc:NotRequested", d.Scdc!.Status));
        var recording = LiveDisplayDiagnosticsReader.BuildScan([selected, other],
            _ => new("active", ""), _ => new("Yes", "No", "Yes", "No", ""),
            recordingInstanceId: selectedId.ToLowerInvariant(), readScdc: Read);
        Assert.Equal(1, reads);
        Assert.Equal(8, recording.Displays[0].Scdc!.GbpsPerLane);
        Assert.Null(recording.Displays[1].Scdc!.FrlRateCode);
        Assert.Equal("Scdc:NotRequested", recording.Displays[1].Scdc!.Status);
    }

    [Theory]
    [InlineData(10, "", "Scdc:NotHdmi")]
    [InlineData(5, "BindingOpen:13", "Scdc:BindingUnavailable")]
    public void ScdcSkipsNonHdmiAndInvalidBinding(int technology, string error, string expected)
    {
        var candidate = Candidate("path", "selected", 1, "Monitor") with
            { OutputTechnology = technology, Error = error };
        int calls = 0;
        var scan = LiveDisplayDiagnosticsReader.BuildScan([candidate],
            _ => new("active", ""), _ => new("Yes", "No", "Yes", "No", ""),
            recordingInstanceId: "selected", readScdc: _ => { calls++; throw new Exception(); });
        Assert.Equal(0, calls);
        Assert.Equal(expected, Assert.Single(scan.Displays).Scdc!.Status);
    }

    [Fact]
    public void ScdcSkipsAmbiguousAndUnresolvedTargets()
    {
        var first = Candidate("path1", "selected", 1, "Monitor");
        var second = Candidate("path2", "selected", 2, "Monitor");
        var unresolved = Candidate("path3", "", 3, "Monitor") with { Error = "BindingOpen:13" };
        int calls = 0;
        var scan = LiveDisplayDiagnosticsReader.BuildScan([first, second, unresolved],
            _ => new("active", ""), _ => new("Yes", "No", "Yes", "No", ""),
            recordingInstanceId: "selected", readScdc: _ => { calls++; throw new Exception(); });
        Assert.Equal(0, calls);
        Assert.Equal("Scdc:Ambiguous", scan.Displays.Single(d => d.InstanceId == "selected").Scdc!.Status);
        Assert.Null(scan.Displays.Single(d => d.InstanceId == "").Scdc!.LaneLockMask);
    }

    [Fact]
    public void ScdcReadFailurePreservesIndependentModeHdrAndVrr()
    {
        var scan = LiveDisplayDiagnosticsReader.BuildScan([Candidate("path", "selected", 1, "Monitor")],
            _ => new("active", ""), _ => new("Yes", "No", "Yes", "No", ""),
            recordingInstanceId: "selected", readScdc: _ => throw new System.IO.IOException("private driver path"));
        var display = Assert.Single(scan.Displays);
        Assert.Equal("5120 x 1440, 120 Hz", display.Mode);
        Assert.Equal("active", display.Hdr);
        Assert.Equal("Yes", display.VrrEnabled);
        Assert.Equal("Scdc:Unavailable", display.Scdc!.Status);
        Assert.Null(display.Scdc.FrlRateCode);
        Assert.Contains("Unknown", display.Scdc.ActiveDsc);
    }

    private static LiveDisplayDiagnosticsReader.DisplayCandidate Candidate(
        string path, string instance, uint target, string name) => new(
            0x12345678,
            0x10203040,
            target,
            path,
            instance,
            name,
            5120,
            1440,
            120,
            1,
            5);
}
