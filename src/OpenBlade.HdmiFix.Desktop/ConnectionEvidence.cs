using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OpenBlade.HdmiFix.Desktop;

internal sealed record EvidenceEvent(DateTimeOffset Time, string Provider, int Id, int Level);
internal sealed record EvidenceEvents(string Status, IReadOnlyList<EvidenceEvent> Events);

internal static class ConnectionEvidence
{
    internal const int MaximumEvents = 128;
    internal const int MaximumXmlCharacters = 32_768;
    internal static readonly TimeSpan MaximumWindow = TimeSpan.FromHours(24);

    private const int EventBatchSize = 16;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorTimeout = 1460;
    private const int EvtQueryChannelPath = 0x1;
    private const int EvtQueryForwardDirection = 0x100;
    private const int EvtRenderEventXml = 1;
    private const int DisplayLogTimeoutMilliseconds = 10_000;
    private static readonly TimeSpan NativeQueryDeadline = TimeSpan.FromSeconds(3);

    private static readonly IReadOnlyDictionary<string, string> AllowedProviders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Display"] = "Display",
            ["nvlddmkm"] = "nvlddmkm",
            ["Microsoft-Windows-DxgKrnl"] = "Microsoft-Windows-DxgKrnl",
            ["Microsoft-Windows-Kernel-PnP"] = "Microsoft-Windows-Kernel-PnP",
            ["Microsoft-Windows-UserPnp"] = "Microsoft-Windows-UserPnp",
            ["Microsoft-Windows-WHEA-Logger"] = "Microsoft-Windows-WHEA-Logger",
            ["Windows Error Reporting"] = "Windows Error Reporting",
        };

    private static readonly ChannelQuery[] Queries =
    [
        new("System",
        [
            "Display",
            "nvlddmkm",
            "Microsoft-Windows-DxgKrnl",
            "Microsoft-Windows-Kernel-PnP",
            "Microsoft-Windows-UserPnp",
            "Microsoft-Windows-WHEA-Logger",
        ]),
        new("Application", ["Windows Error Reporting"]),
    ];

    internal static EvidenceEvents ReadEvents(DateTimeOffset from, DateTimeOffset to) =>
        ReadEvents(from, to, ReadEventXml);

    internal static EvidenceEvents ReadEvents(
        DateTimeOffset from,
        DateTimeOffset to,
        Func<string, string, NativeEventBatch> read)
    {
        if (to < from || to - from > MaximumWindow)
            return new("Events:InvalidRange", []);

        var events = new List<EvidenceEvent>(MaximumEvents + 1);
        bool incomplete = false;
        bool anyQuerySucceeded = false;
        bool truncated = false;

        foreach (var specification in Queries)
        {
            NativeEventBatch batch;
            try
            {
                batch = read(specification.Channel, BuildQuery(from, to, specification.Providers));
                anyQuerySucceeded = true;
            }
            catch (Exception error) when (IsRecoverable(error))
            {
                incomplete = true;
                continue;
            }

            truncated |= batch.Truncated;
            incomplete |= batch.Incomplete;
            foreach (string xml in batch.Xml.Take(MaximumEvents + 1))
            {
                EvidenceEvent? parsed = ParseEventXml(xml);
                if (parsed is null || parsed.Time < from || parsed.Time > to
                    || !specification.Providers.Contains(parsed.Provider, StringComparer.OrdinalIgnoreCase))
                {
                    incomplete = true;
                    continue;
                }

                events.Add(parsed);
            }

            if (batch.Xml.Count > MaximumEvents)
                truncated = true;
        }

        var ordered = events
            .OrderBy(item => item.Time)
            .ThenBy(item => item.Provider, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .Take(MaximumEvents)
            .ToArray();
        truncated |= events.Count > MaximumEvents;

        string status = !anyQuerySucceeded ? "Events:Unavailable"
            : incomplete ? "Events:Partial"
            : truncated ? "Events:Truncated"
            : "Events:Complete";
        return new(status, ordered);
    }

    internal static string BuildQuery(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<string> providers)
    {
        string first = XmlConvert.ToString(from.UtcDateTime, XmlDateTimeSerializationMode.Utc);
        string last = XmlConvert.ToString(to.UtcDateTime, XmlDateTimeSerializationMode.Utc);
        string providerFilter = string.Join(" or ", providers.Select(provider =>
            $"Provider[@Name='{provider}']"));
        return $"*[System[TimeCreated[@SystemTime >= '{first}' and @SystemTime <= '{last}'] and ({providerFilter})]]";
    }

    internal static EvidenceEvent? ParseEventXml(string xml)
    {
        if (string.IsNullOrEmpty(xml) || xml.Length > MaximumXmlCharacters)
            return null;

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlCharacters,
                MaxCharactersFromEntities = 0,
            };
            using var text = new StringReader(xml);
            using XmlReader reader = XmlReader.Create(text, settings);
            XDocument document = XDocument.Load(reader, LoadOptions.None);
            XElement? system = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "System");
            XElement? provider = system?.Elements().FirstOrDefault(element => element.Name.LocalName == "Provider");
            XElement? eventId = system?.Elements().FirstOrDefault(element => element.Name.LocalName == "EventID");
            XElement? level = system?.Elements().FirstOrDefault(element => element.Name.LocalName == "Level");
            XElement? timeCreated = system?.Elements().FirstOrDefault(element => element.Name.LocalName == "TimeCreated");
            string? providerName = provider?.Attribute("Name")?.Value;
            string? timestamp = timeCreated?.Attribute("SystemTime")?.Value;

            if (providerName is null || !AllowedProviders.TryGetValue(providerName, out string? safeProvider)
                || !int.TryParse(eventId?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id < 0
                || !int.TryParse(level?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int eventLevel)
                || eventLevel is < 0 or > byte.MaxValue
                || !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset time))
                return null;

            return new(time, safeProvider, id, eventLevel);
        }
        catch (Exception error) when (error is XmlException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    internal static ProcessStartInfo CreateDisplayLogStartInfo(string directory)
    {
        string destination = Path.Combine(Path.GetFullPath(directory), "display-log.dat");
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "dispdiag.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        start.ArgumentList.Add("-out");
        start.ArgumentList.Add(destination);
        return start;
    }

    internal static async Task<string> CaptureDisplayLogAsync(string directory, CancellationToken token)
    {
        Process? process = null;
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            ProcessStartInfo start = CreateDisplayLogStartInfo(directory);
            string destination = start.ArgumentList[1];
            File.Delete(destination);
            process = Process.Start(start);
            if (process is null)
                return "DisplayLog:Unavailable";

            using var timeout = new CancellationTokenSource(DisplayLogTimeoutMilliseconds);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await WaitAfterKillAsync(process).ConfigureAwait(false);
                return token.IsCancellationRequested ? "DisplayLog:Canceled" : "DisplayLog:TimedOut";
            }

            return process.ExitCode == 0 && File.Exists(destination) && new FileInfo(destination).Length > 0
                ? "DisplayLog:Captured"
                : "DisplayLog:Unavailable";
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            if (process is { HasExited: false })
            {
                TryKill(process);
                await WaitAfterKillAsync(process).ConfigureAwait(false);
            }
            return token.IsCancellationRequested ? "DisplayLog:Canceled" : "DisplayLog:Unavailable";
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static NativeEventBatch ReadEventXml(string channel, string query)
    {
        using var queryHandle = new SafeEventHandle(EvtQuery(IntPtr.Zero, channel, query,
            EvtQueryChannelPath | EvtQueryForwardDirection), ownsHandle: true);
        if (queryHandle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var xml = new List<string>(MaximumEvents + 1);
        bool truncated = false;
        bool incomplete = false;
        var handles = new IntPtr[EventBatchSize];
        var elapsed = Stopwatch.StartNew();
        while (xml.Count <= MaximumEvents)
        {
            if (elapsed.Elapsed >= NativeQueryDeadline)
            {
                incomplete = true;
                break;
            }

            if (!EvtNext(queryHandle, handles.Length, handles, 1_000, 0, out int returned))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreItems)
                    break;
                if (error == ErrorTimeout)
                    continue;
                if (xml.Count != 0)
                {
                    incomplete = true;
                    break;
                }
                throw new Win32Exception(error);
            }

            try
            {
                for (int index = 0; index < returned; index++)
                {
                    if (xml.Count > MaximumEvents)
                    {
                        truncated = true;
                        break;
                    }
                    try { xml.Add(RenderXml(handles[index])); }
                    catch (Exception error) when (IsRecoverable(error)) { incomplete = true; }
                }
            }
            finally
            {
                for (int index = 0; index < returned; index++)
                {
                    if (handles[index] != IntPtr.Zero)
                    {
                        EvtClose(handles[index]);
                        handles[index] = IntPtr.Zero;
                    }
                }
            }
        }

        return new(xml, truncated, incomplete);
    }

    private static string RenderXml(IntPtr eventHandle)
    {
        if (EvtRender(IntPtr.Zero, eventHandle, EvtRenderEventXml, 0, IntPtr.Zero,
            out int bytesRequired, out _))
            throw new InvalidDataException("Unexpected empty event data.");
        int error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer || bytesRequired <= 0
            || bytesRequired > MaximumXmlCharacters * sizeof(char))
            throw new InvalidDataException("Event metadata is unavailable or exceeds its limit.");

        IntPtr buffer = Marshal.AllocHGlobal(bytesRequired);
        try
        {
            if (!EvtRender(IntPtr.Zero, eventHandle, EvtRenderEventXml, bytesRequired, buffer,
                out int bytesUsed, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (bytesUsed <= 0 || bytesUsed > bytesRequired)
                throw new InvalidDataException("Event metadata has an invalid size.");
            return Marshal.PtrToStringUni(buffer, bytesUsed / sizeof(char)).TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: false);
        }
        catch (Exception error) when (IsRecoverable(error)) { }
    }

    private static async Task WaitAfterKillAsync(Process process)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (Exception error) when (IsRecoverable(error)) { }
    }

    private static bool IsRecoverable(Exception error) => error is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException);

    internal sealed record NativeEventBatch(IReadOnlyList<string> Xml, bool Truncated = false, bool Incomplete = false);
    private sealed record ChannelQuery(string Channel, IReadOnlyList<string> Providers);

    private sealed class SafeEventHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeEventHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);
        protected override bool ReleaseHandle() => EvtClose(handle);
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr EvtQuery(IntPtr session, string path, string query, int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(SafeEventHandle resultSet, int eventArraySize,
        [Out] IntPtr[] events, int timeout, int flags, out int returned);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtRender(IntPtr context, IntPtr fragment, int flags, int bufferSize,
        IntPtr buffer, out int bufferUsed, out int propertyCount);

    [DllImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(IntPtr handle);
}
