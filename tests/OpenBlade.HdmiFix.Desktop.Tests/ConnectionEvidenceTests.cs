using OpenBlade.HdmiFix.Desktop;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace OpenBlade.HdmiFix.Desktop.Tests;

public sealed class ConnectionEvidenceTests
{
    private const string EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    [Fact]
    public void ParserReturnsOnlyAllowlistedMetadata()
    {
        string xml = EventXml("nvlddmkm", 14, 2, "2026-10-14T07:08:09.1234567Z",
            "<EventData><Data>C:\\Users\\private\\monitor-serial</Data></EventData>");

        EvidenceEvent? result = ConnectionEvidence.ParseEventXml(xml);

        Assert.Equal(DateTimeOffset.Parse("2026-10-14T07:08:09.1234567Z"), result!.Time);
        Assert.Equal("nvlddmkm", result.Provider);
        Assert.Equal(14, result.Id);
        Assert.Equal(2, result.Level);
        Assert.DoesNotContain("private", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serial", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Unrelated-Provider")]
    [InlineData("Display/../../private")]
    public void ParserRejectsProvidersOutsideTheFixedAllowlist(string provider)
    {
        Assert.Null(ConnectionEvidence.ParseEventXml(EventXml(provider, 1, 2,
            "2026-10-14T07:08:09Z")));
    }

    [Fact]
    public void ParserRejectsDtdMalformedAndOversizedXml()
    {
        Assert.Null(ConnectionEvidence.ParseEventXml("<!DOCTYPE Event [<!ENTITY x SYSTEM 'file:///private'>]><Event>&x;</Event>"));
        Assert.Null(ConnectionEvidence.ParseEventXml("<Event>"));
        Assert.Null(ConnectionEvidence.ParseEventXml(new string('x', ConnectionEvidence.MaximumXmlCharacters + 1)));
    }

    [Fact]
    public void ReaderUsesBoundedServerSideQueriesForBothChannels()
    {
        var from = new DateTimeOffset(2026, 10, 14, 7, 0, 0, TimeSpan.Zero);
        var to = from.AddMinutes(15);
        var calls = new List<(string Channel, string Query)>();

        EvidenceEvents result = ConnectionEvidence.ReadEvents(from, to, (channel, query) =>
        {
            calls.Add((channel, query));
            return new([], false);
        });

        Assert.Equal("Events:Complete", result.Status);
        Assert.Empty(result.Events);
        Assert.Collection(calls,
            system =>
            {
                Assert.Equal("System", system.Channel);
                Assert.Contains("TimeCreated[@SystemTime >= '2026-10-14T07:00:00Z'", system.Query);
                Assert.Contains("@SystemTime <= '2026-10-14T07:15:00Z'", system.Query);
                Assert.Contains("Provider[@Name='Display']", system.Query);
                Assert.DoesNotContain("Windows Error Reporting", system.Query);
            },
            application =>
            {
                Assert.Equal("Application", application.Channel);
                Assert.Contains("Provider[@Name='Windows Error Reporting']", application.Query);
                Assert.DoesNotContain("nvlddmkm", application.Query);
            });
    }

    [Fact]
    public void InvalidOrBroadRangeDoesNotReadTheEventLog()
    {
        int calls = 0;
        ConnectionEvidence.NativeEventBatch Read(string _, string __)
        {
            calls++;
            return new([]);
        }

        var now = DateTimeOffset.UtcNow;
        Assert.Equal("Events:InvalidRange", ConnectionEvidence.ReadEvents(now, now.AddTicks(-1), Read).Status);
        Assert.Equal("Events:InvalidRange", ConnectionEvidence.ReadEvents(now.AddDays(-2), now, Read).Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ReaderSortsCapsAndSignalsTruncation()
    {
        var from = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        var source = Enumerable.Range(0, ConnectionEvidence.MaximumEvents + 4)
            .Select(index => EventXml("Display", index, 3,
                from.AddMinutes(ConnectionEvidence.MaximumEvents + 4 - index).ToString("O")))
            .ToArray();

        EvidenceEvents result = ConnectionEvidence.ReadEvents(from, from.AddHours(4), (channel, _) =>
            channel == "System" ? new(source, true) : new([]));

        Assert.Equal("Events:Truncated", result.Status);
        Assert.Equal(ConnectionEvidence.MaximumEvents, result.Events.Count);
        Assert.True(result.Events.SequenceEqual(result.Events.OrderBy(item => item.Time)));
    }

    [Fact]
    public void PermissionFailureCannotBeReportedAsAHealthyEmptyResult()
    {
        var from = DateTimeOffset.UtcNow.AddMinutes(-5);
        EvidenceEvents unavailable = ConnectionEvidence.ReadEvents(from, from.AddMinutes(1),
            (_, _) => throw new UnauthorizedAccessException(@"C:\Users\private\event-log"));
        EvidenceEvents partial = ConnectionEvidence.ReadEvents(from, from.AddMinutes(1),
            (channel, _) => channel == "System"
                ? new([EventXml("Display", 4101, 3, from.ToString("O"))])
                : throw new UnauthorizedAccessException("private application log"));

        Assert.Equal("Events:Unavailable", unavailable.Status);
        Assert.Empty(unavailable.Events);
        Assert.DoesNotContain("private", unavailable.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Events:Partial", partial.Status);
        Assert.Single(partial.Events);
    }

    [Fact]
    public void NativeBatchFailureIsReportedAsPartial()
    {
        var from = DateTimeOffset.UtcNow.AddMinutes(-5);
        EvidenceEvents result = ConnectionEvidence.ReadEvents(from, from.AddMinutes(1),
            (channel, _) => channel == "System"
                ? new([EventXml("Display", 4101, 3, from.ToString("O"))], Incomplete: true)
                : new([]));

        Assert.Equal("Events:Partial", result.Status);
        Assert.Single(result.Events);
    }

    [Fact]
    public void DisplayLogUsesOnlyTheFixedSystemExecutableAndOutputFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "openblade display evidence");
        ProcessStartInfo start = ConnectionEvidence.CreateDisplayLogStartInfo(directory);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "dispdiag.exe"), start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.False(start.RedirectStandardOutput);
        Assert.False(start.RedirectStandardError);
        Assert.Equal(["-out", Path.Combine(Path.GetFullPath(directory), "display-log.dat")], start.ArgumentList);
        Assert.DoesNotContain(start.ArgumentList, argument => argument.Contains("RunAs", StringComparison.OrdinalIgnoreCase));
    }

    private static string EventXml(string provider, int id, int level, string time, string extra = "") =>
        $"<Event xmlns='{EventNamespace}'><System><Provider Name='{provider}'/><EventID>{id}</EventID>" +
        $"<Level>{level}</Level><TimeCreated SystemTime='{time}'/></System>{extra}</Event>";
}
