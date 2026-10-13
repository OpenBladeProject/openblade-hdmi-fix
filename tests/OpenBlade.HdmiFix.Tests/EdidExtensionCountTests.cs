using OpenBlade.HdmiFix.Core;
using Xunit;

namespace OpenBlade.HdmiFix.Tests;

public sealed class EdidExtensionCountTests
{
    [Fact]
    public void ValidOverrideAdmitsFourBlocksAndChangesOnlyFrlFieldsAndCtaChecksum()
    {
        var edid = Fixture();
        var snapshot = edid.ToArray();
        var count = EdidPatchPlanner.InspectExtensionCount(edid);
        Assert.True(count.IsValid);
        Assert.True(count.HasOverride);
        Assert.Equal(1, count.Declared);
        Assert.Equal(3, count.Effective);
        Assert.Equal(512, count.ExpectedBytes);
        var plan = EdidPatchPlanner.Plan(edid);
        Assert.Equal(PatchDisposition.Ready, plan.Disposition);
        Assert.Equal("SAM74AF", EdidPatchPlanner.MonitorModelId(edid));
        Assert.Equal([1], plan.ChangedBlocks);
        Assert.Equal(snapshot, edid);
        Assert.Equal(edid[..128], plan.Patched[..128]);
        Assert.Equal(edid[256..], plan.Patched[256..]);
        Assert.Equal(edid[132..135], plan.Patched[132..135]);
        Assert.Equal([142, 147, 255], Enumerable.Range(0, 512).Where(i => edid[i] != plan.Patched[i]).ToArray());
        Assert.Equal(PatchDisposition.AlreadyCapped, EdidPatchPlanner.Plan(plan.Patched).Disposition);
        Assert.Contains("HDMI Forum extension-count override: present", EdidPatchPlanner.InspectionText(edid));
        Assert.Contains("Effective extension blocks: 3; expected bytes: 512", EdidPatchPlanner.InspectionText(edid));
        Assert.Contains("Effective block count: matches", EdidPatchPlanner.InspectionText(edid));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(255)]
    public void InvalidOrMismatchedCountNeverTrimsOrAdmitsData(int count)
    {
        var edid = Fixture();
        edid[134] = (byte)count;
        Checksum(edid, 1);
        AssertRejectedUnchanged(edid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryStoredBlockMustHaveAValidChecksum(int block)
    {
        var edid = Fixture();
        edid[128 * block + 127] ^= 1;
        AssertRejectedUnchanged(edid);
    }

    [Fact]
    public void TrailingDisplayIdBlocksWithoutOverrideRemainRejected()
    {
        var edid = Fixture();
        edid[133] = 0x7E; // unknown extended tag, not HF-EEODB
        Checksum(edid, 1);
        Assert.False(EdidPatchPlanner.InspectExtensionCount(edid).HasOverride);
        AssertRejectedUnchanged(edid);
    }

    [Theory]
    [InlineData("wrong-length")]
    [InlineData("truncated")]
    [InlineData("misplaced")]
    [InlineData("duplicate")]
    [InlineData("other-extension")]
    [InlineData("old-revision")]
    [InlineData("zero-base")]
    [InlineData("zero-boundary")]
    [InlineData("invalid-boundary")]
    public void MalformedOverrideCannotAuthorizeExtraBlocks(string problem)
    {
        var edid = Fixture();
        switch (problem)
        {
            case "wrong-length": edid[132] = 0xE3; break;
            case "truncated": edid[130] = 6; break;
            case "misplaced":
                Array.Copy(edid, 132, edid, 133, 17);
                edid[132] = 0; edid[130]++; break;
            case "duplicate":
                new byte[] { 0xE2, 0x78, 3 }.CopyTo(edid, 149);
                edid[130] += 3; break;
            case "other-extension":
                edid[133] = 0x7E;
                edid[256] = 2; edid[257] = 3; edid[258] = 7;
                new byte[] { 0xE2, 0x78, 3 }.CopyTo(edid, 260); break;
            case "old-revision": edid[129] = 2; break;
            case "zero-base": edid[126] = 0; break;
            case "zero-boundary": edid[130] = 0; break;
            case "invalid-boundary": edid[130] = 128; break;
        }
        for (int block = 0; block < 4; block++) Checksum(edid, block);
        AssertRejectedUnchanged(edid);
    }

    [Fact]
    public void CountOverrideDoesNotWeakenIdentityOrOtherCapabilityRequirements()
    {
        var wrongIdentity = Fixture();
        Assert.Throws<InvalidDataException>(() => MonitorInstance.ValidateEdid(@"DISPLAY\SAM1234\fixture", wrongIdentity));
        wrongIdentity[8] |= 0x80;
        Checksum(wrongIdentity, 0);
        AssertRejectedUnchanged(wrongIdentity);
        var noDsc = Fixture(); noDsc[146] = 0; Checksum(noDsc, 1);
        var wrongRate = Fixture(); wrongRate[142] = 0x60; Checksum(wrongRate, 1);
        Assert.Equal(PatchDisposition.Unsupported, EdidPatchPlanner.Plan(noDsc).Disposition);
        Assert.Equal(PatchDisposition.Unsupported, EdidPatchPlanner.Plan(wrongRate).Disposition);
    }

    [Fact]
    public void LaterCtaRevisionKeepsTheExplicitCountRule()
    {
        var edid = Fixture();
        edid[129] = 4;
        Checksum(edid, 1);
        Assert.Equal(PatchDisposition.Ready, EdidPatchPlanner.Plan(edid).Disposition);
        Assert.Equal(512, EdidPatchPlanner.InspectExtensionCount(edid).ExpectedBytes);
    }

    [Fact]
    public void MissingAndPartialOverriddenBlocksAreRejected()
    {
        AssertRejectedUnchanged(Fixture()[..384]);
        AssertRejectedUnchanged(Fixture()[..511]);
    }

    [Fact]
    public void NonzeroBaseCountCanBeOverriddenWithoutInferringOrLosingData()
    {
        var edid = Fixture();
        edid[126] = 2;
        Checksum(edid, 0);
        Assert.Equal(PatchDisposition.Ready, EdidPatchPlanner.Plan(edid).Disposition);
        Assert.Equal(3, EdidPatchPlanner.InspectExtensionCount(edid).Effective);
    }

    internal static byte[] Fixture(ushort product = 0x74AF)
    {
        var edid = new byte[512];
        byte[] header = [0, 255, 255, 255, 255, 255, 255, 0];
        header.CopyTo(edid, 0);
        edid[8] = 0x4C; edid[9] = 0x2D; edid[10] = (byte)product; edid[11] = (byte)(product >> 8);
        edid[18] = 1; edid[19] = 3; edid[126] = 1;
        edid[128] = 2; edid[129] = 3; edid[130] = 21;
        byte[] data = [0xE2, 0x78, 3, 0x6D, 0xD8, 0x5D, 0xC4, 1, 0x78, 0xC8, 0x5B, 7, 0, 0, 0x8F, 0x5A, 0];
        data.CopyTo(edid, 132);
        for (int block = 2; block < 4; block++)
        {
            edid[block * 128] = 0x70;
            edid[block * 128 + 1] = 0x20;
            edid[block * 128 + 2] = 8;
            edid[block * 128 + 5] = 0x7E;
            edid[block * 128 + 7] = 5;
            edid[block * 128 + 10] = (byte)(0x30 + block);
            edid[block * 128 + 13] = unchecked((byte)-edid.Skip(block * 128 + 1).Take(12).Sum(x => (int)x));
        }
        for (int block = 0; block < 4; block++) Checksum(edid, block);
        return edid;
    }

    private static void AssertRejectedUnchanged(byte[] edid)
    {
        var before = edid.ToArray();
        var plan = EdidPatchPlanner.Plan(edid);
        Assert.Equal(PatchDisposition.Invalid, plan.Disposition);
        Assert.Equal(before, edid);
        Assert.Equal(before, plan.Patched);
        Assert.Empty(plan.ChangedBlocks);
        Assert.Empty(EdidPatchPlanner.MonitorModelId(edid));
    }

    private static void Checksum(byte[] edid, int block) => edid[block * 128 + 127]
        = unchecked((byte)-edid.Skip(block * 128).Take(127).Sum(x => (int)x));
}
