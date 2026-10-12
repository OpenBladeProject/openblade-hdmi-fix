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
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public PatchBackup ReadById(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("No valid backup was selected.");
        VerifyDirectory();
        var file = new FileInfo(Path.Combine(DirectoryPath, id + ".json"));
        if (!file.Exists || file.Length > 524288) throw new InvalidDataException("The original backup is missing or incomplete.");
        VerifyAcl(file);
        var backup = JsonSerializer.Deserialize<PatchBackup>(File.ReadAllBytes(file.FullName))
            ?? throw new InvalidDataException("The original backup is unreadable.");
        backup.Validate();
        if (backup.Id != id) throw new InvalidDataException("The backup identity does not match its file.");
        return backup;
    }

    public IReadOnlyList<PatchBackup> ReadAll()
    {
        VerifyDirectory();
        if (!Directory.Exists(DirectoryPath)) return [];
        var files = Directory.EnumerateFiles(DirectoryPath, "*.json").Take(513).ToArray();
        if (files.Length > 512) throw new InvalidDataException("Too many backup records. Preserve the folder and review it before continuing.");
        // A corrupt record blocks mutation rather than hiding an original that might be needed for recovery.
        var result = new List<PatchBackup>();
        foreach (var file in files)
        {
            try { result.Add(ReadById(Path.GetFileNameWithoutExtension(file))); }
            catch (UnauthorizedAccessException) { /* Another Windows account's private recovery payload. */ }
        }
        return result;
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
