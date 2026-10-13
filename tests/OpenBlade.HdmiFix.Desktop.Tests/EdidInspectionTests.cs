using Microsoft.Win32;
using OpenBlade.HdmiFix.Core;
using OpenBlade.HdmiFix.Desktop;
using Xunit;

namespace OpenBlade.HdmiFix.Desktop.Tests;

public sealed class EdidInspectionTests
{
    private const string Instance = @"DISPLAY\SAM74AF\private-instance";

    [Fact]
    public void ReportCollectsIndependentFailuresAndLeavesInputAndAdmissionUnchanged()
    {
        var edid = Edid(3);
        edid[0] = 1; // bad header
        edid[126] = 1; // extra complete block
        edid[127] ^= 8;
        edid[255] ^= 1;
        var snapshot = edid.ToArray();
        string report = EdidPatchPlanner.InspectionText(edid);
        Assert.Contains("Header: invalid", report);
        Assert.Contains("Stored EDID bytes: 384", report);
        Assert.Contains("expected bytes: 256", report);
        Assert.Contains("Block count: extra data", report);
        Assert.Contains("Block 0: base; checksum invalid", report);
        Assert.Contains("Block 1: extension tag 0x02; checksum invalid", report);
        Assert.Contains("Block 2: extension tag 0x02; checksum valid", report);
        Assert.Contains("Observed monitor ID: SAM74AF", report);
        Assert.Equal(snapshot, edid);
        Assert.Equal(PatchDisposition.Invalid, EdidPatchPlanner.Plan(edid).Disposition);
        Assert.Throws<SavedMonitorReadException>(() => MonitorRegistry.ValidateSavedEdid(Instance, edid));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(127)]
    [InlineData(129)]
    [InlineData(32768)]
    [InlineData(32769)]
    public void MalformedAndOversizedDataRemainBounded(int length)
    {
        string report = EdidPatchPlanner.InspectionText(new byte[length]);
        Assert.Contains($"Stored EDID bytes: {length}", report);
        Assert.True(report.Length < 60000);
        if (length > 32768) Assert.DoesNotContain("Block 0:", report);
    }

    [Fact]
    public void MissingDeclaredBlocksAndPartialTrailingBytesAreBothReported()
    {
        var edid = Edid(1).Concat(new byte[3]).ToArray();
        edid[126] = 2;
        string report = EdidPatchPlanner.InspectionText(edid);
        Assert.Contains("Complete blocks: 1; trailing bytes: 3", report);
        Assert.Contains("Declared extension blocks: 2; expected bytes: 384", report);
        Assert.Contains("Block count: missing data", report);
    }

    [Fact]
    public void CapabilityObservationsSurviveBadChecksumWithoutEnablingPatch()
    {
        var edid = Edid(2);
        edid[130] = 17;
        byte[] forum = [0x6C, 0xD8, 0x5D, 0xC4, 1, 0, 0, 0x50, 0, 0, 0, 0x80, 0x50];
        forum.CopyTo(edid, 132);
        FixChecksum(edid, 1);
        Assert.Equal(PatchDisposition.Ready, EdidPatchPlanner.Plan(edid).Disposition);
        edid[255] ^= 1;
        string report = EdidPatchPlanner.InspectionText(edid);
        Assert.Contains("maximum FRL 5; DSC 1.2 present; DSC maximum FRL 5", report);
        Assert.Contains("checksum invalid", report);
        Assert.Equal(PatchDisposition.Invalid, EdidPatchPlanner.Plan(edid).Disposition);
    }

    [Fact]
    public void InvalidCtaBoundaryDoesNotProducePartialCapabilityInterpretation()
    {
        var edid = Edid(2);
        edid[130] = 3;
        FixChecksum(edid, 1);
        string report = EdidPatchPlanner.InspectionText(edid);
        Assert.Contains("invalid data-block boundary", report);
        Assert.DoesNotContain("Observed HDMI fields", report);
    }

