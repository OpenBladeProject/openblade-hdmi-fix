using OpenBlade.HdmiFix.Core;
using Xunit;

namespace OpenBlade.HdmiFix.Tests;

public sealed class EdidDisplayIdTests
{
    [Theory]
    [InlineData(0x13, true)]
    [InlineData(0x20, false)]
    public void EmbeddedForumVendorBlockPreventsAnIncompletePatch(byte version, bool countOverride)
    {
        var edid = Fixture(countOverride);
        SetDisplayId(edid, 2, Container(ForumBlock(edid)), version);
        AssertRejectedUnchanged(edid, PatchDisposition.Unsupported, "inside DisplayID");
    }

    [Fact]
    public void EmbeddedAlternativeCapabilityBlockIsRejected()
    {
        var edid = Fixture();
        SetDisplayId(edid, 2, Container([0xE7, 0x79, 1, 0x78, 0xC8, 0x50, 0, 0]));
        AssertRejectedUnchanged(edid, PatchDisposition.Unsupported, "inside DisplayID");
    }

    [Fact]
    public void LaterDisplayIdExtensionAndLaterContainerCannotHideAConflict()
    {
        var edid = Fixture();
        byte[] unrelated = [0x7E, 0, 4, 0xD8, 0x5D, 0xC4, 0x50];
        SetDisplayId(edid, 3, [.. unrelated, .. Container([0x42, 0x61, 0x10]), .. Container(ForumBlock(edid))]);
        AssertRejectedUnchanged(edid, PatchDisposition.Unsupported, "inside DisplayID");
    }

    [Fact]
    public void SoleEmbeddedForumBlockIsNotAdmittedForMutation()
    {
        var edid = Fixture();
        SetDisplayId(edid, 2, Container(ForumBlock(edid)));
        edid[136] = 0; // Remove the top-level HDMI Forum OUI.
        Checksum(edid, 1);
        AssertRejectedUnchanged(edid, PatchDisposition.Unsupported, "inside DisplayID");
    }

    [Theory]
    [InlineData(0x13)]
    [InlineData(0x20)]
    public void UnrelatedDisplayIdAndEmbeddedCtaDataArePreserved(byte version)
    {
        var edid = Fixture();
        byte[] unrelated = [0x7E, 0, 14, .. ForumBlock(edid)];
        SetDisplayId(edid, 2, [.. unrelated, .. Container([0x42, 0x61, 0x10])], version);
        var before = edid.ToArray();
        var plan = EdidPatchPlanner.Plan(edid);
        Assert.Equal(PatchDisposition.Ready, plan.Disposition);
        Assert.Equal(before, edid);
        Assert.Equal(before[256..], plan.Patched[256..]);
        Assert.Equal([142, 147, 255], Enumerable.Range(0, edid.Length).Where(i => edid[i] != plan.Patched[i]).ToArray());
    }

    [Fact]
    public void MatchingBytesOutsideTheEmbeddedCtaCollectionAreNotCapabilities()
    {
        var edid = Fixture();
        SetDisplayId(edid, 2, [.. Container([0x42, 0x61, 0x10]), 0x7E, 0, 14, .. ForumBlock(edid)]);
        Assert.Equal(PatchDisposition.Ready, EdidPatchPlanner.Plan(edid).Disposition);
    }

    [Theory]
    [InlineData("parent-length")]
    [InlineData("short-header")]
    [InlineData("container-length")]
    [InlineData("cta-length")]
    [InlineData("internal-checksum")]
    public void MalformedDisplayIdCapabilityCollectionCannotBePatched(string problem)
    {
        var edid = Fixture();
        SetDisplayId(edid, 2, Container([0x42, 0x61, 0x10]));
        switch (problem)
        {
            case "parent-length": edid[258] = 122; break;
            case "short-header": SetDisplayId(edid, 2, [0x81]); break;
            case "container-length": edid[263] = 4; InternalChecksum(edid, 2); break;
            case "cta-length": edid[264] = 0x5F; InternalChecksum(edid, 2); break;
            case "internal-checksum": edid[267] ^= 1; break;
        }
        Checksum(edid, 2); // The outer EDID checksum is valid in every case.
        AssertRejectedUnchanged(edid, PatchDisposition.Invalid, "DisplayID");
    }

    [Fact]
    public void TruncatedUnrelatedBlockCannotHideALaterCtaContainer()
    {
        var edid = Fixture();
        SetDisplayId(edid, 2, [0x7E, 0, 127, .. Container(ForumBlock(edid))]);
        AssertRejectedUnchanged(edid, PatchDisposition.Invalid, "truncated");
    }

