namespace OpenBlade.HdmiFix.Desktop;

public sealed class PreviewBackend : IPatcherBackend
{
    private static readonly IReadOnlyList<MonitorView> PreviewMonitors =
    [
        new(
            "DISPLAY\\GSM5B09\\5&2A7B4D91&0&UID4355",
            "LG UltraGear",
            "Previously connected",
            "Display 3 · GSM5B09",
            "Ready to patch",
            "Lower both maximum HDMI link rates from FRL10 to FRL8 and keep DSC enabled. Resolution and refresh-rate entries, HDR, VRR, audio, and the HDMI address stay unchanged.",
            CanPatch: true,
            CanRestore: false,
            BackupPath: null,
            ValidationWarning: MonitorWarnings.Unvalidated),
        new(
            "DISPLAY\\BOE0BCA\\4&17DBF615&0&UID265988",
            "Built-in laptop display",
            "Connected",
            "Internal display · BOE0BCA",
            "Not supported",
            "The built-in display is listed so you can identify it. HDMI Fix cannot patch it.",
            CanPatch: false,
            CanRestore: false,
            BackupPath: null),
        new(
            "DISPLAY\\SAM7463\\5&2A7B4D91&0&UID4352",
            "Samsung Odyssey OLED G9 (G93SC)",
            "Previously connected",
            "Display 2 · SAM7463",
            RecoveryMessages.ChangedStatus,
            RecoveryMessages.ChangedDetails,
            CanPatch: false,
            CanRestore: false,
            BackupPath: null),
    ];

    public string BackupDirectory => @"C:\ProgramData\OpenBlade.HdmiFix.Backups";

    public Task<MonitorScan> ScanAsync()
    {
        return Task.FromResult(new MonitorScan(
            PreviewMonitors,
            string.Empty));
    }

    public Task<OperationOutcome> ApplyAsync(MonitorView monitor, bool restore)
    {
        return Task.FromResult(new OperationOutcome(
            false,
            "Preview data cannot be patched or restored."));
    }

    public string DiagnosticText(MonitorView? monitor)
    {
        if (monitor is null)
        {
            return "Mode: Preview\nSelected monitor: None\nDevice access: Off";
        }

        return $"Mode: Preview\nMonitor: {monitor.Name}\nConnection: {monitor.ConnectionLabel}\nSaved connection: {monitor.InstanceLabel}\nInstance ID: {monitor.InstanceId}\nStatus: {monitor.Status}\nDevice access: Off";
    }
}
