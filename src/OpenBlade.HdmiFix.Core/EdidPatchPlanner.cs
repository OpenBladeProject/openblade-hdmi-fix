using System.Text;

namespace OpenBlade.HdmiFix.Core;

public enum PatchDisposition
{
    Ready,
    AlreadyCapped,
    Unsupported,
    Invalid,
}

public sealed record EdidPatchPlan(
    PatchDisposition Disposition,
    byte[] Original,
    byte[] Patched,
    int[] ChangedBlocks,
    string Summary);

public static class EdidPatchPlanner
{
    private const int BlockSize = 128;
    private const byte CtaExtensionTag = 0x02;
    private const byte VendorSpecificDataBlockTag = 0x03;
    private const byte ExtendedDataBlockTag = 0x07;
    private const byte HdmiForumScdbExtendedTag = 0x79;
    private const byte HdmiForumOui0 = 0xD8;
    private const byte HdmiForumOui1 = 0x5D;
    private const byte HdmiForumOui2 = 0xC4;
    private const int MaximumFrlRateIndex = 7;
    private const int DscFlagsIndex = 11;
    private const int DscMaximumFrlRateIndex = 12;
    private const byte Dsc12Supported = 0x80;
    private const byte FrlRateMask = 0xF0;
    private const byte ExpectedFrlRate = 5;
    private const byte CappedFrlRate = 4;

    private static readonly byte[] EdidHeader = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    public static EdidPatchPlan Plan(byte[] edid)
        => PlanCore(edid, allowVersion1ZeroOffsetDataBlocks: false);

    // Schema version 1 backups were created before zero-offset CTA blocks were
    // excluded from patch admission. Keep that interpretation frozen here so
    // those exact backups remain recoverable without admitting new patches.
    internal static EdidPatchPlan PlanVersion1BackupRecovery(byte[] edid)
        => PlanCore(edid, allowVersion1ZeroOffsetDataBlocks: true);

    private static EdidPatchPlan PlanCore(byte[] edid, bool allowVersion1ZeroOffsetDataBlocks)
    {
        byte[] original = edid?.ToArray() ?? [];
        byte[] patched = original.ToArray();

        if (!TryValidateEdid(original, out int extensionCount, out string validationFailure))
        {
            return Result(PatchDisposition.Invalid, original, patched, validationFailure);
        }

        if (MonitorModelId(original).Length == 0)
        {
            return Result(PatchDisposition.Invalid, original, patched, "The EDID manufacturer identity is invalid.");
        }

        var forumBlocks = new List<ForumBlock>();
        bool hasAlternativeForumCapabilityBlock = false;

        for (int blockNumber = 1; blockNumber <= extensionCount; blockNumber++)
        {
            int blockStart = blockNumber * BlockSize;
            if (original[blockStart] != CtaExtensionTag)
            {
                continue;
            }

            if (!TryInspectCtaBlock(
                    original,
                    blockNumber,
                    forumBlocks,
                    ref hasAlternativeForumCapabilityBlock,
                    allowVersion1ZeroOffsetDataBlocks,
                    out string ctaFailure))
            {
                return Result(PatchDisposition.Invalid, original, patched, ctaFailure);
            }
        }

        if (hasAlternativeForumCapabilityBlock)
        {
            return Result(
                PatchDisposition.Unsupported,
                original,
                patched,
                "An HDMI Forum Sink Capability Data Block is present; its capabilities cannot be patched unambiguously.");
        }

        if (forumBlocks.Count != 1)
        {
            string summary = forumBlocks.Count == 0
                ? "The required HDMI Forum Vendor-Specific Data Block was not found."
                : "Multiple HDMI Forum Vendor-Specific Data Blocks were found.";
            return Result(PatchDisposition.Unsupported, original, patched, summary);
        }

        ForumBlock forumBlock = forumBlocks[0];
        if (forumBlock.PayloadLength < DscMaximumFrlRateIndex)
        {
            return Result(PatchDisposition.Unsupported, original, patched, "The HDMI Forum data block does not contain the required DSC capability fields.");
        }

        int blockOffset = forumBlock.AbsoluteOffset;
        if ((original[blockOffset + DscFlagsIndex] & Dsc12Supported) == 0)
        {
            return Result(PatchDisposition.Unsupported, original, patched, "The monitor EDID does not advertise DSC 1.2 support.");
        }

        byte normalRate = (byte)((original[blockOffset + MaximumFrlRateIndex] & FrlRateMask) >> 4);
        byte dscRate = (byte)((original[blockOffset + DscMaximumFrlRateIndex] & FrlRateMask) >> 4);

        if (normalRate == CappedFrlRate && dscRate == CappedFrlRate)
        {
            return Result(PatchDisposition.AlreadyCapped, original, patched, "Both HDMI FRL rate fields are already capped at 4.");
        }

        if (normalRate != ExpectedFrlRate || dscRate != ExpectedFrlRate)
        {
            return Result(
                PatchDisposition.Unsupported,
                original,
                patched,
                $"Expected both HDMI FRL rate fields to be 5, but found {normalRate} and {dscRate}.");
        }

        patched[blockOffset + MaximumFrlRateIndex] = ReplaceHighNibble(
            patched[blockOffset + MaximumFrlRateIndex],
            CappedFrlRate);
        patched[blockOffset + DscMaximumFrlRateIndex] = ReplaceHighNibble(
            patched[blockOffset + DscMaximumFrlRateIndex],
            CappedFrlRate);
        UpdateChecksum(patched, forumBlock.BlockNumber);

        return new EdidPatchPlan(
            PatchDisposition.Ready,
            original,
            patched,
            [forumBlock.BlockNumber],
            "Capped the normal and DSC HDMI FRL rate fields from 5 to 4.");
    }

