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
            : live.Failure.Length != 0 ? "unknown (live display query unavailable)"
            : "unavailable (no active path matches this saved connection)";
        if (selected.ConnectionLabel == "Previously connected" || matches.Length != 1)
        {
            string state = unavailable.StartsWith("unknown", StringComparison.Ordinal) ? "unknown" : "unavailable";
            return "Live display settings: " + unavailable + "\nLive monitor name: " + state
                + "\nCurrent mode: " + state + "\nHDR: " + state
                + "\nG-SYNC or Adaptive Sync (NVIDIA): " + state;
        }
        var display = matches[0];
        var lines = new List<string>
        {
            "Live monitor name: " + DiagnosticValues.Text(display.MonitorName),
            "Current mode: " + DiagnosticValues.Text(display.Mode),
            "Connection type: " + DiagnosticValues.Text(display.Connection),
            "HDR: " + DiagnosticValues.Text(display.Hdr),
            "NVIDIA VRR capable: " + DiagnosticValues.Text(display.VrrCapability),
            "NVIDIA VRR requested: " + DiagnosticValues.Text(display.VrrRequested),
            "G-SYNC or Adaptive Sync enabled (NVIDIA): " + DiagnosticValues.Text(display.VrrEnabled),
            "VRR active at capture (NVIDIA): " + DiagnosticValues.Text(display.VrrActive),
            "Monitor Adaptive Sync menu setting: unknown (check the monitor menu)",
        };
        if (display.Error.Length != 0) lines.Add("Live display read: " + DiagnosticValues.Status(display.Error));
        return string.Join("\n", lines);
    }
}
