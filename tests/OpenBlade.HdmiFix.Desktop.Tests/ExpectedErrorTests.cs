using System.IO;
using OpenBlade.HdmiFix.Core;
using OpenBlade.HdmiFix.Desktop;
using Xunit;

namespace OpenBlade.HdmiFix.Desktop.Tests;

public sealed class ExpectedErrorTests
{
    [Fact]
    public void InvalidSavedInstanceUsesExpectedErrorHandler()
    {
        var handled = false;
        try { MonitorInstance.Validate(@"DISPLAY\Default_Monitor\fixture"); }
        catch (Exception e) when (PatcherBackend.IsExpected(e))
        {
            Assert.IsType<InvalidDataException>(e);
            handled = true;
        }
        Assert.True(handled);
    }

    [Fact]
    public void CorruptBackupUsesExpectedErrorHandlerWithoutBeingIgnored()
    {
        var handled = false;
        try { BackupInventory.Read(["invalid"], _ => throw new InvalidDataException("Corrupt original")); }
        catch (Exception e) when (PatcherBackend.IsExpected(e))
        {
            Assert.IsType<InvalidDataException>(e);
            handled = true;
        }
        Assert.True(handled);
    }

    [Fact]
    public void FixedHelperValidationFailureUsesSharedErrorHandler()
    {
        Assert.True(PatcherBackend.IsExpected(new InvalidDataException("Unexpected patch arguments.")));
    }

    [Fact]
    public void UnexpectedFailuresRemainUnhandled()
    {
        Assert.False(PatcherBackend.IsExpected(new NullReferenceException()));
        Assert.False(PatcherBackend.IsExpected(new OutOfMemoryException()));
        Assert.False(PatcherBackend.IsExpected(new OperationCanceledException()));
    }
}
