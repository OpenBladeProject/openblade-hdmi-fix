namespace OpenBlade.HdmiFix.Desktop;

public sealed class PreviewBackend : IPatcherBackend
{
    private static readonly IReadOnlyList<MonitorView> PreviewMonitors =
    [
        PatcherBackend.UnavailableMonitor(@"DISPLAY\SAM74AF\preview", "Previously connected", "Connection PREVIEW",
            new SavedMonitorReadException(SavedMonitorReadFailure.EdidInvalid, PreviewInspection())),
        new(
            "DISPLAY\\GSM5B09\\5&2A7B4D91&0&UID4355",
            "LG UltraGear",
            "Previously connected",
            "Display 3 Ã‚Â· GSM5B09",
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
            "Internal display Ã‚Â· BOE0BCA",
            "Not supported",
            "The built-in display is listed so you can identify it. HDMI Fix cannot patch it.",
            CanPatch: false,
            CanRestore: false,
            BackupPath: null),
        new(
            "DISPLAY\\SAM7463\\5&2A7B4D91&0&UID4352",
            "Samsung Odyssey OLED G9 (G93SC)",
            "Previously connected",
            "Display 2 Ã‚Â· SAM7463",
            RecoveryMessages.ChangedStatus,
            RecoveryMessages.ChangedDetails,
            CanPatch: false,
            CanRestore: false,
            BackupPath: null),
    ];

    private static string PreviewInspection()
    {
        var bytes = new byte[256];
        byte[] header = [0, 255, 255, 255, 255, 255, 255, 0];
        header.CopyTo(bytes, 0);
        bytes[8] = 0x4C; bytes[9] = 0x2D; bytes[10] = 0xAF; bytes[11] = 0x74;
        bytes[18] = 1; bytes[19] = 4; bytes[126] = 1;
        bytes[127] = unchecked((byte)-bytes.Take(127).Sum(x => (int)x));
        bytes[128] = 2; bytes[129] = 3; bytes[130] = 4;
        // Synthetic checksum failure; no saved monitor or registry is read.
        return SavedEntryInspection.DescribeEdid(@"DISPLAY\SAM74AF\preview", bytes, Microsoft.Win32.RegistryValueKind.Binary)
            + "\nOverride: absent";
    }

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
        return "Mode: Preview\n" + PatcherBackend.FormatRelatedDiagnostics(monitor, PreviewMonitors) + "\nDevice access: Off";
    }
}
