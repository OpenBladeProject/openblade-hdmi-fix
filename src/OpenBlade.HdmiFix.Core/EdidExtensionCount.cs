namespace OpenBlade.HdmiFix.Core;

public sealed record EdidExtensionCount(int Declared, int Effective, bool HasOverride, string Failure)
{
    public bool IsValid => Failure.Length == 0;
    public int ExpectedBytes => (Effective + 1) * 128;
}

public static partial class EdidPatchPlanner
{
    private const byte HdmiForumEeodbExtendedTag = 0x78;

    // HDMI 2.1 section 10.3.6: one HF-EEODB at bytes 4..6 of the
    // first CTA extension. Linux drm_edid.c and v4l-utils edid-decode
    // implement this count override; no trailing blocks are inferred.
    public static EdidExtensionCount InspectExtensionCount(byte[] edid)
    {
        ArgumentNullException.ThrowIfNull(edid);
        if (edid.Length < BlockSize || edid.Length > 32768)
            return new(0, 0, false, "The EDID block count cannot be read from this length.");
        int declared = edid[126];
        int effective = declared;
        bool found = false;
        int blocks = edid.Length / BlockSize;
        for (int block = 1; block < blocks; block++)
        {
            int start = block * BlockSize;
            if (edid[start] != CtaExtensionTag) continue;
            int end = edid[start + 2];
            if (end is < 4 or > 127) continue; // Patch planning validates CTA structure.
            for (int offset = 4; offset < end;)
            {
                int absolute = start + offset;
                int length = edid[absolute] & 31;
                int next = offset + length + 1;
                bool isEeodb = edid[absolute] >> 5 == ExtendedDataBlockTag
                    && length >= 1 && offset + 1 < end && edid[absolute + 1] == HdmiForumEeodbExtendedTag;
                if (isEeodb)
                {
                    if (found)
                        return new(declared, effective, true, "Multiple HDMI Forum extension-count overrides were found.");
                    found = true;
                    if (length != 2 || next > end)
                        return new(declared, effective, true, "The HDMI Forum extension-count override has an invalid length.");
                    if (block != 1 || offset != 4 || edid[start + 1] < 3 || declared == 0)
                        return new(declared, effective, true, "The HDMI Forum extension-count override has an invalid position, revision, or base count.");
                    effective = edid[absolute + 2];
                    if (effective <= 1)
                        return new(declared, effective, true, "The HDMI Forum extension-count override must declare at least two extension blocks.");
                }
                if (next > end) break; // Never reinterpret a truncated block's payload.
                offset = next;
            }
        }
        return new(declared, effective, found, "");
    }
}
