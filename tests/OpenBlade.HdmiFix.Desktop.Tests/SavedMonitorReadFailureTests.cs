using System.IO;
using System.ComponentModel;
using System.Security;
using Microsoft.Win32;
using OpenBlade.HdmiFix.Desktop;
using Xunit;

namespace OpenBlade.HdmiFix.Desktop.Tests;

public sealed class SavedMonitorReadFailureTests
{
    private const string Instance = @"DISPLAY\SAM74AF\private-instance-token";

    [Fact]
    public void MissingEdidHasSpecificReason()
    {
        var failure = Assert.Throws<SavedMonitorReadException>(() => MonitorRegistry.ValidateSavedEdid(Instance, null));
        Assert.Equal(SavedMonitorReadFailure.EdidMissing, failure.Reason);
    }

    [Fact]
    public void InvalidEdidHasSpecificReason()
    {
        var valid = Edid();
        var invalidChecksum = valid.ToArray();
        invalidChecksum[127] ^= 1;
        foreach (var invalid in new object[] { "private-value", new byte[32769], invalidChecksum })
        {
            var failure = Assert.Throws<SavedMonitorReadException>(() => MonitorRegistry.ValidateSavedEdid(Instance, invalid));
            Assert.Equal(SavedMonitorReadFailure.EdidInvalid, failure.Reason);
        }
    }

    [Fact]
    public void MissingExtensionIsInvalidRatherThanIdentityMismatch()
    {
        var incomplete = Edid();
        incomplete[126] = 1;
        Checksum(incomplete);
        var failure = Assert.Throws<SavedMonitorReadException>(() => MonitorRegistry.ValidateSavedEdid(Instance, incomplete));
        Assert.Equal(SavedMonitorReadFailure.EdidIncomplete, failure.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(129)]
    public void PartialBlocksHaveIncompleteReason(int length)
    {
        var failure = Assert.Throws<SavedMonitorReadException>(() => MonitorRegistry.ValidateSavedEdid(Instance, new byte[length]));
        Assert.Equal(SavedMonitorReadFailure.EdidIncomplete, failure.Reason);
    }

    [Fact]
    public void ValidEdidWithDifferentIdentityHasSpecificReason()
    {
        var failure = Assert.Throws<SavedMonitorReadException>(() => MonitorRegistry.ValidateSavedEdid(@"DISPLAY\SAM1234\fixture", Edid()));
        Assert.Equal(SavedMonitorReadFailure.IdentityMismatch, failure.Reason);
    }

    [Fact]
    public void MatchingSam74afIdentifierPassesReadValidation()
    {
        var edid = Edid();
        Assert.Same(edid, MonitorRegistry.ValidateSavedEdid(Instance, edid));
    }

    [Theory]
    [InlineData("01", RegistryValueKind.Binary, 128)]
    [InlineData("256", RegistryValueKind.Binary, 128)]
    [InlineData("private-name", RegistryValueKind.Binary, 128)]
    [InlineData("1", RegistryValueKind.String, 128)]
    [InlineData("1", RegistryValueKind.Binary, 127)]
    public void UnrecognizedOverrideHasSpecificReason(string name, RegistryValueKind kind, int length)
    {
        var failure = Assert.Throws<SavedMonitorReadException>(() => MonitorRegistry.ValidateOverrideValue(name, kind, new byte[length]));
        Assert.Equal(SavedMonitorReadFailure.OverrideUnrecognized, failure.Reason);
    }

    [Fact]
    public void RecognizedOverrideStillPassesReadValidation()
    {
        var data = new byte[128];
        var result = MonitorRegistry.ValidateOverrideValue("1", RegistryValueKind.Binary, data);
        Assert.Equal(1, result.Key);
        Assert.Same(data, result.Value);
    }

    [Fact]
    public void NativeAccessFailuresHaveSpecificReason()
    {
        foreach (var error in new Exception[] { new UnauthorizedAccessException("private-path"),
                     new SecurityException("private-path"), new Win32Exception(5) })
            Assert.Equal(SavedMonitorReadFailure.AccessDenied, SavedMonitorReadErrors.Classify(error));
    }

    [Fact]
    public void UnknownErrorUsesSafeFallbackWithoutCopyingExceptionText()
    {
        var error = new IOException(@"C:\Users\private-user\serial-private-token");
        var view = PatcherBackend.UnavailableMonitor(Instance, "Previously connected", "Connection PREVIEW", error);
        Assert.Equal(SavedMonitorReadFailure.ReadFailed, view.ReadFailure);
        Assert.DoesNotContain("private-user", view.Details);
        Assert.DoesNotContain("serial-private-token", view.Details);
    }

    [Fact]
    public void EveryReadFailureKeepsMutationDisabledAndCopiesSanitizedReason()
    {
        foreach (var reason in Enum.GetValues<SavedMonitorReadFailure>())
        {
            var view = PatcherBackend.UnavailableMonitor(Instance, "Previously connected", "Connection PREVIEW",
                new SavedMonitorReadException(reason));
            Assert.False(view.CanPatch);
            Assert.False(view.CanRestore);
            Assert.Null(view.BackupPath);
            Assert.Contains(SavedMonitorReadErrors.Describe(reason), view.Details);
            var report = PatcherBackend.FormatDiagnostics(view);
            Assert.Contains($"Read failure: {reason}", report);
            Assert.Contains(SavedMonitorReadErrors.Describe(reason), report);
            Assert.DoesNotContain("private-instance-token", report);
            Assert.DoesNotContain(Instance, report);
        }
    }

    private static byte[] Edid()
    {
        var bytes = new byte[128];
        byte[] header = [0, 255, 255, 255, 255, 255, 255, 0];
        header.CopyTo(bytes, 0);
        bytes[8] = 0x4C; bytes[9] = 0x2D; bytes[10] = 0xAF; bytes[11] = 0x74;
        bytes[18] = 1; bytes[19] = 4;
        Checksum(bytes);
        return bytes;
    }

    private static void Checksum(byte[] bytes) => bytes[127] = unchecked((byte)-bytes.Take(127).Sum(x => (int)x));
}