    public static string MonitorModelId(byte[] edid)
    {
        if (edid is null || !TryValidateEdid(edid, out _, out _)) return string.Empty;
        int manufacturer = (edid[8] << 8) | edid[9];
        int first = (manufacturer >> 10) & 31;
        int second = (manufacturer >> 5) & 31;
        int third = manufacturer & 31;
        if ((manufacturer & 0x8000) != 0 || first is < 1 or > 26
            || second is < 1 or > 26 || third is < 1 or > 26) return string.Empty;
        int product = edid[10] | (edid[11] << 8);
        return $"{(char)('A' + first - 1)}{(char)('A' + second - 1)}{(char)('A' + third - 1)}{product:X4}";
    }

    public static string MonitorName(byte[] edid)
    {
        if (edid is null || !TryValidateEdid(edid, out _, out _))
        {
            return string.Empty;
        }

        for (int descriptorOffset = 54; descriptorOffset <= 108; descriptorOffset += 18)
        {
            if (edid[descriptorOffset] != 0 ||
                edid[descriptorOffset + 1] != 0 ||
                edid[descriptorOffset + 2] != 0 ||
                edid[descriptorOffset + 3] != 0xFC ||
                edid[descriptorOffset + 4] != 0)
            {
                continue;
            }

            ReadOnlySpan<byte> nameBytes = edid.AsSpan(descriptorOffset + 5, 13);
            int terminator = nameBytes.IndexOfAny((byte)0x00, (byte)0x0A);
            if (terminator >= 0)
            {
                nameBytes = nameBytes[..terminator];
            }

            foreach (byte value in nameBytes)
            {
                if (value is < 0x20 or > 0x7E)
                {
                    return string.Empty;
                }
            }

            return Encoding.ASCII.GetString(nameBytes).TrimEnd();
        }

        return string.Empty;
    }

