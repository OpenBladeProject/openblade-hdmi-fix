using System.Text;

namespace OpenBlade.HdmiFix.Core;

public static partial class EdidPatchPlanner
{
    // Diagnostic observations never establish patch admission. Include every
    // complete block even when the header, count or a checksum is invalid.
    public static string InspectionText(byte[] edid)
    {
        ArgumentNullException.ThrowIfNull(edid);
        var report = new StringBuilder();
        report.AppendLine($"Stored EDID bytes: {edid.Length}");
        if (edid.Length > 32768)
            return report.Append("EDID inspection: exceeds the 32768-byte limit.").ToString();
        int completeBlocks = edid.Length / BlockSize;
        report.AppendLine($"Complete blocks: {completeBlocks}; trailing bytes: {edid.Length % BlockSize}");
        report.AppendLine("Header: " + (edid.Length < EdidHeader.Length ? "unavailable"
            : edid.AsSpan(0, EdidHeader.Length).SequenceEqual(EdidHeader) ? "valid" : "invalid"));
        if (edid.Length >= 12)
        {
            int manufacturer = (edid[8] << 8) | edid[9];
            report.AppendLine($"Manufacturer word: 0x{manufacturer:X4}; reserved bit {(manufacturer >> 15) & 1}; character codes {(manufacturer >> 10) & 31}, {(manufacturer >> 5) & 31}, {manufacturer & 31}");
            report.AppendLine($"Product code: 0x{(edid[10] | (edid[11] << 8)):X4}");
        }
        string model = ObservedModelId(edid);
        report.AppendLine("Observed monitor ID: " + (model.Length == 0 ? "unavailable or invalid" : model));
        if (edid.Length >= BlockSize)
        {
            int expectedBytes = (edid[126] + 1) * BlockSize;
            report.AppendLine($"Declared extension blocks: {edid[126]}; expected bytes: {expectedBytes}");
            var count = InspectExtensionCount(edid);
            int effectiveBytes = count.IsValid ? count.ExpectedBytes : expectedBytes;
            report.AppendLine("Block count: " + (edid.Length == effectiveBytes
                ? count.HasOverride ? "matches HDMI Forum override" : "matches"
                : edid.Length < effectiveBytes ? "missing data" : "extra data"));
            report.AppendLine("HDMI Forum extension-count override: " + (count.HasOverride ? "present" : "absent"));
            if (!count.IsValid) report.AppendLine("Extension-count validation: " + count.Failure);
            else
            {
                report.AppendLine($"Effective extension blocks: {count.Effective}; expected bytes: {count.ExpectedBytes}");
                report.AppendLine("Effective block count: " + (edid.Length == count.ExpectedBytes ? "matches"
                    : edid.Length < count.ExpectedBytes ? "missing data" : "extra data"));
            }
            report.AppendLine($"EDID version: {edid[18]}.{edid[19]}");
        }
        else report.AppendLine("Declared extension blocks: unavailable");

        for (int block = 0; block < completeBlocks; block++)
        {
            int start = block * BlockSize;
            int remainder = 0;
            for (int i = 0; i < BlockSize; i++) remainder += edid[start + i];
            remainder &= 255;
            report.AppendLine($"Block {block}: " + (block == 0 ? "base" : $"extension tag 0x{edid[start]:X2}")
                + $"; checksum {(remainder == 0 ? "valid" : "invalid")}; remainder {remainder}");
            if (block == 0 || edid[start] != CtaExtensionTag) continue;
            var forumBlocks = new List<ForumBlock>();
            bool alternative = false;
            bool valid = TryInspectCtaBlock(edid, block, forumBlocks, ref alternative,
                allowVersion1ZeroOffsetDataBlocks: false, out string failure);
            report.AppendLine($"CTA block {block}: " + (valid ? "structure valid" : failure));
            if (!valid) continue; // Never interpret partially parsed capability fields.
            report.AppendLine($"CTA block {block}: HDMI Forum vendor blocks {forumBlocks.Count}; alternative capability block {(alternative ? "present" : "absent")}");
            foreach (var forum in forumBlocks)
            {
                report.Append($"Observed HDMI fields in block {block}: payload bytes {forum.PayloadLength}");
                if (forum.PayloadLength >= MaximumFrlRateIndex)
                    report.Append($"; maximum FRL {(edid[forum.AbsoluteOffset + MaximumFrlRateIndex] & FrlRateMask) >> 4}");
                if (forum.PayloadLength >= DscFlagsIndex)
                    report.Append($"; DSC 1.2 {((edid[forum.AbsoluteOffset + DscFlagsIndex] & Dsc12Supported) != 0 ? "present" : "absent")}");
                if (forum.PayloadLength >= DscMaximumFrlRateIndex)
                    report.Append($"; DSC maximum FRL {(edid[forum.AbsoluteOffset + DscMaximumFrlRateIndex] & FrlRateMask) >> 4}");
                else report.Append("; required DSC fields incomplete");
                report.AppendLine();
            }
        }
        var plan = Plan(edid);
        report.Append($"Patch validation: {plan.Disposition}; {plan.Summary}");
        return report.ToString().TrimEnd();
    }
}
