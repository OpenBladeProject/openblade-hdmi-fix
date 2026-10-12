namespace OpenBlade.HdmiFix.Core;

public sealed record BackupInventory(IReadOnlyList<PatchBackup> Records, bool HasPrivateBackups)
{
    public const int MaximumActiveBackups = 512;
    public static BackupInventory Empty => new([], false);
    public bool AllowsNewBackup => !HasPrivateBackups && Records.Count < MaximumActiveBackups;

    // Limit new work, never discovery of an original needed for recovery.
    public static BackupInventory Read(IEnumerable<string> ids, Func<string, PatchBackup> read)
    {
        var records = new List<PatchBackup>();
        var hasPrivateBackups = false;
        foreach (var id in ids)
        {
            try
            {
                var backup = read(id);
                if (backup.Stage != "Restored") records.Add(backup);
            }
            catch (UnauthorizedAccessException) { hasPrivateBackups = true; }
        }
        return new(records, hasPrivateBackups);
    }

    public PatchBackup[] RecoverableFor(MonitorState state) => Records
        .Where(b => b.InstanceId == state.InstanceId && OverrideOperations.CanRestore(state, b)).ToArray();

    public bool HasChangedEdid(MonitorState state) => Records.Any(b =>
        b.InstanceId == state.InstanceId && !state.Edid.AsSpan().SequenceEqual(b.OriginalEdid)
        && state.Overrides.Fingerprint() == b.PatchedOverrides.Fingerprint());
}
