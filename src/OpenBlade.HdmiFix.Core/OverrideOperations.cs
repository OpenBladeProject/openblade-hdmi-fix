using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenBlade.HdmiFix.Core;

public sealed record OverrideState(bool KeyExists, Dictionary<int, byte[]> Blocks)
{
    public static OverrideState Empty => new(false, []);
    public OverrideState Copy() => new(KeyExists, Blocks.ToDictionary(x => x.Key, x => x.Value.ToArray()));
    public string Fingerprint()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(KeyExists);
            foreach (var block in Blocks.OrderBy(x => x.Key))
            {
                writer.Write(block.Key);
                writer.Write(block.Value.Length);
                writer.Write(block.Value);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}

public sealed record MonitorState(string InstanceId, byte[] Edid, OverrideState Overrides)
{
    public string EdidHash => Convert.ToHexString(SHA256.HashData(Edid));
    public string Fingerprint => EdidHash + ":" + Overrides.Fingerprint();
}

public sealed record PatchBackup(
    int SchemaVersion, string Id, string InstanceId, byte[] OriginalEdid,
    OverrideState OriginalOverrides, OverrideState PatchedOverrides, string Stage)
{
    public void Validate()
    {
        if (SchemaVersion != 1 || !Guid.TryParseExact(Id, "N", out _)
            || Stage is not ("Prepared" or "Applied" or "Restored")
            || OriginalEdid is null || OriginalOverrides?.Blocks is null || PatchedOverrides?.Blocks is null)
            throw new InvalidDataException("The saved backup is incomplete or uses an unsupported format.");
        foreach (var state in new[] { OriginalOverrides, PatchedOverrides })
        {
            if ((!state.KeyExists && state.Blocks.Count != 0) || state.Blocks.Any(b => b.Key is < 0 or > 255
                || b.Value is null || b.Value.Length != 128 || b.Value.Sum(x => (int)x) % 256 != 0))
                throw new InvalidDataException("The backup contains an invalid EDID block.");
        }
        MonitorInstance.ValidateEdid(InstanceId, OriginalEdid);
        // Only the original model could have a backup from the old zero-offset parser.
        var plan = InstanceId.Split('\\')[1].Equals("SAM7463", StringComparison.OrdinalIgnoreCase)
            ? EdidPatchPlanner.PlanVersion1BackupRecovery(OriginalEdid)
            : EdidPatchPlanner.Plan(OriginalEdid);
        if (plan.Disposition != PatchDisposition.Ready || plan.ChangedBlocks.Length != 1
            || OriginalOverrides.Blocks.Count != 0)
            throw new InvalidDataException("The saved backup does not describe an admitted original EDID.");
        var expected = OverrideOperations.BuildPatchedState(OriginalOverrides, plan);
        if (expected.Fingerprint() != PatchedOverrides.Fingerprint())
            throw new InvalidDataException("The saved override does not match the two-field EDID patch.");
    }
}

public static class MonitorInstance
{
    // Arguments never select an arbitrary registry path: only one exact monitor instance.
    public static void Validate(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || instanceId.Length > 240)
            throw new InvalidDataException("No valid saved monitor instance was selected.");
        var parts = instanceId.Split('\\');
        if (parts.Length != 3 || parts[0] != "DISPLAY" || parts[1].Length != 7
            || parts[1][..3].Any(c => !char.IsAsciiLetter(c))
            || parts[1][3..].Any(c => !Uri.IsHexDigit(c))
            || parts[2].Length == 0 || parts[2].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('&' or '_' or '-' or '.'))
            || parts[2] is "." or "..")
            throw new InvalidDataException("Only an exact saved DISPLAY monitor instance can be changed.");
    }

    public static void ValidateEdid(string instanceId, byte[] edid)
    {
        Validate(instanceId);
        string model = EdidPatchPlanner.MonitorModelId(edid);
        if (model.Length == 0 || !instanceId.Split('\\')[1].Equals(model, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The saved monitor identity does not match its EDID.");
    }
}

public interface IMonitorOverrideStore
{
    MonitorState Read(string instanceId);
    void WriteBlock(string instanceId, int block, byte[] value, string expectedFingerprint);
    void RemoveBlock(string instanceId, int block, string expectedFingerprint);
}

public interface IBackupStore
{
    void Save(PatchBackup backup);
}

public static class OverrideOperations
{
    public static OverrideState BuildPatchedState(OverrideState original, EdidPatchPlan plan)
    {
        var blocks = original.Copy().Blocks;
        foreach (var number in plan.ChangedBlocks)
            blocks[number] = plan.Patched.AsSpan(number * 128, 128).ToArray();
        return new(true, blocks);
    }

    public static PatchBackup Apply(IMonitorOverrideStore store, IBackupStore backups,
        string instanceId, string expectedFingerprint)
    {
        MonitorInstance.Validate(instanceId);
        var original = store.Read(instanceId);
        EnsureOriginal(original, instanceId, expectedFingerprint);
        MonitorInstance.ValidateEdid(instanceId, original.Edid);
        var plan = EdidPatchPlanner.Plan(original.Edid);
        if (plan.Disposition != PatchDisposition.Ready || plan.ChangedBlocks.Length != 1)
            throw new InvalidOperationException(plan.Summary);
        if (original.Overrides.Blocks.Count != 0)
            throw new InvalidOperationException("This monitor has an existing override. Restore it with its owning tool before patching.");
        var backup = new PatchBackup(1, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), instanceId,
            original.Edid.ToArray(), original.Overrides.Copy(), BuildPatchedState(original.Overrides, plan), "Prepared");
        backup.Validate();
        backups.Save(backup); // Durable original is committed before the first registry mutation.
        EnsureOriginal(store.Read(instanceId), instanceId, expectedFingerprint);
        var number = plan.ChangedBlocks.Single();
        store.WriteBlock(instanceId, number, backup.PatchedOverrides.Blocks[number], expectedFingerprint);
        var readback = store.Read(instanceId);
        EnsureOriginal(readback, instanceId, original.EdidHash + ":" + backup.PatchedOverrides.Fingerprint());
        backup = backup with { Stage = "Applied" };
        backups.Save(backup);
        return backup;
    }

    public static bool CanRestore(MonitorState current, PatchBackup backup)
    {
        try
        {
            backup.Validate();
            if (backup.Stage == "Restored" || current.InstanceId != backup.InstanceId
                || !current.Edid.AsSpan().SequenceEqual(backup.OriginalEdid)) return false;
            return IsRecoverable(current.Overrides, backup);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NullReferenceException)
        {
            return false;
        }
    }

    public static void Restore(IMonitorOverrideStore store, IBackupStore backups,
        PatchBackup backup, string expectedFingerprint)
    {
        backup.Validate();
        var current = store.Read(backup.InstanceId);
        EnsureOriginal(current, backup.InstanceId, expectedFingerprint);
        if (!CanRestore(current, backup))
            throw new InvalidOperationException("The saved monitor or override changed. No restoration was attempted; keep the backup.");
        var number = backup.PatchedOverrides.Blocks.Keys.Single();
        // An interrupted apply may leave only a newly-created empty key.
        store.RemoveBlock(backup.InstanceId, number, expectedFingerprint);
        var restored = store.Read(backup.InstanceId);
        if (restored.InstanceId != backup.InstanceId || restored.EdidHash != current.EdidHash
            || !IsRestoredState(restored.Overrides, backup.OriginalOverrides))
            throw new InvalidOperationException("Restore could not be verified. Keep the monitor unplugged and preserve its backup.");
        backups.Save(backup with { Stage = "Restored" });
    }

    // Keep an inert empty key instead of deleting a registry subtree that another tool could populate.
    public static bool IsRestoredState(OverrideState actual, OverrideState original) =>
        actual.Fingerprint() == original.Fingerprint() || (actual.Blocks.Count == 0 && original.Blocks.Count == 0);

    private static bool IsRecoverable(OverrideState current, PatchBackup backup) =>
        current.Fingerprint() == backup.PatchedOverrides.Fingerprint()
        || current.Fingerprint() == backup.OriginalOverrides.Fingerprint()
        || (!backup.OriginalOverrides.KeyExists && current.KeyExists && current.Blocks.Count == 0);

    private static void EnsureOriginal(MonitorState current, string instanceId, string fingerprint)
    {
        if (current.InstanceId != instanceId || current.Fingerprint != fingerprint)
            throw new InvalidOperationException("The selected monitor state changed. Refresh before continuing. No further changes were made.");
    }
}
