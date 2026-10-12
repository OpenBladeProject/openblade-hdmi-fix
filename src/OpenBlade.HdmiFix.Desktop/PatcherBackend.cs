using OpenBlade.HdmiFix.Core;
using System.ComponentModel;
using System.Diagnostics;

namespace OpenBlade.HdmiFix.Desktop;

public sealed class PatcherBackend : IPatcherBackend
{
    private readonly MonitorRegistry registry = new();
    private readonly BackupFiles backups = new();
    private readonly Dictionary<string, MonitorState> displayed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PatchBackup> restorations = new(StringComparer.OrdinalIgnoreCase);
    public string BackupDirectory => backups.DirectoryPath;

    public Task<MonitorScan> ScanAsync() => Task.Run(Scan);

    private MonitorScan Scan()
    {
        displayed.Clear();
        restorations.Clear();
        HashSet<string>? present = null;
        var notices = new List<string>();
        try { present = PresentMonitorDevices.Read(); }
        catch (Exception e) when (IsExpected(e)) { notices.Add("The app couldn't check which monitors are connected. Patch and Restore are unavailable."); }
        var blade = MonitorRegistry.IsRazerBlade();
        if (!blade) notices.Add("You can view saved monitors here. Patching is available only on a Razer Blade.");
        var inventory = BackupInventory.Empty;
        var trustedBackups = true;
        try
        {
            inventory = backups.ReadInventory();
            if (inventory.HasPrivateBackups)
                notices.Add(RecoveryMessages.PrivateBackups);
            else if (!inventory.AllowsNewBackup)
                notices.Add("Restore unfinished operations before patching again. Existing backups can still be restored.");
        }
        catch (Exception e) when (IsExpected(e))
        {
            trustedBackups = false;
            notices.Add("The app couldn't verify your backups. Keep the backup folder. Patch and Restore are unavailable.");
        }
        var views = new List<MonitorView>();
        foreach (var id in registry.EnumerateSavedInstances())
        {
            var connected = present?.Contains(id);
            var connection = connected switch { true => "Connected", false => "Previously connected", _ => "Connection unknown" };
            // Short stable digest distinguishes port/instance records without putting machine-specific IDs in copied reports.
            var instanceLabel = "Connection " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(id)))[..8];
            try
            {
                var state = registry.Read(id);
                displayed[id] = state;
                var plan = EdidPatchPlanner.Plan(state.Edid);
                var name = EdidPatchPlanner.MonitorName(state.Edid);
                if (string.IsNullOrWhiteSpace(name))
                    name = id.StartsWith(@"DISPLAY\SAM7463\", StringComparison.OrdinalIgnoreCase) ? "Samsung G93SC (SAM7463)" : id.Split('\\')[1];
                var candidates = inventory.RecoverableFor(state);
                var backup = candidates.Length == 1 ? candidates[0] : null;
                var canOperate = blade && trustedBackups && connected == false;
                var canPatch = canOperate && inventory.AllowsNewBackup && plan.Disposition == PatchDisposition.Ready
                    && state.Overrides.Blocks.Count == 0 && candidates.Length == 0;
                var canRestore = canOperate && backup is not null;
                var status = plan.Disposition switch
                {
                    PatchDisposition.Ready => "Ready to patch",
                    PatchDisposition.AlreadyCapped => "Already capped at FRL8",
                    PatchDisposition.Invalid => "Incomplete or invalid EDID",
                    _ => "Unsupported configuration"
                };
                var details = plan.Summary;
                if (plan.Disposition == PatchDisposition.Ready)
                    details = "Lower both maximum HDMI link rates from FRL10 to FRL8 and keep DSC enabled. Resolution and refresh-rate entries, HDR, VRR, audio, and the HDMI address stay unchanged.";
                if (backup is not null)
                {
                    restorations[id] = backup;
                    var applied = state.Overrides.Fingerprint() == backup.PatchedOverrides.Fingerprint();
                    status = applied ? "Patch installed" : "Recovery available";
                    details = applied ? "The FRL8 override is installed and verified. Restore returns this connection to its previous state. Check the display after reconnecting."
                        : "A backup from an unfinished operation is available. Choose Restore before patching again.";
                }
                else if (inventory.HasChangedEdid(state))
                {
                    status = RecoveryMessages.ChangedStatus;
                    details = RecoveryMessages.ChangedDetails;
                }
                else if (state.Overrides.Blocks.Count != 0)
                {
                    status = "Existing override";
                    details = inventory.HasPrivateBackups
                        ? "This connection has an override. A backup may belong to another Windows account. Open the app under the account that created it to check Restore."
                        : "This connection already has an override, but the app has no matching original backup. Use the tool that installed it to restore it first.";
                }
                if (candidates.Length > 1)
                {
                    status = "Several backups match";
                    details = "More than one original backup matches this connection. Keep the backup folder. Patch and Restore are unavailable.";
                }
                if (connected == true && (plan.Disposition == PatchDisposition.Ready || backup is not null))
                    details += " Unplug this monitor to use Patch or Restore.";
                if (connected is null) details += " The app must check the connection before making changes.";
                views.Add(new(id, name, connection, instanceLabel, status, details, canPatch, canRestore,
                    backup is null ? null : Path.Combine(BackupDirectory, backup.Id + ".json")));
            }
            catch (Exception e) when (IsExpected(e))
            {
                views.Add(new(id, id.Split('\\')[1], connection, instanceLabel, "Saved entry unavailable",
                    "Windows has no complete, readable capability data or recognized override for this entry. Keep the affected monitor unplugged. Patching is unavailable.", false, false, null));
            }
        }
        if (views.Count == 0) notices.Add("Windows has no saved monitors. Keep the crashing monitor unplugged. The app needs its previously saved capability data to patch it.");
        return new(views.OrderByDescending(v => v.InstanceId.StartsWith(@"DISPLAY\SAM7463\", StringComparison.OrdinalIgnoreCase))
            .ThenBy(v => v.DisplayLabel, StringComparer.OrdinalIgnoreCase).ToArray(), string.Join(" ", notices));
    }

    public async Task<OperationOutcome> ApplyAsync(MonitorView monitor, bool restore)
    {
        if (restore ? !monitor.CanRestore : !monitor.CanPatch)
            return new(false, "This action is unavailable. Refresh the saved monitor status.");
        try
        {
            if (!displayed.TryGetValue(monitor.InstanceId, out var expected))
                return new(false, "Refresh before continuing.");
            MonitorInstance.Validate(monitor.InstanceId);
            MonitorRegistry.EnsureDisconnected(monitor.InstanceId);
            if (registry.Read(monitor.InstanceId).Fingerprint != expected.Fingerprint)
                return new(false, "The selected monitor state changed. Refresh before continuing.");
            var backupId = restore && restorations.TryGetValue(monitor.InstanceId, out var backup) ? backup.Id : "-";
            if (restore && backupId == "-") return new(false, "No verified original backup is available.");
            var info = new ProcessStartInfo(Environment.ProcessPath
                ?? throw new InvalidOperationException("The application path is unavailable."))
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory
            };
            info.ArgumentList.Add("--elevated-operation");
            info.ArgumentList.Add(restore ? "restore" : "patch");
            info.ArgumentList.Add(monitor.InstanceId);
            info.ArgumentList.Add(expected.Fingerprint);
            info.ArgumentList.Add(backupId);
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                info.ArgumentList.Add(identity.User?.Value ?? throw new InvalidOperationException("The Windows account is unavailable."));
            using var process = Process.Start(info) ?? throw new InvalidOperationException("The administrator operation did not start.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                return new(false, "The change was canceled or couldn't be verified. Keep the monitor unplugged and keep its backups. The refreshed status will show whether Restore is available.");
            // The normal parent independently confirms the administrator child's result.
            var actual = registry.Read(monitor.InstanceId);
            if (restore)
            {
                var original = backups.ReadById(backupId);
                if (actual.EdidHash != expected.EdidHash || !OverrideOperations.IsRestoredState(actual.Overrides, original.OriginalOverrides))
                    return new(false, "Restoration could not be confirmed. Keep the backup and leave the monitor disconnected.");
                return new(true, "Restore completed and was verified. Windows will use the previous capabilities when it next initializes this saved connection.");
            }
            var plan = EdidPatchPlanner.Plan(expected.Edid);
            var intended = OverrideOperations.BuildPatchedState(expected.Overrides, plan);
            if (actual.EdidHash != expected.EdidHash || actual.Overrides.Fingerprint() != intended.Fingerprint())
                return new(false, "The installed override could not be confirmed. Keep the backup and leave the monitor disconnected.");
            return new(true, "Patch installed. You can reconnect the monitor. Only this saved connection is patched. If Windows creates a new entry or the problem remains, unplug the monitor and check its saved entries again.");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return new(false, "Administrator approval was canceled. No new operation was started."); }
        catch (Exception e) when (IsExpected(e))
        {
            return new(false, "The change couldn't be completed. Keep the monitor unplugged and keep its backups. Check the refreshed status for Restore.");
        }
    }

    public string DiagnosticText(MonitorView? monitor)
    {
        var version = typeof(PatcherBackend).Assembly.GetName().Version;
        if (monitor is null) return $"OpenBlade HDMI Fix {version}\nNo monitor selected.";
        return $"OpenBlade HDMI Fix {version}\nMonitor ID: {monitor.InstanceId.Split('\\')[1]}\n"
            + $"Connection: {monitor.ConnectionLabel}\nState: {monitor.Status}\n"
            + $"Patch available: {monitor.CanPatch}\nRestore available: {monitor.CanRestore}\n"
            + "Raw EDID, serials, device instance IDs, and local paths are omitted.";
    }

    internal static bool IsExpected(Exception e) => e is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or InvalidOperationException or Win32Exception
        or System.Text.Json.JsonException or ArgumentException;
}

internal static class RecoveryMessages
{
    internal const string PrivateBackups = "Backups from another Windows account are present. Use the account that created them before patching. You can still restore your own backups.";
    internal const string ChangedStatus = "Saved capabilities changed";
    internal const string ChangedDetails = "The installed override matches a saved patch, but Windows now has different capability data for this connection. Restore is unavailable. Keep the backup folder and copy the details to report this change.";
}
