using OpenBlade.HdmiFix.Core;
using Xunit;

namespace OpenBlade.HdmiFix.Tests;

public sealed class EdidPatchTests
{
    [Fact]
    public void Plan_TargetEdid_PatchesOnlyFrlNibblesAndAffectedChecksum()
    {
        byte[] videoBlock = [0x42, 0x61, 0x10];
        byte[] unknownExtendedBlock = [0xE2, 0x7E, 0xA5];
        byte[] edid = CreateEdid(
            forumDataBlock: CreateForumDataBlock(),
            leadingDataBlocks: [videoBlock],
            trailingDataBlocks: [unknownExtendedBlock],
            includeUnknownExtension: true);
        byte[] inputSnapshot = edid.ToArray();
        int forumOffset = FindForumDataBlockOffset(edid);
        byte oldChecksum = edid[255];

        EdidPatchPlan plan = EdidPatchPlanner.Plan(edid);

        Assert.Equal(PatchDisposition.Ready, plan.Disposition);
        Assert.Equal([1], plan.ChangedBlocks);
        Assert.Equal(inputSnapshot, plan.Original);
        Assert.Equal(inputSnapshot, edid);
        Assert.NotSame(edid, plan.Original);
        Assert.NotSame(plan.Original, plan.Patched);
        Assert.Equal(0x4B, plan.Patched[forumOffset + 7]);
        Assert.Equal(0x4A, plan.Patched[forumOffset + 12]);
        Assert.Equal((byte)(oldChecksum + 0x20), plan.Patched[255]);
        AssertChecksum(plan.Patched, 1);

        int[] changedOffsets = Enumerable.Range(0, inputSnapshot.Length)
            .Where(index => inputSnapshot[index] != plan.Patched[index])
            .ToArray();
        Assert.Equal([forumOffset + 7, forumOffset + 12, 255], changedOffsets);
        Assert.Equal(inputSnapshot.AsSpan(256, 128).ToArray(), plan.Patched.AsSpan(256, 128).ToArray());
    }

    [Fact]
    public void Plan_AlreadyCapped_IsUnchangedAndIdempotent()
    {
        byte[] edid = CreateEdid(CreateForumDataBlock());
        EdidPatchPlan first = EdidPatchPlanner.Plan(edid);

        EdidPatchPlan second = EdidPatchPlanner.Plan(first.Patched);

        Assert.Equal(PatchDisposition.Ready, first.Disposition);
        Assert.Equal(PatchDisposition.AlreadyCapped, second.Disposition);
        Assert.Empty(second.ChangedBlocks);
        Assert.Equal(first.Patched, second.Original);
        Assert.Equal(first.Patched, second.Patched);
    }

    [Fact]
    public void Plan_FindsForumBlockAfterOtherStructuredDataBlocks()
    {
        byte[] audioBlock = [0x23, 0x09, 0x07, 0x07];
        byte[] speakerBlock = [0x83, 0x01, 0x00, 0x00];
        byte[] edid = CreateEdid(
            CreateForumDataBlock(),
            leadingDataBlocks: [audioBlock, speakerBlock]);

        EdidPatchPlan plan = EdidPatchPlanner.Plan(edid);

        Assert.Equal(PatchDisposition.Ready, plan.Disposition);
        Assert.Equal(12, FindForumDataBlockOffset(edid) - 128);
    }

    [Fact]
    public void Plan_DoesNotTreatMatchingBytesOutsideDataBlockCollectionAsForumBlock()
    {
        byte[] edid = CreateEdid(forumDataBlock: null);
        int extensionStart = 128;
        edid[extensionStart + 2] = 4;
        byte[] lookalike = CreateForumDataBlock();
        lookalike.CopyTo(edid, extensionStart + 20);
        SetChecksum(edid, 1);

        EdidPatchPlan plan = EdidPatchPlanner.Plan(edid);

        Assert.Equal(PatchDisposition.Unsupported, plan.Disposition);
        Assert.Equal(edid, plan.Patched);
    }

    [Fact]
    public void Plan_InvalidHeader_ReturnsInvalidWithoutThrowing()
    {
        byte[] edid = CreateEdid(CreateForumDataBlock());
        edid[0] = 0x01;
        SetChecksum(edid, 0);

        EdidPatchPlan plan = EdidPatchPlanner.Plan(edid);

        Assert.Equal(PatchDisposition.Invalid, plan.Disposition);
        Assert.Equal(edid, plan.Patched);
    }

