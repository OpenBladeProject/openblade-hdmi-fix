using OpenBlade.HdmiFix.Core;
using Xunit;

namespace OpenBlade.HdmiFix.Tests;

public sealed class OverrideOperationsTests
{
    private const string Instance = @"DISPLAY\SAM7463\5&fixture&0&UID1";

    [Fact]
    public void Apply_PersistsOriginalBeforeWrite_AndRestoreRecoversCapabilities()
    {
        var store = new MemoryMonitor();
        var backups = new MemoryBackups();
        store.BeforeWrite = () => Assert.Equal("Prepared", Assert.Single(backups.Items).Value.Stage);
        var original = store.State;
        var backup = OverrideOperations.Apply(store, backups, Instance, original.Fingerprint);
        Assert.Equal("Applied", backup.Stage);
        Assert.Single(store.State.Overrides.Blocks);
        Assert.Equal(original.Edid, store.State.Edid);
        Assert.True(OverrideOperations.CanRestore(store.State, backup));
        OverrideOperations.Restore(store, backups, backup, store.State.Fingerprint);
        Assert.True(OverrideOperations.IsRestoredState(store.State.Overrides, original.Overrides));
        Assert.True(store.State.Overrides.KeyExists);
        Assert.Equal("Restored", backups.Items[backup.Id].Stage);
        Assert.False(OverrideOperations.CanRestore(store.State, backups.Items[backup.Id]));
    }

    [Fact]
    public void Restore_PreservesAnOriginallyExistingEmptyKey()
    {
        var store = new MemoryMonitor { State = new(Instance, Edid(), new(true, [])) };
        var backups = new MemoryBackups();
        var backup = OverrideOperations.Apply(store, backups, Instance, store.State.Fingerprint);
        OverrideOperations.Restore(store, backups, backup, store.State.Fingerprint);
        Assert.True(store.State.Overrides.KeyExists);
        Assert.Empty(store.State.Overrides.Blocks);
    }

    [Fact]
    public void Apply_BackupFailureLeavesRegistryUntouched()
    {
        var store = new MemoryMonitor();
        var before = store.State.Fingerprint;
        Assert.Throws<IOException>(() => OverrideOperations.Apply(store,
            new MemoryBackups { FailSave = true }, Instance, before));
        Assert.Equal(0, store.WriteCount);
        Assert.Empty(store.State.Overrides.Blocks);
    }

    [Fact]
    public void Apply_DriftAfterBackupStopsBeforeWrite()
    {
        var store = new MemoryMonitor();
        var backups = new MemoryBackups { AfterSave = () => store.State = store.State with { Overrides = new(true, []) } };
        Assert.Throws<InvalidOperationException>(() => OverrideOperations.Apply(store, backups, Instance, store.State.Fingerprint));
        Assert.Equal(0, store.WriteCount);
        Assert.Single(backups.Items);
    }

    [Fact]
    public void Apply_InterruptedAfterCommittedValueLeavesRecoverableOriginal_WithoutReplay()
    {
        var store = new MemoryMonitor { ThrowAfterWrite = true };
        var backups = new MemoryBackups();
        var before = store.State.Fingerprint;
        Assert.Throws<IOException>(() => OverrideOperations.Apply(store, backups, Instance, before));
        Assert.Equal(1, store.WriteCount);
        var backup = Assert.Single(backups.Items).Value;
        Assert.Equal("Prepared", backup.Stage);
        Assert.True(OverrideOperations.CanRestore(store.State, backup));
        store.ThrowAfterWrite = false;
        OverrideOperations.Restore(store, backups, backup, store.State.Fingerprint);
        Assert.Empty(store.State.Overrides.Blocks);
    }

    [Fact]
    public void Apply_InterruptedAfterKeyCreationCanRestoreOriginalCapabilities()
    {
        var store = new MemoryMonitor { ThrowAfterKeyCreation = true };
        var backups = new MemoryBackups();
        var before = store.State.Fingerprint;
        Assert.Throws<IOException>(() => OverrideOperations.Apply(store, backups, Instance, before));
        var backup = Assert.Single(backups.Items).Value;
        Assert.True(OverrideOperations.CanRestore(store.State, backup));
        OverrideOperations.Restore(store, backups, backup, store.State.Fingerprint);
        Assert.True(OverrideOperations.IsRestoredState(store.State.Overrides, OverrideState.Empty));
        Assert.True(store.State.Overrides.KeyExists);
    }

    [Fact]
    public void Restore_RefusesOverrideChangedByAnotherTool()
    {
        var store = new MemoryMonitor();
        var backups = new MemoryBackups();
        var backup = OverrideOperations.Apply(store, backups, Instance, store.State.Fingerprint);
        store.State.Overrides.Blocks[1][10] ^= 1;
        var before = store.State.Fingerprint;
        Assert.False(OverrideOperations.CanRestore(store.State, backup));
        Assert.Throws<InvalidOperationException>(() => OverrideOperations.Restore(store, backups, backup, before));
        Assert.Equal(before, store.State.Fingerprint);
        Assert.Equal(0, store.RemoveCount);
    }

