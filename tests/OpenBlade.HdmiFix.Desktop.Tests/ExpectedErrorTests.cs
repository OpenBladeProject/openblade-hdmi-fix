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
    public void UnreadableModelDoesNotHideOtherSavedConnections()
    {
        var instances = MonitorRegistry.EnumerateSavedInstances(["AAA0001", "BBB0002", "CCC0003"],
            model => model == "BBB0002" ? throw new UnauthorizedAccessException() : ["fixture"], out var incomplete);
        Assert.True(incomplete);
        Assert.Equal(new[] { @"DISPLAY\AAA0001\fixture", @"DISPLAY\CCC0003\fixture" }, instances);
    }

    [Fact]
    public void DisappearingModelReportsPartialEnumeration()
    {
        var instances = MonitorRegistry.EnumerateSavedInstances(["AAA0001", "BBB0002"],
            model => model == "AAA0001" ? null : ["fixture"], out var incomplete);
        Assert.True(incomplete);
        Assert.Equal(@"DISPLAY\BBB0002\fixture", Assert.Single(instances));
    }

    [Fact]
    public void EnumerationDoesNotHideUnexpectedFailures()
    {
        Assert.Throws<NullReferenceException>(() => MonitorRegistry.EnumerateSavedInstances(["AAA0001"],
            _ => throw new NullReferenceException(), out _));
    }

    [Fact]
    public void UnexpectedFailuresRemainUnhandled()
    {
        Assert.False(PatcherBackend.IsExpected(new NullReferenceException()));
        Assert.False(PatcherBackend.IsExpected(new OutOfMemoryException()));
        Assert.False(PatcherBackend.IsExpected(new OperationCanceledException()));
    }
}
