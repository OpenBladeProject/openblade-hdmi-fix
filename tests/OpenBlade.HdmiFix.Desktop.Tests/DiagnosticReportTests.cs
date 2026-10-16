using OpenBlade.HdmiFix.Desktop;
using Xunit;

namespace OpenBlade.HdmiFix.Desktop.Tests;

public sealed class DiagnosticReportTests
{
    private const string Instance = @"DISPLAY\SAM74AF\private-instance";
    private static MonitorView Selected(string connection = "Connected") => new(Instance,
        "Odyssey G95", connection, "Connection TEST", "Patch installed", "", false, false, null);
    private static LiveDisplayDiagnostics Display(string instance = Instance) => new(instance,
        "Odyssey G95", "5120 x 1440, 120 Hz", "HDMI", "active", "yes", "yes", "yes", "no");

    [Fact]
    public void ReportIncludesSystemAndDistinctCurrentVrrStatesWithoutPrivateIdentifiers()
    {
        var selected = Selected();
        var host = new HostDiagnosticSnapshot("Razer Blade 16", "10.0.26100.1234", "24H2",
            [new("NVIDIA GeForce RTX Laptop GPU", "32.0.15.8180", "NVIDIA"),
             new("Intel Graphics", "32.0.101.0000", "Intel")]);
        var timing = new SignalTimingObservation(2_376_000_000, 540_000, 11, 240_000, 1001,
            5120, 1440, 5500, 1800, 240_000, 1001, "Progressive", "SignalTiming:Available");
        var display = Display() with
        {
            SignalTiming = timing,
            ColorEncoding = "YCbCr420",
            BitsPerColorChannel = 10,
            DrivingAdapter = "NVIDIA GeForce RTX 5080 Laptop GPU",
        };
        string report = DiagnosticReport.Format(selected, [selected], host, new([display]),
            new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        Assert.Contains("Laptop model: Razer Blade 16", report);
        Assert.Contains("Windows build: 10.0.26100.1234", report);
        Assert.Contains("GPU 1 Windows driver version: 32.0.15.8180", report);
        Assert.Contains("GPU 2: Intel Graphics", report);
        Assert.Contains("Current mode: 5120 x 1440, 120 Hz", report);
        Assert.Contains("HDR: active", report);
        Assert.Contains("Display path GPU: NVIDIA GeForce RTX 5080 Laptop GPU", report);
        Assert.Contains("Color encoding: YCbCr420", report);
        Assert.Contains("Bits per color channel: 10", report);
        Assert.Contains("Signal pixel clock: 2376000000 Hz", report);
        Assert.Contains("Signal horizontal sync: 540000/11 Hz", report);
        Assert.Contains("Signal vertical sync: 240000/1001 Hz", report);
        Assert.Contains("Signal active pixels: 5120 x 1440", report);
        Assert.Contains("Signal total pixels: 5500 x 1800", report);
        Assert.Contains("Path refresh: 240000/1001 Hz", report);
        Assert.Contains("Signal scan-line ordering: Progressive", report);
        Assert.Contains("enabled (NVIDIA): yes", report);
        Assert.Contains("VRR active at capture (NVIDIA): no", report);
        Assert.DoesNotContain("private-instance", report);
    }

    [Fact]
    public void DisconnectedEntryNeverBorrowsCurrentSettingsEvenIfTheNativeSnapshotStillMatches()
    {
        var display = Display() with { DrivingAdapter = "NVIDIA private adapter", ColorEncoding = "RGB" };
        string report = DiagnosticReport.FormatLiveDisplay(Selected("Previously connected"), new([display]));
        Assert.Contains("selected saved connection is disconnected", report);
        Assert.DoesNotContain("5120 x 1440", report);
        Assert.DoesNotContain("HDR: active", report);
        Assert.DoesNotContain("NVIDIA private adapter", report);
    }

    [Fact]
    public void SameModelOnAnotherConnectionIsNotACurrentMatch()
    {
        var other = Display(@"DISPLAY\SAM74AF\different-private-instance");
        string report = DiagnosticReport.FormatLiveDisplay(Selected(), new([other]));
        Assert.Contains("no active path matches", report);
        Assert.DoesNotContain("5120 x 1440", report);
    }

    [Fact]
    public void AmbiguousPathsDoNotClaimSettings()
    {
        string report = DiagnosticReport.FormatLiveDisplay(Selected(), new([Display(), Display()]));
        Assert.Contains("multiple active paths", report);
        Assert.DoesNotContain("HDR: active", report);
    }

    [Fact]
    public void IdentityMatchIsCaseInsensitiveAndUnavailableVrrDoesNotLoseOtherFields()
    {
        var display = Display(Instance.ToLowerInvariant()) with
        {
            VrrCapability = "unknown", VrrRequested = "unknown", VrrEnabled = "unknown", VrrActive = "unknown",
            Error = "NVIDIA query unavailable",
        };
        string report = DiagnosticReport.FormatLiveDisplay(Selected(), new([display]));
        Assert.Contains("Current mode: 5120 x 1440, 120 Hz", report);
        Assert.Contains("HDR: active", report);
        Assert.Contains("enabled (NVIDIA): unknown", report);
        Assert.Contains("Live display read: NVIDIA query unavailable", report);
    }

    [Theory]
    [InlineData("C:\\Users\\private-owner")]
    [InlineData("NVIDIA\nInjected serial=private")]
    [InlineData("user@private.invalid")]
    [InlineData("http://private.invalid")]
    [InlineData("NVIDIA\u202Eprivate")]
    public void PrivatePathsAndInjectedReportLinesAreRejected(string value)
    {
        Assert.Equal("unknown", DiagnosticValues.Text(value));
        var host = new HostDiagnosticSnapshot(value, value, value, [new(value, value, value, value)], value);
        string report = host.Format();
        Assert.DoesNotContain("private", report);
        Assert.DoesNotContain("Injected", report);
    }

    [Fact]
    public void FixedNativeErrorCategoriesSurviveWithoutPrivatePaths()
    {
        Assert.Equal("HdrQuery:50; NvapiTarget:-6", DiagnosticValues.Status("HdrQuery:50; NvapiTarget:-6"));
        Assert.Equal("unknown", DiagnosticValues.Status(@"HdrQuery:C:\Users\private"));
        var display = Display() with { Error = "HdrQuery:50" };
        Assert.Contains("Live display read: HdrQuery:50",
            DiagnosticReport.FormatLiveDisplay(Selected(), new([display])));
    }
    [Fact]
    public void WrongTypesAndOversizedFieldsAreUnavailable()
    {
        Assert.Equal("unknown", DiagnosticValues.Text(new byte[128]));
        Assert.Equal("unknown", DiagnosticValues.Text(new string('A', 161)));
        Assert.Equal("unknown", DiagnosticValues.DriverVersion("581.80"));
        Assert.Equal("unknown", DiagnosticValues.WindowsBuild("Windows 11"));
    }

    [Fact]
    public void MissingSelectionStillIncludesHostDataAndPartialGpuFailures()
    {
        var host = new HostDiagnosticSnapshot("Razer Blade 16", "10.0.26100.1234", "24H2",
            [new("NVIDIA GPU", "32.0.15.8180", "NVIDIA"), new("unknown", "unknown", "unknown", "unavailable")],
            "incomplete or unavailable");
        string report = DiagnosticReport.Format(null, [], host, new([], "unavailable"), null);
        Assert.Contains("No monitor selected", report);
        Assert.Contains("GPU 1: NVIDIA GPU", report);
        Assert.Contains("GPU 2 driver read: unavailable", report);
        Assert.Contains("GPU enumeration: incomplete or unavailable", report);
    }

    [Fact]
    public void RelatedSavedConnectionsDoNotRepeatHostInventoryOrBorrowSelectedLiveState()
    {
        var selected = Selected();
        var other = Selected("Previously connected") with { InstanceId = @"DISPLAY\SAM74AF\private-other" };
        var host = new HostDiagnosticSnapshot("Razer Blade 16", "10.0.26100.1234", "24H2", []);
        string report = DiagnosticReport.Format(selected, [selected, other], host, new([Display()]), null);
        Assert.Equal(1, report.Split("Laptop model:").Length - 1);
        Assert.Equal(1, report.Split("Current mode:").Length - 1);
        Assert.DoesNotContain("private-other", report);
    }
}