    [Fact]
    public void Restore_RefusesSameEdidFromDifferentInstance()
    {
        var store = new MemoryMonitor();
        var backups = new MemoryBackups();
        var backup = OverrideOperations.Apply(store, backups, Instance, store.State.Fingerprint);
        var other = store.State with { InstanceId = @"DISPLAY\SAM7463\5&other&0&UID2" };
        Assert.False(OverrideOperations.CanRestore(other, backup));
    }

    [Fact]
    public void Restore_AcceptsExactHistoricalVersion1ZeroBoundaryBackup()
    {
        PatchBackup backup = LegacyZeroBoundaryBackup();
        var store = new MemoryMonitor
        {
            State = new(Instance, backup.OriginalEdid.ToArray(), backup.PatchedOverrides.Copy()),
        };
        var backups = new MemoryBackups();

        Assert.Equal(PatchDisposition.Invalid, EdidPatchPlanner.Plan(backup.OriginalEdid).Disposition);
        backup.Validate();
        Assert.True(OverrideOperations.CanRestore(store.State, backup));

        OverrideOperations.Restore(store, backups, backup, store.State.Fingerprint);

        Assert.Empty(store.State.Overrides.Blocks);
        Assert.Equal("Restored", backups.Items[backup.Id].Stage);
    }

    [Fact]
    public void Restore_RefusesChangedStateOrTamperedHistoricalVersion1Backup()
    {
        PatchBackup backup = LegacyZeroBoundaryBackup();
        var exact = new MonitorState(Instance, backup.OriginalEdid.ToArray(), backup.PatchedOverrides.Copy());

        OverrideState foreignOverride = backup.PatchedOverrides.Copy();
        foreignOverride.Blocks[1][20] ^= 0x01;
        SetChecksum(foreignOverride.Blocks[1], 0);
        Assert.False(OverrideOperations.CanRestore(exact with { Overrides = foreignOverride }, backup));

        byte[] changedEdid = backup.OriginalEdid.ToArray();
        changedEdid[20] ^= 0x01;
        SetChecksum(changedEdid, 0);
        Assert.False(OverrideOperations.CanRestore(exact with { Edid = changedEdid }, backup));

        OverrideState tamperedPatch = backup.PatchedOverrides.Copy();
        tamperedPatch.Blocks[1][20] ^= 0x01;
        SetChecksum(tamperedPatch.Blocks[1], 0);
        PatchBackup tamperedBackup = backup with { PatchedOverrides = tamperedPatch };
        Assert.Throws<InvalidDataException>(tamperedBackup.Validate);
        Assert.False(OverrideOperations.CanRestore(exact, tamperedBackup));
    }

    [Fact]
    public void Backup_RejectsTamperedOriginalOrPatchedContents()
    {
        var store = new MemoryMonitor();
        var backup = OverrideOperations.Apply(store, new MemoryBackups(), Instance, store.State.Fingerprint);
        backup.PatchedOverrides.Blocks[1][20] ^= 1;
        Assert.Throws<InvalidDataException>(backup.Validate);
        Assert.False(OverrideOperations.CanRestore(store.State, backup));
    }

    [Fact]
    public void Apply_DoesNotReplaceAnExistingOverride()
    {
        var store = new MemoryMonitor { State = new(Instance, Edid(), new(true, new() { [0] = Edid()[..128] })) };
        var backups = new MemoryBackups();
        Assert.Throws<InvalidOperationException>(() => OverrideOperations.Apply(store, backups, Instance, store.State.Fingerprint));
        Assert.Equal(0, store.WriteCount);
        Assert.Empty(backups.Items);
    }

    [Fact]
    public void Apply_DriftAtMutationStopsWithoutReplacingForeignValues()
    {
        var store = new MemoryMonitor();
        var backups = new MemoryBackups();
        store.BeforeWrite = () => store.State = store.State with { Overrides = new(true, new() { [0] = Edid()[..128] }) };
        Assert.Throws<InvalidOperationException>(() => OverrideOperations.Apply(store, backups, Instance, store.State.Fingerprint));
        Assert.Equal(0, store.WriteCount);
        Assert.Single(store.State.Overrides.Blocks);
        Assert.True(store.State.Overrides.Blocks.ContainsKey(0));
    }

