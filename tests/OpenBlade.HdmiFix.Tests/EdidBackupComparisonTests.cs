using OpenBlade.HdmiFix.Core;
using Xunit;

namespace OpenBlade.HdmiFix.Tests;

public sealed class EdidBackupComparisonTests
{
    private const string Instance = @"DISPLAY\SAM74AF\5&comparison&0&UID1";

    [Fact]
    public void ExactOriginal_ReportsFullEdidAndOriginalOverrideMatch()
    {
        PatchBackup backup = Backup();
        var state = new MonitorState(Instance, backup.OriginalEdid.ToArray(), backup.OriginalOverrides.Copy());

        EdidBackupComparisonResult result = EdidBackupComparison.Compare(state, [backup]);

        Assert.Equal(EdidBackupComparisonKind.ExactOriginal, result.Kind);
        Assert.True(result.EdidMatchesOriginal);
        Assert.False(result.EdidMatchesExpectedPatched);
        Assert.True(result.CurrentOverridesMatchOriginal);
        Assert.False(result.CurrentOverridesMatchPatched);
        Assert.Empty(result.FromOriginal!.ChangedBlockNumbers);
        Assert.Contains("original backup", result.Summary);
    }

    [Fact]
    public void ExactExpectedPatched_RequiresEveryByteAndReportsOverrideIndependently()
    {
        PatchBackup backup = Backup();
        byte[] expected = ExpectedPatched(backup);
        var state = new MonitorState(Instance, expected, backup.PatchedOverrides.Copy());

        EdidBackupComparisonResult result = EdidBackupComparison.Compare(state, [backup]);

        Assert.Equal(EdidBackupComparisonKind.ExactExpectedPatched, result.Kind);
        Assert.False(result.EdidMatchesOriginal);
        Assert.True(result.EdidMatchesExpectedPatched);
        Assert.True(result.CurrentOverridesMatchPatched);
        Assert.Equal([1], result.FromOriginal!.ChangedBlockNumbers);
        Assert.Equal(3, result.FromOriginal.ChangedByteCount);
        Assert.Contains("Windows caching", result.Summary);
        Assert.Contains("active driver's EDID", result.ToDiagnosticText());
    }

    [Fact]
    public void ExpectedEdidMatch_WithForeignOverrideDoesNotAttributeTheChange()
    {
        PatchBackup backup = Backup();
        OverrideState foreign = backup.PatchedOverrides.Copy();
        foreign.Blocks[1][20] ^= 1;
        SetChecksum(foreign.Blocks[1], 0);

        EdidBackupComparisonResult result = EdidBackupComparison.Compare(
            new MonitorState(Instance, ExpectedPatched(backup), foreign), [backup]);

        Assert.Equal(EdidBackupComparisonKind.ExactExpectedPatched, result.Kind);
        Assert.True(result.EdidMatchesExpectedPatched);
        Assert.False(result.CurrentOverridesMatchOriginal);
        Assert.False(result.CurrentOverridesMatchPatched);
        Assert.DoesNotContain("consistent with Windows caching", result.Summary);
        Assert.Contains("differs from both saved states", result.Summary);
    }

    [Fact]
    public void SerialOnlyDifference_IsReportedWithoutSerialValueOrRawBytes()
    {
        PatchBackup backup = Backup();
        byte[] changed = backup.OriginalEdid.ToArray();
        changed[12] ^= 1;
        SetChecksum(changed, 0);

        EdidBackupComparisonResult result = EdidBackupComparison.Compare(
            new MonitorState(Instance, changed, backup.OriginalOverrides.Copy()), [backup]);
        string report = result.ToDiagnosticText();

        Assert.Equal(EdidBackupComparisonKind.OtherChanges, result.Kind);
        Assert.True(result.FromOriginal!.SerialAreaChanged);
        Assert.False(result.FromOriginal.BaseIdentityChanged);
        Assert.Equal([0], result.FromOriginal.ChangedBlockNumbers);
        Assert.Equal(2, result.FromOriginal.ChangedByteCount);
        Assert.DoesNotContain(Instance, report);
        Assert.DoesNotContain(backup.Id, report);
        Assert.DoesNotContain(Convert.ToHexString(changed), report);
    }