    [Fact]
    public void ZeroFillerAndAnEmptyContainerRemainPatchable()
    {
        var edid = Fixture();
        SetDisplayId(edid, 2, [.. Container([]), 0, 0]);
        Assert.Equal(PatchDisposition.Ready, EdidPatchPlanner.Plan(edid).Disposition);
    }

    [Fact]
    public void InternalChecksumIsRequiredWithoutAnEmbeddedCtaContainer()
    {
        var edid = Fixture();
        SetDisplayId(edid, 2, [0x7E, 0, 3, 1, 2, 3]);
        edid[267] ^= 1;
        Checksum(edid, 2);
        AssertRejectedUnchanged(edid, PatchDisposition.Invalid, "internal checksum");
    }

    [Fact]
    public void UnderdeclaredPayloadCannotHideCapabilitiesInPadding()
    {
        var edid = Fixture();
        SetDisplayId(edid, 2, []);
        Container(ForumBlock(edid)).CopyTo(edid, 262);
        Checksum(edid, 2);
        AssertRejectedUnchanged(edid, PatchDisposition.Invalid, "declared boundary");
    }

    [Fact]
    public void MaximumDisplayIdPayloadDoesNotIncludeTheOuterChecksum()
    {
        var edid = Fixture();
        byte[] payload = new byte[121];
        payload[0] = 0x7E;
        payload[2] = 118;
        payload[^1] = 0x81; // Unrelated payload byte, not a CTA container.
        SetDisplayId(edid, 2, payload);
        Assert.Equal(PatchDisposition.Ready, EdidPatchPlanner.Plan(edid).Disposition);
    }

    [Fact]
    public void ApplyRejectsEmbeddedConflictBeforeBackupOrOverrideWrites()
    {
        var edid = Fixture();
        SetDisplayId(edid, 2, Container(ForumBlock(edid)));
        var store = new ProbeStore(new(@"DISPLAY\SAM74AF\fixture", edid, OverrideState.Empty));
        Assert.Throws<InvalidOperationException>(() => OverrideOperations.Apply(store, store,
            store.State.InstanceId, store.State.Fingerprint));
        Assert.Equal(0, store.Writes);
        Assert.Equal(0, store.Backups);
    }

    private static byte[] Fixture(bool countOverride = true)
    {
        var edid = EdidExtensionCountTests.Fixture();
        if (!countOverride)
        {
            edid[126] = 3;
            edid[133] = 0x7E;
            Checksum(edid, 0);
            Checksum(edid, 1);
        }
        return edid;
    }

    private static byte[] ForumBlock(byte[] edid) => edid[135..149];
    private static byte[] Container(byte[] payload) => [0x81, 0, (byte)payload.Length, .. payload];

    private static void SetDisplayId(byte[] edid, int block, byte[] payload, byte version = 0x20)
    {
        int start = block * 128;
        Array.Clear(edid, start, 128);
        edid[start] = 0x70;
        edid[start + 1] = version;
        edid[start + 2] = (byte)payload.Length;
        payload.CopyTo(edid, start + 5);
        InternalChecksum(edid, block);
        Checksum(edid, block);
    }

    private static void InternalChecksum(byte[] edid, int block)
    {
        int start = block * 128;
        int checksumOffset = start + 5 + edid[start + 2];
        edid[checksumOffset] = unchecked((byte)-edid.Skip(start + 1).Take(checksumOffset - start - 1).Sum(x => (int)x));
    }

    private static void Checksum(byte[] edid, int block) => edid[block * 128 + 127]
        = unchecked((byte)-edid.Skip(block * 128).Take(127).Sum(x => (int)x));

    private static void AssertRejectedUnchanged(byte[] edid, PatchDisposition disposition, string summary)
    {
        var before = edid.ToArray();
        var plan = EdidPatchPlanner.Plan(edid);
        Assert.Equal(disposition, plan.Disposition);
        Assert.Contains(summary, plan.Summary);
        Assert.Equal(before, edid);
        Assert.Equal(before, plan.Patched);
        Assert.Empty(plan.ChangedBlocks);
        Assert.Contains(plan.Summary, EdidPatchPlanner.InspectionText(edid));
    }

    private sealed class ProbeStore(MonitorState state) : IMonitorOverrideStore, IBackupStore
    {
        public MonitorState State { get; } = state;
        public int Writes { get; private set; }
        public int Backups { get; private set; }
        public MonitorState Read(string instanceId) => State;
        public void WriteBlock(string instanceId, int block, byte[] value, string expectedFingerprint) => Writes++;
        public void RemoveBlock(string instanceId, int block, string expectedFingerprint) => Writes++;
        public void Save(PatchBackup backup) => Backups++;
    }
}