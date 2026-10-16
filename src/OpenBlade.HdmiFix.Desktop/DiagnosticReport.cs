using System.Globalization;

namespace OpenBlade.HdmiFix.Desktop;

internal static class DiagnosticReport
{
    internal static string Format(MonitorView? selected, IReadOnlyList<MonitorView> monitors,
        HostDiagnosticSnapshot host, LiveDisplayScan live, DateTimeOffset? captured)
    {
        string saved = PatcherBackend.FormatRelatedDiagnostics(selected, monitors);
        int headerEnd = saved.IndexOf('\n');
        var section = new List<string>
        {
            captured is { } time
                ? "Captured UTC: " + time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "Captured UTC: unknown",
            host.Format(),
        };
        if (selected is not null) section.Add(FormatLiveDisplay(selected, live));
        string details = string.Join("\n", section);
        return headerEnd >= 0 ? saved[..headerEnd] + "\n" + details + "\n\n" + saved[(headerEnd + 1)..]
            : saved + "\n" + details;
    }

    internal static string FormatLiveDisplay(MonitorView selected, LiveDisplayScan live)
    {
        // Full instance equality is required. Never borrow data from a same-model
        // connection, a different HDMI port, or a previous connected capture.
        var matches = live.Displays.Where(d => d.InstanceId.Equals(selected.InstanceId, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        string unavailable = selected.ConnectionLabel == "Previously connected"
            ? "unavailable (selected saved connection is disconnected)"
            : matches.Length > 1 ? "unknown (multiple active paths match this connection)"
            : live.HasUnresolvedPaths ? "unknown (active display matching was incomplete)"
            : live.Failure.Length != 0 ? "unknown (live display query unavailable)"
            : "unavailable (no active path matches this saved connection)";
        if (selected.ConnectionLabel == "Previously connected" || matches.Length != 1)
        {
            string state = unavailable.StartsWith("unknown", StringComparison.Ordinal) ? "unknown" : "unavailable";
            return "Live display settings: " + unavailable + "\nLive monitor name: " + state
                + "\nCurrent mode: " + state + "\nHDR: " + state
                + "\nDisplay path GPU: " + state + "\nColor encoding: " + state
                + "\nBits per color channel: " + state + "\nSignal timing: " + state
                + "\nG-SYNC or Adaptive Sync (NVIDIA): " + state
                + (live.Failure.Length == 0 ? "" : "\nLive display query: " + DiagnosticValues.Status(live.Failure));
        }
        var display = matches[0];
        var lines = new List<string>
        {
            "Live monitor name: " + DiagnosticValues.Text(display.MonitorName),
            "Current mode: " + DiagnosticValues.Text(display.Mode),
            "Connection type: " + DiagnosticValues.Text(display.Connection),
            "HDR: " + DiagnosticValues.Text(display.Hdr),
            "Display path GPU: " + DiagnosticValues.Text(display.DrivingAdapter),
            "Color encoding: " + DiagnosticValues.Text(display.ColorEncoding),
            "Bits per color channel: " + (display.BitsPerColorChannel is >= 1 and <= 64
                ? display.BitsPerColorChannel.Value.ToString(CultureInfo.InvariantCulture) : "unknown"),
            "NVIDIA VRR capable: " + DiagnosticValues.Text(display.VrrCapability),
            "NVIDIA VRR requested: " + DiagnosticValues.Text(display.VrrRequested),
            "G-SYNC or Adaptive Sync enabled (NVIDIA): " + DiagnosticValues.Text(display.VrrEnabled),
            "VRR active at capture (NVIDIA): " + DiagnosticValues.Text(display.VrrActive),
            "Monitor Adaptive Sync menu setting: unknown (check the monitor menu)",
        };
        lines.AddRange(FormatSignalTiming(display.SignalTiming));
        if (display.Error.Length != 0) lines.Add("Live display read: " + DiagnosticValues.Status(display.Error));
        if (live.Failure.Length != 0) lines.Add("Other active display reads: " + DiagnosticValues.Status(live.Failure));
        return string.Join("\n", lines);
    }

    private static IEnumerable<string> FormatSignalTiming(SignalTimingObservation? observation)
    {
        SignalTimingObservation? timing = observation?.Sanitized();
        if (timing is null)
        {
            yield return "Signal timing: unknown";
            yield break;
        }

        yield return "Signal pixel clock: " + (timing.PixelClockHz is { } pixelClock
            ? pixelClock.ToString(CultureInfo.InvariantCulture) + " Hz" : "unknown");
        yield return "Signal horizontal sync: " + FormatRational(
            timing.HorizontalSyncNumerator, timing.HorizontalSyncDenominator);
        yield return "Signal vertical sync: " + FormatRational(
            timing.VerticalSyncNumerator, timing.VerticalSyncDenominator);
        yield return "Signal active pixels: " + FormatRegion(timing.ActiveWidth, timing.ActiveHeight);
        yield return "Signal total pixels: " + FormatRegion(timing.TotalWidth, timing.TotalHeight);
        yield return "Path refresh: " + FormatRational(
            timing.PathRefreshNumerator, timing.PathRefreshDenominator);
        yield return "Signal scan-line ordering: " + DiagnosticValues.Text(timing.ScanLineOrdering);
        yield return "Signal timing read: " + DiagnosticValues.Status(timing.Status);
    }

    private static string FormatRational(uint? numerator, uint? denominator) =>
        numerator is > 0 && denominator is > 0
            ? numerator.Value.ToString(CultureInfo.InvariantCulture) + "/"
                + denominator.Value.ToString(CultureInfo.InvariantCulture) + " Hz"
            : "unknown";

    private static string FormatRegion(uint? width, uint? height) =>
        width is > 0 && height is > 0
            ? width.Value.ToString(CultureInfo.InvariantCulture) + " x "
                + height.Value.ToString(CultureInfo.InvariantCulture)
            : "unknown";
}
