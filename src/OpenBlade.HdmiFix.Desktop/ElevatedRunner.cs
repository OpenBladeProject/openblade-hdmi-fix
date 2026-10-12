using OpenBlade.HdmiFix.Core;
using System.Security.Principal;
using System.Windows;

namespace OpenBlade.HdmiFix.Desktop;

internal static class ElevatedRunner
{
    public static int? TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] != "--elevated-operation") return null;
        try
        {
            if (args.Length != 6 || args[1] is not ("patch" or "restore")
                || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("The fixed administrator operation was not started correctly.");
            MonitorInstance.Validate(args[2]);
            if (!MonitorRegistry.IsRazerBlade()) throw new InvalidOperationException("This workaround is intended for a Razer Blade.");
            MonitorRegistry.EnsureDisconnected(args[2]);
            // Serialize administrator operations across app copies and users; no automatic wait/retry of writes.
            using var mutex = new Mutex(false, @"Global\OpenBlade.HdmiFix.Operation");
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("Another HDMI patch operation is running. Refresh after it finishes.");
                var store = new MonitorRegistry();
                var files = new BackupFiles(args[5]);
                files.EnsureProtectedDirectory();
                if (args[1] == "patch")
                {
                    if (args[4] != "-") throw new InvalidDataException("Unexpected patch arguments.");
                    var current = store.Read(args[2]);
                    var inventory = files.ReadInventory();
                    if (!inventory.AllowsNewBackup)
                        throw new InvalidOperationException(inventory.HasPrivateBackups
                            ? "Backups from another Windows account are present. Use the account that created them before patching."
                            : "Restore unfinished operations before creating another backup. Existing backups can still be restored.");
                    if (inventory.RecoverableFor(current).Length != 0)
                        throw new InvalidOperationException("An original backup requires reconciliation. Use Restore before a new patch.");
                    OverrideOperations.Apply(store, files, args[2], args[3]);
                }
                else
                {
                    var original = files.ReadById(args[4]);
                    if (original.InstanceId != args[2]) throw new InvalidDataException("The backup belongs to a different saved monitor instance.");
                    OverrideOperations.Restore(store, files, original, args[3]);
                }
                return 0;
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (Exception e) when (PatcherBackend.IsExpected(e))
        {
            // No automatic rollback after ambiguous submission: a Prepared backup permits one verified recovery.
            MessageBox.Show(e.Message + "\n\nKeep the affected monitor disconnected. Refresh in the main window and preserve all backups.",
                "HDMI operation stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }
}