    [Fact]
    public void InvalidManufacturerReportsEncodedIdentityWithoutAcceptingIt()
    {
        var edid = Edid(1);
        edid[8] |= 0x80;
        FixChecksum(edid, 0);
        string report = EdidPatchPlanner.InspectionText(edid);
        Assert.Contains("Manufacturer word: 0xCC2D; reserved bit 1; character codes 19, 1, 13", report);
        Assert.Contains("Product code: 0x74AF", report);
        Assert.Contains("Observed monitor ID: unavailable or invalid", report);
        Assert.Equal(PatchDisposition.Invalid, EdidPatchPlanner.Plan(edid).Disposition);
        Assert.Throws<SavedMonitorReadException>(() => MonitorRegistry.ValidateSavedEdid(Instance, edid));
    }

    [Fact]
    public void ExtraValidBlockCannotBeTrimmedOrAcceptedByInspection()
    {
        var edid = Edid(2);
        edid[126] = 0;
        FixChecksum(edid, 0);
        string report = EdidPatchPlanner.InspectionText(edid);
        Assert.Contains("Block count: extra data", report);
        Assert.Contains("checksum valid", report);
        Assert.Equal(PatchDisposition.Invalid, EdidPatchPlanner.Plan(edid).Disposition);
        Assert.Equal(SavedMonitorReadFailure.EdidInvalid,
            Assert.Throws<SavedMonitorReadException>(() => MonitorRegistry.ValidateSavedEdid(Instance, edid)).Reason);
    }

    [Fact]
    public void ReportOmitsRawSerialDescriptorAndNonbinaryValues()
    {
        var edid = Edid(1);
        System.Text.Encoding.ASCII.GetBytes("SECRET-SERIAL").CopyTo(edid, 60);
        FixChecksum(edid, 0);
        string report = SavedEntryInspection.DescribeEdid(Instance, edid, RegistryValueKind.Binary);
        Assert.DoesNotContain("SECRET-SERIAL", report);
        Assert.DoesNotContain("private-instance", report);
        Assert.Contains("Saved connection identity: matches", report);
        Assert.DoesNotContain("SECRET-VALUE", SavedEntryInspection.DescribeEdid(Instance, "SECRET-VALUE", RegistryValueKind.String));
        Assert.Contains("no saved data", SavedEntryInspection.DescribeEdid(Instance, null, null));
    }

    [Fact]
    public void OverrideReportInspectsAllValuesAndRedactsNonstandardNamesAndContents()
    {
        var result = MonitorRegistry.InspectOverrideValues(0, 3,
        [ ("private-name", RegistryValueKind.String, "private-content"),
          ("1", RegistryValueKind.Binary, new byte[128]),
          ("2", RegistryValueKind.Binary, new byte[127]) ]);
        Assert.Null(result.State);
        Assert.Equal(SavedMonitorReadFailure.OverrideUnrecognized, result.Failure);
        Assert.Contains("Override value 1: type Binary; bytes 128; checksum remainder 0", result.Report);
        Assert.Contains("Override value 2: type Binary; bytes 127", result.Report);
        Assert.DoesNotContain("private", result.Report);
    }

    [Fact]
    public void OverrideInspectionPreservesRecognizedStateAndStopsOversizedEnumeration()
    {
        var bytes = new byte[128];
        var result = MonitorRegistry.InspectOverrideValues(0, 1, [("1", RegistryValueKind.Binary, bytes)]);
        Assert.Null(result.Failure);
        Assert.Same(bytes, result.State!.Blocks[1]);
        var refused = MonitorRegistry.InspectOverrideValues(0, 257, ThrowingValues());
        Assert.Equal(SavedMonitorReadFailure.OverrideUnrecognized, refused.Failure);
        Assert.Null(refused.State);
    }

