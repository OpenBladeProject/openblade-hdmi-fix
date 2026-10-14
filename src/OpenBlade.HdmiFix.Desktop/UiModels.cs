namespace OpenBlade.HdmiFix.Desktop;

public sealed record MonitorView(
    string InstanceId, string Name, string ConnectionLabel, string InstanceLabel,
    string Status, string Details, bool CanPatch, bool CanRestore, string? BackupPath, string ValidationWarning = "", SavedMonitorReadFailure? ReadFailure = null, string InspectionDetails = "", bool IsStaleInternalPanel = false, bool IsInternalPanel = false)
{
    public string ActionHint => IsStaleInternalPanel || IsInternalPanel
        ? "Select the affected HDMI monitor to patch its saved connection."
        : "Keep the affected monitor unplugged until you have verified the patch or restore.";

    public string DisplayLabel => $"{Name} ({ConnectionLabel}, {InstanceLabel})";
}

public sealed record MonitorScan(IReadOnlyList<MonitorView> Monitors, string Message);
public sealed record OperationOutcome(bool Success, string Message);

public interface IPatcherBackend
{
    string BackupDirectory { get; }
    Task<MonitorScan> ScanAsync();
    Task<OperationOutcome> ApplyAsync(MonitorView monitor, bool restore);
    string DiagnosticText(MonitorView? monitor);
}

internal static class MonitorWarnings
{
    internal const string Unvalidated = "This workaround hasn't been validated on this monitor. Check refresh rate, HDR, VRR, and audio after reconnecting.";
}
