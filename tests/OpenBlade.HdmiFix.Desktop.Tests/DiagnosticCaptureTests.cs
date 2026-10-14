using OpenBlade.HdmiFix.Desktop;
using Xunit;
using System.IO;

namespace OpenBlade.HdmiFix.Desktop.Tests;

public sealed class DiagnosticCaptureTests
{
    private static HostDiagnosticSnapshot Host() => new("Razer Blade 16", "10.0.26100.1234", "24H2",
        [new("NVIDIA GPU", "32.0.15.8180", "NVIDIA")]);
    private static LiveDisplayScan Live() => new([new(@"DISPLAY\SAM74AF\fixture", "Odyssey", "5120 x 1440, 120 Hz",
        "HDMI", "active", "Yes", "Yes", "Yes", "No")]);

    [Fact]
    public void LiveReaderFailurePreservesHealthyHostDataAndReturnsAFixedFailure()
    {
        var host = Host();
        var result = DiagnosticCapture.Read(() => host,
            () => throw new UnauthorizedAccessException(@"C:\Users\private-owner\native-error"));
        Assert.Same(host, result.Host);
        Assert.Empty(result.Live.Displays);
        Assert.Equal("LiveCapture:Unavailable", result.Live.Failure);
        Assert.NotNull(result.Captured);
        Assert.DoesNotContain("private", result.Live.Failure);
    }

    [Fact]
    public void HostFailureDoesNotSkipOrLoseLiveCapture()
    {
        var live = Live();
        var result = DiagnosticCapture.Read(() => throw new EntryPointNotFoundException("private-native-text"), () => live);
        Assert.Equal("unknown", result.Host.LaptopModel);
        Assert.Equal("capture unavailable", result.Host.AdapterFailure);
        Assert.Same(live, result.Live);
    }

    [Fact]
    public void BothOptionalReadersCanFailWithoutPreventingSubsequentSavedMonitorWork()
    {
        var result = DiagnosticCapture.Read(() => throw new InvalidOperationException("private-host"),
            () => throw new BadImageFormatException("private-driver"));
        var selected = new MonitorView(@"DISPLAY\SAM74AF\private-instance", "Odyssey", "Connected",
            "Connection TEST", "Patch installed", "", false, false, null);
        string report = DiagnosticReport.Format(selected, [selected], result.Host, result.Live, result.Captured);
        Assert.Contains("State: Patch installed", report);
        Assert.Contains("LiveCapture:Unavailable", report);
        Assert.DoesNotContain("private", report);
    }

    [Fact]
    public void FailedRefreshDoesNotReuseAFormerLiveSnapshot()
    {
        var first = DiagnosticCapture.Read(Host, Live);
        var second = DiagnosticCapture.Read(Host, () => throw new InvalidDataException("unavailable"));
        Assert.NotEmpty(first.Live.Displays);
        Assert.Empty(second.Live.Displays);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void FatalMemoryAndNativeCorruptionAreNotMaskedAsNormalDiagnosticFailures()
    {
        Assert.Throws<OutOfMemoryException>(() => DiagnosticCapture.Read(
            () => throw new OutOfMemoryException(), Live));
        Assert.Throws<AccessViolationException>(() => DiagnosticCapture.Read(Host,
            () => throw new AccessViolationException()));
    }
}
