namespace OpenBlade.HdmiFix.Desktop;

public sealed class PreviewBackend : IPatcherBackend
{
    private static readonly IReadOnlyList<MonitorView> PreviewMonitors =
    [
        new(
            "DISPLAY\\SAM7463\\5&2A7B4D91&0&UID4352",
            "Samsung Odyssey OLED G9 (G93SC)",
            "Previously connected",
            "Display 2 · SAM7463",
            "Ready to patch",
            "The saved display data is complete. The patch changes FRL 10 to FRL 8 and keeps DSC enabled.",
            CanPatch: true,
            CanRestore: false,
            BackupPath: null),
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
            "DISPLAY\\GSM5B09\\5&2A7B4D91&0&UID4355",
            "LG UltraGear",
            "Previously connected",
            "Display 3 · GSM5B09",
            "Saved display data is incomplete",
            "OpenBlade does not have complete saved display data for this monitor, so it cannot be patched.",
            CanPatch: false,
            CanRestore: false,
            BackupPath: null),
    ];

    public string BackupDirectory => @"C:\ProgramData\OpenBlade.HdmiFix.Backups";

    public Task<MonitorScan> ScanAsync()
    {
        return Task.FromResult(new MonitorScan(
            PreviewMonitors,
            "Three saved monitors loaded."));
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