    [Fact]
    public void OverrideReadFailureKeepsPartialSafeReportAndNativeTextPrivate()
    {
        var result = MonitorRegistry.InspectOverrideValues(0, 1, ThrowingValues());
        Assert.Null(result.State);
        Assert.Equal(SavedMonitorReadFailure.AccessDenied, result.Failure);
        Assert.Contains("Override inspection failed: AccessDenied", result.Report);
        Assert.DoesNotContain("private", result.Report);
    }

    [Fact]
    public void CopiedReportIncludesRelatedConnectionsWithoutUnrelatedOrPrivateIdentifiers()
    {
        var first = PatcherBackend.UnavailableMonitor(Instance, "Previously connected", "Connection FIRST",
            new SavedMonitorReadException(SavedMonitorReadFailure.EdidInvalid, "Stored EDID bytes: 256\nOverride: absent"));
        var second = first with { InstanceId = @"DISPLAY\sam74af\private-other", InstanceLabel = "Connection SECOND" };
        var unrelated = first with { InstanceId = @"DISPLAY\GSM1234\private-unrelated", InstanceLabel = "Connection OTHER" };
        string report = PatcherBackend.FormatRelatedDiagnostics(first, [first, second, unrelated]);
        Assert.Contains("Connection FIRST", report);
        Assert.Contains("Connection SECOND", report);
        Assert.Contains("Other saved connection", report);
        Assert.DoesNotContain("Connection OTHER", report);
        Assert.DoesNotContain("private", report);
        Assert.Contains("Stored EDID bytes: 256", report);
        Assert.False(first.CanPatch);
        Assert.False(first.CanRestore);
    }

    [Fact]
    public void SavedSam74afWithExtensionOverridePassesReadValidationAndReportsEffectiveCount()
    {
        var edid = Edid(4);
        edid[126] = 1;
        edid[130] = 7;
        new byte[] { 0xE2, 0x78, 3 }.CopyTo(edid, 132);
        edid[256] = 0x70; edid[384] = 0x70;
        for (int block = 0; block < 4; block++) FixChecksum(edid, block);
        Assert.Same(edid, MonitorRegistry.ValidateSavedEdid(Instance, edid));
        string report = SavedEntryInspection.DescribeEdid(Instance, edid, RegistryValueKind.Binary);
        Assert.Contains("Saved connection identity: matches", report);
        Assert.Contains("Effective extension blocks: 3; expected bytes: 512", report);
        Assert.Contains("Effective block count: matches", report);
        var incomplete = Assert.Throws<SavedMonitorReadException>(() => MonitorRegistry.ValidateSavedEdid(Instance, edid[..384]));
        Assert.Equal(SavedMonitorReadFailure.EdidIncomplete, incomplete.Reason);
    }

    private static IEnumerable<(string, RegistryValueKind, object?)> ThrowingValues()
    {
        yield return ("1", RegistryValueKind.Binary, new byte[128]);
        throw new UnauthorizedAccessException("private-path");
    }

    private static byte[] Edid(int blocks)
    {
        var bytes = new byte[128 * blocks];
        byte[] header = [0, 255, 255, 255, 255, 255, 255, 0];
        header.CopyTo(bytes, 0);
        bytes[8] = 0x4C; bytes[9] = 0x2D; bytes[10] = 0xAF; bytes[11] = 0x74;
        bytes[18] = 1; bytes[19] = 4; bytes[126] = (byte)(blocks - 1);
        FixChecksum(bytes, 0);
        for (int block = 1; block < blocks; block++)
        {
            bytes[128 * block] = 2; bytes[128 * block + 1] = 3; bytes[128 * block + 2] = 4;
            FixChecksum(bytes, block);
        }
        return bytes;
    }

    private static void FixChecksum(byte[] bytes, int block) => bytes[block * 128 + 127]
        = unchecked((byte)-bytes.Skip(block * 128).Take(127).Sum(x => (int)x));
}
