namespace OpenBlade.HdmiFix.Core;

public static partial class EdidPatchPlanner
{
    private const byte DisplayIdCtaDataBlockTag = 0x81;

    // DisplayID 1.3 Appendix C / 2.0 section 4.10 embeds ordinary CTA
    // data blocks in a bounded 0x81 payload. Inspect these for conflicts;
    // never patch DisplayID or search its unrelated payloads for byte patterns.
    private static bool TryInspectDisplayIdBlock(
        byte[] edid, int blockNumber, out bool hasForumCapabilities, out string failure)
    {
        hasForumCapabilities = false;
        failure = string.Empty;
        int blockStart = blockNumber * BlockSize;
        int payloadLength = edid[blockStart + 2];
        int end = blockStart + 5 + payloadLength;
        if (payloadLength > 121)
        {
            failure = $"DisplayID extension block {blockNumber} has an invalid data-block boundary.";
            return false;
        }

        int checksum = 0;
        for (int index = blockStart + 1; index <= end; index++) checksum += edid[index];
        if ((checksum & 255) != 0)
        {
            failure = $"DisplayID extension block {blockNumber} has an invalid internal checksum.";
            return false;
        }
        if (edid.AsSpan(end + 1, blockStart + BlockSize - 2 - end).ContainsAnyExcept((byte)0))
        {
            failure = $"DisplayID extension block {blockNumber} has nonzero data after its declared boundary.";
            return false;
        }

        for (int offset = blockStart + 5; offset < end;)
        {
            // DisplayID can end with zero filler, including a partial header.
            if (!edid.AsSpan(offset, end - offset).ContainsAnyExcept((byte)0)) break;
            if (end - offset < 3 || offset + 3 + edid[offset + 2] > end)
            {
                failure = $"DisplayID extension block {blockNumber} contains a truncated data block.";
                return false;
            }
            int next = offset + 3 + edid[offset + 2];
            if (edid[offset] == DisplayIdCtaDataBlockTag)
            {
                var forumBlocks = new List<ForumBlock>();
                bool alternative = false;
                if (!TryInspectCtaDataCollection(edid, blockNumber, offset + 3, next,
                        forumBlocks, ref alternative, out _))
                {
                    failure = $"DisplayID extension block {blockNumber} contains a truncated CTA data block.";
                    return false;
                }
                hasForumCapabilities |= forumBlocks.Count != 0 || alternative;
            }
            offset = next;
        }
        return true;
    }
}