    [Fact]
    public void Plan_TruncatedEdid_ReturnsInvalidWithoutThrowing()
    {
        byte[] truncated = CreateEdid(CreateForumDataBlock())[..200];

        EdidPatchPlan plan = EdidPatchPlanner.Plan(truncated);

        Assert.Equal(PatchDisposition.Invalid, plan.Disposition);
        Assert.Equal(truncated, plan.Original);
        Assert.Equal(truncated, plan.Patched);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Plan_BadChecksumInAnyBlock_ReturnsInvalid(int blockNumber)
    {
        byte[] edid = CreateEdid(CreateForumDataBlock());
        edid[(blockNumber * 128) + 20] ^= 0x01;

        EdidPatchPlan plan = EdidPatchPlanner.Plan(edid);

        Assert.Equal(PatchDisposition.Invalid, plan.Disposition);
    }

    [Fact]
    public void Plan_DeclaredExtensionCountMismatch_ReturnsInvalid()
    {
        byte[] edid = CreateEdid(CreateForumDataBlock());
        edid[126] = 2;
        SetChecksum(edid, 0);

        Assert.Equal(PatchDisposition.Invalid, EdidPatchPlanner.Plan(edid).Disposition);
    }

    [Fact]
    public void Plan_InvalidCtaBoundary_ReturnsInvalid()
    {
        byte[] edid = CreateEdid(CreateForumDataBlock());
        edid[130] = 3;
        SetChecksum(edid, 1);

        Assert.Equal(PatchDisposition.Invalid, EdidPatchPlanner.Plan(edid).Disposition);
    }

    [Fact]
    public void Plan_ZeroCtaBoundaryWithEmptyPayload_ReturnsUnsupported()
    {
        byte[] edid = CreateEdid(forumDataBlock: null);
        edid[130] = 0;
        SetChecksum(edid, 1);

        Assert.Equal(PatchDisposition.Unsupported, EdidPatchPlanner.Plan(edid).Disposition);
    }

    [Fact]
    public void Plan_ZeroCtaBoundaryWithNonzeroPayload_ReturnsInvalid()
    {
        byte[] edid = CreateEdid(CreateForumDataBlock());
        edid[130] = 0;
        SetChecksum(edid, 1);

        EdidPatchPlan plan = EdidPatchPlanner.Plan(edid);

        Assert.Equal(PatchDisposition.Invalid, plan.Disposition);
        Assert.Equal(edid, plan.Patched);
    }

    [Fact]
    public void Plan_EmptyZeroBoundaryCtaAmongMultipleExtensions_DoesNotHideValidBlock()
    {
        byte[] edid = CreateEdid(CreateForumDataBlock(), includeUnknownExtension: true);
        Array.Clear(edid, 256, 128);
        edid[256] = 0x02;
        edid[257] = 0x03;
        SetChecksum(edid, 2);

        EdidPatchPlan plan = EdidPatchPlanner.Plan(edid);

        Assert.Equal(PatchDisposition.Ready, plan.Disposition);
        Assert.Equal([1], plan.ChangedBlocks);
    }

    [Fact]
    public void Plan_ZeroBoundaryPhantomBlockInLaterExtension_ReturnsInvalid()
    {
        byte[] edid = CreateEdid(forumDataBlock: null, includeUnknownExtension: true);
        Array.Clear(edid, 256, 128);
        edid[256] = 0x02;
        edid[257] = 0x03;
        CreateForumDataBlock().CopyTo(edid, 260);
        SetChecksum(edid, 2);

        Assert.Equal(PatchDisposition.Invalid, EdidPatchPlanner.Plan(edid).Disposition);
    }

    [Fact]
    public void Plan_TruncatedCtaDataBlock_ReturnsInvalid()
    {
        byte[] edid = CreateEdid(CreateForumDataBlock());
        int forumOffset = FindForumDataBlockOffset(edid);
        edid[forumOffset] = (byte)((3 << 5) | 31);
        SetChecksum(edid, 1);

        Assert.Equal(PatchDisposition.Invalid, EdidPatchPlanner.Plan(edid).Disposition);
    }

    [Theory]
    [InlineData(0x1E6D, 0x5B09, "GSM5B09")]
    [InlineData(0x10AC, 0xA1B2, "DELA1B2")]
    [InlineData(0x4C2D, 0x7464, "SAM7464")]
    public void Plan_OtherMonitorModels_PatchTheirOwnStructuredCapabilities(int manufacturer, int product, string model)
    {
        byte[] edid = CreateEdid(CreateForumDataBlock(), manufacturer: (ushort)manufacturer, product: (ushort)product);
        EdidPatchPlan plan = EdidPatchPlanner.Plan(edid);
        Assert.Equal(PatchDisposition.Ready, plan.Disposition);
        Assert.Equal(model, EdidPatchPlanner.MonitorModelId(edid));
        Assert.Equal(edid[..128], plan.Patched[..128]);
        Assert.Equal([1], plan.ChangedBlocks);
        Assert.Equal(3, edid.Zip(plan.Patched).Count(pair => pair.First != pair.Second));
        AssertChecksum(plan.Patched, 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x7FFF)]
    [InlineData(0xCC2D)]
    public void Plan_InvalidManufacturerEncoding_IsRefused(int manufacturer)
    {
        byte[] edid = CreateEdid(CreateForumDataBlock(), manufacturer: (ushort)manufacturer);
        Assert.Equal(PatchDisposition.Invalid, EdidPatchPlanner.Plan(edid).Disposition);
        Assert.Empty(EdidPatchPlanner.MonitorModelId(edid));
    }

    [Fact]
    public void Plan_OtherManufacturer_StillRejectsUnsafeCapabilities()
    {
        byte[] noDsc = CreateEdid(CreateForumDataBlock(dscEnabled: false), manufacturer: 0x1E6D, product: 0x5B09);
        byte[] wrongRate = CreateEdid(CreateForumDataBlock(6, 6), manufacturer: 0x1E6D, product: 0x5B09);
        byte[] duplicate = CreateEdid(CreateForumDataBlock(), trailingDataBlocks: [CreateForumDataBlock()], manufacturer: 0x1E6D, product: 0x5B09);
        Assert.All(new[] { noDsc, wrongRate, duplicate }, edid =>
        {
            var plan = EdidPatchPlanner.Plan(edid);
            Assert.Equal(PatchDisposition.Unsupported, plan.Disposition);
            Assert.Equal(edid, plan.Patched);
        });
        byte[] corrupt = CreateEdid(CreateForumDataBlock(), manufacturer: 0x1E6D, product: 0x5B09);
        corrupt[200] ^= 1;
        Assert.Equal(PatchDisposition.Invalid, EdidPatchPlanner.Plan(corrupt).Disposition);
        byte[] badBoundary = CreateEdid(CreateForumDataBlock(), manufacturer: 0x1E6D, product: 0x5B09);
        badBoundary[130] = 3;
        SetChecksum(badBoundary, 1);
        Assert.Equal(PatchDisposition.Invalid, EdidPatchPlanner.Plan(badBoundary).Disposition);
    }

    [Fact]
    public void Plan_WithoutDscSupport_ReturnsUnsupported()
    {
        byte[] edid = CreateEdid(CreateForumDataBlock(dscEnabled: false));

        Assert.Equal(PatchDisposition.Unsupported, EdidPatchPlanner.Plan(edid).Disposition);
    }

    [Theory]
    [InlineData(4, 5)]
    [InlineData(5, 4)]
    [InlineData(6, 6)]
    [InlineData(0, 0)]
    public void Plan_UnexpectedOrInconsistentRateFields_ReturnUnsupported(byte normalRate, byte dscRate)
    {
        byte[] edid = CreateEdid(CreateForumDataBlock(normalRate, dscRate));

        EdidPatchPlan plan = EdidPatchPlanner.Plan(edid);

        Assert.Equal(PatchDisposition.Unsupported, plan.Disposition);
        Assert.Equal(edid, plan.Patched);
    }

    [Fact]
    public void Plan_MultipleForumBlocks_ReturnsUnsupported()
    {
        byte[] secondForumBlock = CreateForumDataBlock();
        byte[] edid = CreateEdid(
            CreateForumDataBlock(),
            trailingDataBlocks: [secondForumBlock]);

        Assert.Equal(PatchDisposition.Unsupported, EdidPatchPlanner.Plan(edid).Disposition);
    }

    [Fact]
    public void Plan_AlternativeForumCapabilityBlock_ReturnsUnsupported()
    {
        byte[] forumScdb = [0xE7, 0x79, 0x01, 0x78, 0xC8, 0x50, 0x00, 0x00];
        byte[] edid = CreateEdid(
            CreateForumDataBlock(),
            trailingDataBlocks: [forumScdb]);

        Assert.Equal(PatchDisposition.Unsupported, EdidPatchPlanner.Plan(edid).Disposition);
    }

    [Fact]
    public void Plan_TooShortForumBlock_ReturnsUnsupported()
    {
        byte[] shortForumBlock = [0x66, 0xD8, 0x5D, 0xC4, 0x01, 0x78, 0xC8];
        byte[] edid = CreateEdid(shortForumBlock);

        Assert.Equal(PatchDisposition.Unsupported, EdidPatchPlanner.Plan(edid).Disposition);
    }

    [Fact]
    public void MonitorName_ReadsBaseBlockDescriptor()
    {
        byte[] edid = CreateEdid(CreateForumDataBlock(), monitorName: "Odyssey G93SC");

        Assert.Equal("Odyssey G93SC", EdidPatchPlanner.MonitorName(edid));
    }

    [Fact]
    public void MonitorName_InvalidEdid_ReturnsEmpty()
    {
        byte[] edid = CreateEdid(CreateForumDataBlock(), monitorName: "Odyssey G93SC");
        edid[200] ^= 0x01;

        Assert.Equal(string.Empty, EdidPatchPlanner.MonitorName(edid));
        Assert.Equal(string.Empty, EdidPatchPlanner.MonitorName([]));
    }

    private static byte[] CreateEdid(
        byte[]? forumDataBlock,
        IReadOnlyList<byte[]>? leadingDataBlocks = null,
        IReadOnlyList<byte[]>? trailingDataBlocks = null,
        ushort manufacturer = 0x4C2D,
        ushort product = 0x7463,
        bool includeUnknownExtension = false,
        string monitorName = "Odyssey G93SC")
    {
        int extensionCount = includeUnknownExtension ? 2 : 1;
        byte[] edid = new byte[(extensionCount + 1) * 128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(edid, 0);
        edid[8] = (byte)(manufacturer >> 8);
        edid[9] = (byte)manufacturer;
        edid[10] = (byte)product;
        edid[11] = (byte)(product >> 8);
        edid[18] = 1;
        edid[19] = 4;
        WriteMonitorNameDescriptor(edid, monitorName);
        edid[126] = (byte)extensionCount;
        SetChecksum(edid, 0);

        int extensionStart = 128;
        edid[extensionStart] = 0x02;
        edid[extensionStart + 1] = 0x03;
        int cursor = extensionStart + 4;
        CopyDataBlocks(edid, ref cursor, leadingDataBlocks);
        if (forumDataBlock is not null)
        {
            forumDataBlock.CopyTo(edid, cursor);
            cursor += forumDataBlock.Length;
        }

        CopyDataBlocks(edid, ref cursor, trailingDataBlocks);
        edid[extensionStart + 2] = (byte)(cursor - extensionStart);
        SetChecksum(edid, 1);

        if (includeUnknownExtension)
        {
            int unknownStart = 256;
            edid[unknownStart] = 0x70;
            edid[unknownStart + 1] = 0x20;
            edid[unknownStart + 14] = 0xD8;
            edid[unknownStart + 15] = 0x5D;
            edid[unknownStart + 16] = 0xC4;
            edid[unknownStart + 17] = 0x5B;
            edid[unknownStart + 22] = 0x5A;
            SetChecksum(edid, 2);
        }

        return edid;
    }

    private static byte[] CreateForumDataBlock(
        byte normalRate = 5,
        byte dscRate = 5,
        bool dscEnabled = true)
        =>
        [
            0x6C,
            0xD8, 0x5D, 0xC4,
            0x01,
            0x78,
            0xC8,
            (byte)((normalRate << 4) | 0x0B),
            0x07,
            0x00,
            0x00,
            (byte)((dscEnabled ? 0x80 : 0x00) | 0x0F),
            (byte)((dscRate << 4) | 0x0A),
        ];

    private static void CopyDataBlocks(byte[] destination, ref int cursor, IReadOnlyList<byte[]>? blocks)
    {
        if (blocks is null)
        {
            return;
        }

        foreach (byte[] block in blocks)
        {
            block.CopyTo(destination, cursor);
            cursor += block.Length;
        }
    }

    private static void WriteMonitorNameDescriptor(byte[] edid, string monitorName)
    {
        int offset = 54;
        edid[offset + 3] = 0xFC;
        Span<byte> nameBytes = edid.AsSpan(offset + 5, 13);
        nameBytes.Fill(0x20);
        int length = Math.Min(monitorName.Length, nameBytes.Length);
        for (int index = 0; index < length; index++)
        {
            nameBytes[index] = (byte)monitorName[index];
        }

        if (length < nameBytes.Length)
        {
            nameBytes[length] = 0x0A;
        }
    }

    private static int FindForumDataBlockOffset(byte[] edid)
    {
        int cursor = 132;
        int end = 128 + edid[130];
        while (cursor < end)
        {
            int payloadLength = edid[cursor] & 0x1F;
            if ((edid[cursor] >> 5) == 3 &&
                payloadLength >= 3 &&
                edid[cursor + 1] == 0xD8 &&
                edid[cursor + 2] == 0x5D &&
                edid[cursor + 3] == 0xC4)
            {
                return cursor;
            }

            cursor += payloadLength + 1;
        }

        throw new InvalidOperationException("Fixture does not contain an HDMI Forum data block.");
    }

    private static void AssertChecksum(byte[] edid, int blockNumber)
    {
        int sum = edid.AsSpan(blockNumber * 128, 128).ToArray().Sum(value => value);
        Assert.Equal(0, sum & 0xFF);
    }

    private static void SetChecksum(byte[] edid, int blockNumber)
    {
        int blockStart = blockNumber * 128;
        int sum = 0;
        for (int index = 0; index < 127; index++)
        {
            sum += edid[blockStart + index];
        }

        edid[blockStart + 127] = (byte)(0 - sum);
    }
}
