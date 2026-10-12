using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using OpenBlade.HdmiFix.Core;

namespace OpenBlade.HdmiFix.Desktop;

internal sealed class BackupFiles : IBackupStore
{
    private readonly SecurityIdentifier reader;
    public BackupFiles(string? readerSid = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        reader = readerSid is null ? identity.User ?? throw new InvalidOperationException("The current Windows account is unavailable.")
            : new SecurityIdentifier(readerSid);
        if (!reader.IsAccountSid() && !reader.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal))
            throw new InvalidDataException("The backup reader must be a Windows user account.");
    }
    public string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenBlade.HdmiFix.Backups");
    private string HistoryDirectory => Path.Combine(DirectoryPath, "history-" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reader.Value)))[..16]);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public void EnsureProtectedDirectory()
    {
        var directory = new DirectoryInfo(DirectoryPath);
        if (!directory.Exists)
        {
            var acl = new DirectorySecurity();
            acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
            directory.Create(acl);
        }
        VerifyDirectory();
    }

    public void VerifyDirectory()
    {
        var directory = new DirectoryInfo(DirectoryPath);
        if (!directory.Exists) return;
        VerifyAcl(directory);
    }

    public void Save(PatchBackup backup)
    {
        backup.Validate();
        EnsureProtectedDirectory();
        var target = Path.Combine(DirectoryPath, backup.Id + ".json");
        if (File.Exists(target)) VerifyAcl(new FileInfo(target));
        var temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        var data = JsonSerializer.SerializeToUtf8Bytes(backup, JsonOptions);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                var temporaryFile = new FileInfo(temporary);
                var fileAcl = temporaryFile.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                fileAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                fileAcl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                fileAcl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                fileAcl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                fileAcl.AddAccessRule(new(reader, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                temporaryFile.SetAccessControl(fileAcl);
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
            VerifyAcl(new FileInfo(target));
            var readback = ReadById(backup.Id);
            if (!JsonSerializer.SerializeToUtf8Bytes(readback, JsonOptions).AsSpan().SequenceEqual(data))
                throw new IOException("The original backup could not be verified. No new patch should be attempted.");
            if (backup.Stage == "Restored")
            {
                var history = new DirectoryInfo(HistoryDirectory);
                if (!history.Exists)
                {
                    var historyAcl = new DirectorySecurity();
                    historyAcl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                    historyAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                    historyAcl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                        FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
                    historyAcl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                        FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
                    historyAcl.AddAccessRule(new(reader, FileSystemRights.ReadAndExecute,
                        inheritance, PropagationFlags.None, AccessControlType.Allow));
                    history.Create(historyAcl);
                }
                VerifyAcl(history);
                var archived = Path.Combine(HistoryDirectory, backup.Id + ".json");
                if (File.Exists(archived)) throw new InvalidDataException("A history record already uses this backup ID. Keep both records.");
                File.Move(target, archived);
                if (ReadById(backup.Id).Stage != "Restored")
                    throw new IOException("The finished backup could not be verified in history.");
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public PatchBackup ReadById(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("No valid backup was selected.");
        VerifyDirectory();
        var active = new FileInfo(Path.Combine(DirectoryPath, id + ".json"));
        var history = new DirectoryInfo(HistoryDirectory);
        if (history.Exists) VerifyAcl(history);
        var archived = new FileInfo(Path.Combine(HistoryDirectory, id + ".json"));
        if (active.Exists && archived.Exists) throw new InvalidDataException("Several files use this backup ID. Keep the backup folder.");
        var file = active.Exists ? active : archived;
        if (!file.Exists || file.Length > 524288) throw new InvalidDataException("The original backup is missing or incomplete.");
        VerifyAcl(file);
        // Use the initiating user's explicit payload grant even when the helper is elevated.
        // Administrator credentials must not make its catalog differ from the normal window's.
        var access = file.GetAccessControl(AccessControlSections.Access);
        var rules = access.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        if (rules.Any(rule => rule.AccessControlType == AccessControlType.Deny
            && (rule.FileSystemRights & FileSystemRights.ReadData) != 0))
            throw new InvalidDataException("The backup has an unsupported read restriction. Keep the backup folder.");
        var permitted = rules.Any(rule => !rule.IsInherited && rule.IdentityReference.Equals(reader)
                && rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & FileSystemRights.ReadData) != 0);
        if (!permitted) throw new UnauthorizedAccessException("Use the Windows account that created this backup.");
        var backup = JsonSerializer.Deserialize<PatchBackup>(File.ReadAllBytes(file.FullName))
            ?? throw new InvalidDataException("The original backup is unreadable.");
        backup.Validate();
        if (backup.Id != id) throw new InvalidDataException("The backup identity does not match its file.");
        return backup;
    }

    public BackupInventory ReadInventory()
    {
        VerifyDirectory();
        if (!Directory.Exists(DirectoryPath)) return BackupInventory.Empty;
        // Enumerate every active original; an arbitrary discovery cutoff can hide the selected recovery.
        // Finished history is retained separately. Legacy Restored records are excluded by the inventory.
        return BackupInventory.Read(Directory.EnumerateFiles(DirectoryPath, "*.json")
            .Select(Path.GetFileNameWithoutExtension).Select(id => id!), ReadById);
    }

    private static void VerifyAcl(FileSystemInfo item)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The backup location contains a redirected file or directory. No changes are allowed.");
        FileSystemSecurity security = item is DirectoryInfo directory
            ? directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : ((FileInfo)item).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var owner = (SecurityIdentifier?)security.GetOwner(typeof(SecurityIdentifier));
        if (owner is null || (!owner.Equals(admin) && !owner.Equals(system)))
            throw new InvalidDataException("The backup location is not administrator-owned. No changes are allowed.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            const FileSystemRights mutating = FileSystemRights.Write | FileSystemRights.Delete
                | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & mutating) != 0
                && !rule.IdentityReference.Equals(admin) && !rule.IdentityReference.Equals(system))
                throw new InvalidDataException("The backup location permits untrusted changes. No changes are allowed.");
        }
    }
}