    [Fact]
    public void Backup_NullBlockFailsAsInvalidData()
    {
        var store = new MemoryMonitor();
        var backup = OverrideOperations.Apply(store, new MemoryBackups(), Instance, store.State.Fingerprint);
        backup.PatchedOverrides.Blocks[1] = null!;
        Assert.Throws<InvalidDataException>(backup.Validate);
        Assert.False(OverrideOperations.CanRestore(store.State, backup));
    }

    [Theory]
    [InlineData(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\SAM7463\x")]
    [InlineData(@"DISPLAY\SAM7463\x\Device Parameters")]
    [InlineData(@"DISPLAY\OTH1234\x")]
    [InlineData(@"DISPLAY\SAM7463\..")]
    public void Apply_RejectsAnUnboundedRegistryTarget(string target)
    {
        var store = new MemoryMonitor();
        Assert.Throws<InvalidDataException>(() => OverrideOperations.Apply(store, new MemoryBackups(), target, store.State.Fingerprint));
        Assert.Equal(0, store.WriteCount);
    }

    private sealed class MemoryMonitor : IMonitorOverrideStore
    {
        public MonitorState State { get; set; } = new(Instance, Edid(), OverrideState.Empty);
        public Action? BeforeWrite { get; set; }
        public bool ThrowAfterWrite { get; set; }
        public bool ThrowAfterKeyCreation { get; set; }
        public int WriteCount { get; private set; }
        public int RemoveCount { get; private set; }
        public MonitorState Read(string instanceId) => new(State.InstanceId, State.Edid.ToArray(), State.Overrides.Copy());
        public void WriteBlock(string instanceId, int block, byte[] value, string expectedFingerprint)
        {
            BeforeWrite?.Invoke();
            if (State.Fingerprint != expectedFingerprint) throw new InvalidOperationException("State drifted at mutation");
            WriteCount++;
            State = State with { Overrides = new(true, State.Overrides.Copy().Blocks) };
            if (ThrowAfterKeyCreation) throw new IOException("Interrupted before value write");
            State.Overrides.Blocks[block] = value.ToArray();
            if (ThrowAfterWrite) throw new IOException("Submission completed but caller lost acknowledgement");
        }
        public void RemoveBlock(string instanceId, int block, string expectedFingerprint)
        {
            if (State.Fingerprint != expectedFingerprint) throw new InvalidOperationException("State drifted at restoration");
            RemoveCount++;
            var blocks = State.Overrides.Copy().Blocks;
            blocks.Remove(block);
            State = State with { Overrides = new(State.Overrides.KeyExists, blocks) };
        }
    }

    private sealed class MemoryBackups : IBackupStore
    {
        public Dictionary<string, PatchBackup> Items { get; } = [];
        public bool FailSave { get; set; }
        public Action? AfterSave { get; set; }
        public void Save(PatchBackup backup)
        {
            if (FailSave) throw new IOException("Backup unavailable");
            Items[backup.Id] = backup with
            {
                OriginalEdid = backup.OriginalEdid.ToArray(), OriginalOverrides = backup.OriginalOverrides.Copy(),
                PatchedOverrides = backup.PatchedOverrides.Copy()
            };
            AfterSave?.Invoke();
        }
    }

    private static byte[] Edid()
    {
        var data = new byte[256];
        byte[] header = [0, 255, 255, 255, 255, 255, 255, 0];
        header.CopyTo(data, 0);
        data[8] = 0x4C; data[9] = 0x2D; data[10] = 0x63; data[11] = 0x74;
        data[18] = 1; data[19] = 4; data[126] = 1;
        data[128] = 2; data[129] = 3; data[130] = 18;
        byte[] forum = [0x6D, 0xD8, 0x5D, 0xC4, 1, 120, 0xC8, 0x5B, 0, 0, 0, 0x87, 0x54, 0x28];
        forum.CopyTo(data, 132);
        for (var block = 0; block < 2; block++)
            data[block * 128 + 127] = unchecked((byte)-data.AsSpan(block * 128, 127).ToArray().Sum(x => (int)x));
        return data;
    }

    private static PatchBackup LegacyZeroBoundaryBackup()
    {
        byte[] original = Edid();
        original[130] = 0;
        SetChecksum(original, 1);

        byte[] patched = original.ToArray();
        patched[139] = (byte)((patched[139] & 0x0F) | 0x40);
        patched[144] = (byte)((patched[144] & 0x0F) | 0x40);
        SetChecksum(patched, 1);

        return new PatchBackup(
            1,
            Guid.NewGuid().ToString("N"),
            Instance,
            original,
            OverrideState.Empty,
            new OverrideState(true, new() { [1] = patched[128..256] }),
            "Applied");
    }

    private static void SetChecksum(byte[] edid, int blockNumber)
    {
        int blockStart = blockNumber * 128;
        edid[blockStart + 127] = unchecked((byte)-edid.AsSpan(blockStart, 127).ToArray().Sum(x => (int)x));
    }
}