    [Fact]
    public void OtherBlockAndLongerValidEdid_ReportChangedAndAddedBlocks()
    {
        PatchBackup backup = Backup();
        byte[] changed = new byte[backup.OriginalEdid.Length + 128];
        backup.OriginalEdid.CopyTo(changed, 0);
        changed[134] = 4;
        changed[300] ^= 1;
        changed[^128] = 0x70;
        SetChecksum(changed, 0);
        SetChecksum(changed, 1);
        SetChecksum(changed, 2);
        SetChecksum(changed, 4);

        EdidBackupComparisonResult result = EdidBackupComparison.Compare(
            new MonitorState(Instance, changed, backup.OriginalOverrides.Copy()), [backup]);

        Assert.Equal(EdidBackupComparisonKind.OtherChanges, result.Kind);
        Assert.Equal([1, 2, 4], result.FromOriginal!.ChangedBlockNumbers);
        Assert.Equal(128, result.FromOriginal.AddedByteCount);
        Assert.Equal(0, result.FromOriginal.RemovedByteCount);
        Assert.Equal(changed.Length, result.CurrentLength);
        Assert.False(result.FromOriginal.BaseIdentityChanged);
    }

    [Fact]
    public void ShorterValidEdid_ReportsRemovedTrailingBlocks()
    {
        PatchBackup backup = Backup();
        byte[] shorter = backup.OriginalEdid[..256];
        shorter[130] = 4;
        Array.Clear(shorter, 132, 123);
        SetChecksum(shorter, 1);

        EdidBackupComparisonResult result = EdidBackupComparison.Compare(
            new MonitorState(Instance, shorter, backup.OriginalOverrides.Copy()), [backup]);

        Assert.Equal(EdidBackupComparisonKind.OtherChanges, result.Kind);
        Assert.Equal([1, 2, 3], result.FromOriginal!.ChangedBlockNumbers);
        Assert.Equal(256, result.FromOriginal.RemovedByteCount);
        Assert.Equal(0, result.FromOriginal.AddedByteCount);
    }

    [Fact]
    public void MissingAndDifferentInstanceBackupsAreUnavailable()
    {
        PatchBackup backup = Backup();
        var state = new MonitorState(Instance, backup.OriginalEdid.ToArray(), OverrideState.Empty);

        EdidBackupComparisonResult missing = EdidBackupComparison.Compare(state, []);
        EdidBackupComparisonResult otherInstance = EdidBackupComparison.Compare(state,
            [backup with { InstanceId = @"DISPLAY\SAM74AF\5&comparison&0&UID2" }]);

        Assert.Equal(EdidBackupComparisonUnavailableReason.NoEligibleBackup, missing.UnavailableReason);
        Assert.Equal(EdidBackupComparisonUnavailableReason.NoEligibleBackup, otherInstance.UnavailableReason);
    }

    [Fact]
    public void MultipleExactInstanceBackupsAreAmbiguousEvenWhenIdentical()
    {
        PatchBackup backup = Backup();
        PatchBackup duplicate = backup with { Id = Guid.NewGuid().ToString("N") };
        var state = new MonitorState(Instance, backup.OriginalEdid.ToArray(), OverrideState.Empty);

        EdidBackupComparisonResult result = EdidBackupComparison.Compare(state, [backup, duplicate]);

        Assert.Equal(EdidBackupComparisonKind.Unavailable, result.Kind);
        Assert.Equal(EdidBackupComparisonUnavailableReason.AmbiguousBackups, result.UnavailableReason);
    }

    [Fact]
    public void InvalidExactInstanceBackupIsNotUsed()
    {
        PatchBackup backup = Backup();
        OverrideState tampered = backup.PatchedOverrides.Copy();
        tampered.Blocks[1][20] ^= 1;
        SetChecksum(tampered.Blocks[1], 0);
        PatchBackup invalid = backup with { PatchedOverrides = tampered };
        var state = new MonitorState(Instance, backup.OriginalEdid.ToArray(), OverrideState.Empty);

        EdidBackupComparisonResult result = EdidBackupComparison.Compare(state, [invalid]);

        Assert.Equal(EdidBackupComparisonKind.Unavailable, result.Kind);
        Assert.Equal(EdidBackupComparisonUnavailableReason.InvalidBackup, result.UnavailableReason);
    }

    [Fact]
    public void InvalidOrForeignCurrentEdidCannotProduceAConclusiveMatch()
    {
        PatchBackup backup = Backup();
        byte[] invalid = backup.OriginalEdid.ToArray();
        invalid[20] ^= 1;
        byte[] foreign = EdidExtensionCountTests.Fixture(product: 0x7463);

        EdidBackupComparisonResult invalidResult = EdidBackupComparison.Compare(
            new MonitorState(Instance, invalid, OverrideState.Empty), [backup]);
        EdidBackupComparisonResult foreignResult = EdidBackupComparison.Compare(
            new MonitorState(Instance, foreign, OverrideState.Empty), [backup]);

        Assert.Equal(EdidBackupComparisonUnavailableReason.CurrentEdidInvalid, invalidResult.UnavailableReason);
        Assert.Equal(EdidBackupComparisonUnavailableReason.CurrentEdidIdentityMismatch, foreignResult.UnavailableReason);
        Assert.False(invalidResult.EdidMatchesOriginal);
        Assert.False(foreignResult.EdidMatchesOriginal);
    }

