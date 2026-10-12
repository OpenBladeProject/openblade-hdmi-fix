namespace OpenBlade.HdmiFix.Desktop;

public sealed record MonitorView(
    string InstanceId, string Name, string ConnectionLabel, string InstanceLabel,
    string Status, string Details, bool CanPatch, bool CanRestore, string? BackupPath)
{
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
