using OpenBlade.HdmiFix.Core;
using Xunit;

namespace OpenBlade.HdmiFix.Tests;

public sealed class BackupInventoryTests
{
    private const string Instance = @"DISPLAY\SAM7463\5&inventory&0&UID1";

    [Fact]
    public void FinishedHistoryBeyondOldLimitDoesNotHideActiveRecovery()
    {
        var active = Backup();
        var records = Enumerable.Range(0, 513).Select(_ => Backup() with { Stage = "Restored" })
            .Append(active).ToDictionary(b => b.Id);

        var inventory = BackupInventory.Read(records.Keys, id => records[id]);

        Assert.Same(active, Assert.Single(inventory.Records));
        Assert.True(inventory.AllowsNewBackup);
        Assert.Same(active, Assert.Single(inventory.RecoverableFor(Current(active))));
    }

    [Theory]
    [InlineData(511, true)]
    [InlineData(512, false)]
    [InlineData(513, false)]
    public void ActiveLimitBlocksNewWorkButPreservesEveryRecovery(int count, bool allowsNew)
    {
        var records = Enumerable.Range(0, count).Select(n => Backup() with
        {
            InstanceId = @"DISPLAY\SAM7463\5&inventory&0&UID" + n
        }).ToDictionary(b => b.Id);
        var selected = records.Values.Last();

        var inventory = BackupInventory.Read(records.Keys, id => records[id]);

        Assert.Equal(count, inventory.Records.Count);
        Assert.Equal(allowsNew, inventory.AllowsNewBackup);
        Assert.Same(selected, Assert.Single(inventory.RecoverableFor(Current(selected))));
    }

    [Fact]
    public void PrivatePayloadBlocksPatchWithoutHidingOwnRecovery()
    {
        var own = Backup();
        var inventory = BackupInventory.Read(["private", own.Id], id => id == "private"
            ? throw new UnauthorizedAccessException() : own);

        Assert.True(inventory.HasPrivateBackups);
        Assert.False(inventory.AllowsNewBackup);
        Assert.Same(own, Assert.Single(inventory.RecoverableFor(Current(own))));
    }

    [Fact]
    public void InvalidPayloadIsNotSilentlyIgnored()
    {
        Assert.Throws<InvalidDataException>(() => BackupInventory.Read(["invalid"],
            _ => throw new InvalidDataException("Corrupt original")));
    }

    [Fact]
    public void ChangedEdidIdentifiesOwnedOverrideWithoutAllowingRestore()
    {
        var backup = Backup();
        var inventory = BackupInventory.Read([backup.Id], _ => backup);
        var original = Current(backup);
        var changed = original with { Edid = original.Edid.ToArray() };
        changed.Edid[12] ^= 1;

        Assert.True(inventory.HasChangedEdid(changed));
        Assert.Empty(inventory.RecoverableFor(changed));
        changed.Overrides.Blocks[1][20] ^= 1;
        Assert.False(inventory.HasChangedEdid(changed));
        Assert.False(inventory.HasChangedEdid(original with { InstanceId = @"DISPLAY\SAM7463\other" }));
    }

    private static MonitorState Current(PatchBackup backup) =>
        new(backup.InstanceId, backup.OriginalEdid.ToArray(), backup.PatchedOverrides.Copy());

    private static PatchBackup Backup()
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
        var plan = EdidPatchPlanner.Plan(data);
        return new(1, Guid.NewGuid().ToString("N"), Instance, data, OverrideState.Empty,
            OverrideOperations.BuildPatchedState(OverrideState.Empty, plan), "Applied");
    }
}