    [Fact]
    public void ComparisonDoesNotWeakenRestoreGateOrMutateInputs()
    {
        PatchBackup backup = Backup();
        byte[] changed = backup.OriginalEdid.ToArray();
        changed[300] ^= 1;
        SetChecksum(changed, 2);
        var state = new MonitorState(Instance, changed, backup.PatchedOverrides.Copy());
        byte[] currentBefore = state.Edid.ToArray();
        byte[] originalBefore = backup.OriginalEdid.ToArray();
        byte[] patchBefore = backup.PatchedOverrides.Blocks[1].ToArray();

        EdidBackupComparisonResult result = EdidBackupComparison.Compare(state, [backup]);

        Assert.Equal(EdidBackupComparisonKind.OtherChanges, result.Kind);
        Assert.False(OverrideOperations.CanRestore(state, backup));
        Assert.Equal(currentBefore, state.Edid);
        Assert.Equal(originalBefore, backup.OriginalEdid);
        Assert.Equal(patchBefore, backup.PatchedOverrides.Blocks[1]);
    }

    [Fact]
    public void LegacyVersion1BackupRemainsComparableWithoutBroadeningCurrentPlanner()
    {
        PatchBackup backup = LegacyBackup();
        byte[] expected = ExpectedPatched(backup);

        Assert.Equal(PatchDisposition.Invalid, EdidPatchPlanner.Plan(backup.OriginalEdid).Disposition);
        EdidBackupComparisonResult result = EdidBackupComparison.Compare(
            new MonitorState(backup.InstanceId, expected, backup.PatchedOverrides.Copy()), [backup]);

        Assert.Equal(EdidBackupComparisonKind.ExactExpectedPatched, result.Kind);
        Assert.True(result.CurrentOverridesMatchPatched);
    }

    private static PatchBackup Backup()
    {
        byte[] original = EdidExtensionCountTests.Fixture();
        EdidPatchPlan plan = EdidPatchPlanner.Plan(original);
        Assert.Equal(PatchDisposition.Ready, plan.Disposition);
        return new PatchBackup(
            1,
            Guid.NewGuid().ToString("N"),
            Instance,
            original,
            OverrideState.Empty,
            OverrideOperations.BuildPatchedState(OverrideState.Empty, plan),
            "Applied");
    }

    private static PatchBackup LegacyBackup()
    {
        const string legacyInstance = @"DISPLAY\SAM7463\5&comparison&0&UID1";
        var original = new byte[256];
        byte[] header = [0, 255, 255, 255, 255, 255, 255, 0];
        header.CopyTo(original, 0);
        original[8] = 0x4C;
        original[9] = 0x2D;
        original[10] = 0x63;
        original[11] = 0x74;
        original[18] = 1;
        original[19] = 4;
        original[126] = 1;
        original[128] = 2;
        original[129] = 3;
        original[130] = 0;
        byte[] forum = [0x6D, 0xD8, 0x5D, 0xC4, 1, 120, 0xC8, 0x5B, 0, 0, 0, 0x87, 0x54, 0x28];
        forum.CopyTo(original, 132);
        SetChecksum(original, 0);
        SetChecksum(original, 1);
        byte[] patched = original.ToArray();
        patched[139] = (byte)((patched[139] & 0x0F) | 0x40);
        patched[144] = (byte)((patched[144] & 0x0F) | 0x40);
        SetChecksum(patched, 1);
        return new PatchBackup(
            1,
            Guid.NewGuid().ToString("N"),
            legacyInstance,
            original,
            OverrideState.Empty,
            new OverrideState(true, new() { [1] = patched[128..256] }),
            "Applied");
    }

    private static byte[] ExpectedPatched(PatchBackup backup)
    {
        byte[] expected = backup.OriginalEdid.ToArray();
        foreach ((int blockNumber, byte[] block) in backup.PatchedOverrides.Blocks)
            block.CopyTo(expected, blockNumber * 128);
        return expected;
    }

    private static void SetChecksum(byte[] edid, int blockNumber)
    {
        int start = blockNumber * 128;
        edid[start + 127] = unchecked((byte)-edid.AsSpan(start, 127).ToArray().Sum(x => (int)x));
    }
}