    private static bool TryValidateEdid(byte[] edid, out int extensionCount, out string failure)
    {
        extensionCount = 0;
        failure = string.Empty;

        if (edid.Length < BlockSize || edid.Length % BlockSize != 0)
        {
            failure = "The EDID length is not a whole number of 128-byte blocks.";
            return false;
        }

        if (!edid.AsSpan(0, EdidHeader.Length).SequenceEqual(EdidHeader))
        {
            failure = "The EDID header is invalid.";
            return false;
        }

        extensionCount = edid[126];
        int expectedLength = checked((extensionCount + 1) * BlockSize);
        if (edid.Length != expectedLength)
        {
            failure = "The EDID length does not match its declared extension count.";
            return false;
        }

        for (int blockNumber = 0; blockNumber <= extensionCount; blockNumber++)
        {
            int sum = 0;
            int blockStart = blockNumber * BlockSize;
            for (int index = 0; index < BlockSize; index++)
            {
                sum += edid[blockStart + index];
            }

            if ((sum & 0xFF) != 0)
            {
                failure = $"EDID block {blockNumber} has an invalid checksum.";
                return false;
            }
        }

        return true;
    }

    private static bool TryInspectCtaBlock(
        byte[] edid,
        int blockNumber,
        List<ForumBlock> forumBlocks,
        ref bool hasAlternativeForumCapabilityBlock,
        bool allowVersion1ZeroOffsetDataBlocks,
        out string failure)
    {
        failure = string.Empty;
        int blockStart = blockNumber * BlockSize;
        int dtdOffset = edid[blockStart + 2];
        int dataBlockEnd;

        if (dtdOffset == 0)
        {
            if (!allowVersion1ZeroOffsetDataBlocks)
            {
                if (edid.AsSpan(blockStart + 4, BlockSize - 5).ContainsAnyExcept((byte)0))
                {
                    failure = $"CTA extension block {blockNumber} has data after a zero data-block boundary.";
                    return false;
                }

                return true;
            }

            dataBlockEnd = BlockSize - 1;
        }
        else if (dtdOffset is >= 4 and <= 127)
        {
            dataBlockEnd = dtdOffset;
        }
        else
        {
            failure = $"CTA extension block {blockNumber} has an invalid data-block boundary.";
            return false;
        }

        int relativeOffset = 4;
        while (relativeOffset < dataBlockEnd)
        {
            int absoluteOffset = blockStart + relativeOffset;
            byte header = edid[absoluteOffset];
            int payloadLength = header & 0x1F;
            int nextRelativeOffset = relativeOffset + payloadLength + 1;
            if (nextRelativeOffset > dataBlockEnd)
            {
                failure = $"CTA extension block {blockNumber} contains a truncated data block.";
                return false;
            }

            int tag = header >> 5;
            if (tag == VendorSpecificDataBlockTag &&
                payloadLength >= 3 &&
                edid[absoluteOffset + 1] == HdmiForumOui0 &&
                edid[absoluteOffset + 2] == HdmiForumOui1 &&
                edid[absoluteOffset + 3] == HdmiForumOui2)
            {
                forumBlocks.Add(new ForumBlock(blockNumber, absoluteOffset, payloadLength));
            }
            else if (tag == ExtendedDataBlockTag &&
                     payloadLength >= 1 &&
                     edid[absoluteOffset + 1] == HdmiForumScdbExtendedTag)
            {
                hasAlternativeForumCapabilityBlock = true;
            }

            relativeOffset = nextRelativeOffset;
        }

        return true;
    }

    private static byte ReplaceHighNibble(byte value, byte replacement)
        => (byte)((value & 0x0F) | (replacement << 4));

    private static void UpdateChecksum(byte[] edid, int blockNumber)
    {
        int blockStart = blockNumber * BlockSize;
        int sum = 0;
        for (int index = 0; index < BlockSize - 1; index++)
        {
            sum += edid[blockStart + index];
        }

        edid[blockStart + BlockSize - 1] = (byte)(0 - sum);
    }

    private static EdidPatchPlan Result(
        PatchDisposition disposition,
        byte[] original,
        byte[] patched,
        string summary)
        => new(disposition, original, patched, [], summary);

    private sealed record ForumBlock(int BlockNumber, int AbsoluteOffset, int PayloadLength);
}
