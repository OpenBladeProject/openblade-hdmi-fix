using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.HdmiFix.Desktop;

internal sealed record LiveDisplayDiagnostics(
    string InstanceId,
    string MonitorName,
    string Mode,
    string Connection,
    string Hdr,
    string VrrCapability,
    string VrrRequested,
    string VrrEnabled,
    string VrrActive,
    string Error = "",
    ScdcObservation? Scdc = null,
    SignalTimingObservation? SignalTiming = null,
    string ColorEncoding = "Unknown",
    uint? BitsPerColorChannel = null,
    string DrivingAdapter = "Unknown");

internal sealed record SignalTimingObservation(
    ulong? PixelClockHz,
    uint? HorizontalSyncNumerator,
    uint? HorizontalSyncDenominator,
    uint? VerticalSyncNumerator,
    uint? VerticalSyncDenominator,
    uint? ActiveWidth,
    uint? ActiveHeight,
    uint? TotalWidth,
    uint? TotalHeight,
    uint? PathRefreshNumerator,
    uint? PathRefreshDenominator,
    string ScanLineOrdering,
    string Status)
{
    private const ulong MaximumPixelClockHz = 100_000_000_000;
    private const uint MaximumDimension = 1_000_000;

    internal SignalTimingObservation Sanitized()
    {
        var issues = new List<string>();
        bool completeExpected = Status == "SignalTiming:Available";
        ulong? pixelClock = PixelClockHz is > 0 and <= MaximumPixelClockHz ? PixelClockHz : null;
        if ((PixelClockHz is not null || completeExpected) && pixelClock is null) issues.Add("PixelClock");

        (uint? hNumerator, uint? hDenominator) = SanitizeRational(
            HorizontalSyncNumerator, HorizontalSyncDenominator, "HSync", completeExpected, issues);
        (uint? vNumerator, uint? vDenominator) = SanitizeRational(
            VerticalSyncNumerator, VerticalSyncDenominator, "VSync", completeExpected, issues);
        (uint? refreshNumerator, uint? refreshDenominator) = SanitizeRational(
            PathRefreshNumerator, PathRefreshDenominator, "PathRefresh", completeExpected, issues);
        (uint? activeWidth, uint? activeHeight) = SanitizeRegion(
            ActiveWidth, ActiveHeight, "Active", completeExpected, issues);
        (uint? totalWidth, uint? totalHeight) = SanitizeRegion(
            TotalWidth, TotalHeight, "Total", completeExpected, issues);

        string? suppliedScanLine = ScanLineOrdering;
        string scanLine = suppliedScanLine is "Progressive" or "Interlaced upper-field-first"
            or "Interlaced lower-field-first" or "Unknown" ? suppliedScanLine : "Unknown";
        if (scanLine != suppliedScanLine) issues.Add("ScanLine");

        string status = string.IsNullOrWhiteSpace(Status) ? "SignalTiming:InvalidStatus"
            : DiagnosticValues.Status(Status);
        if (status == "unknown") status = "SignalTiming:InvalidStatus";
        if (issues.Count != 0)
            status = DiagnosticValues.Status(status + "; Invalid " + string.Join(",", issues));

        return this with
        {
            PixelClockHz = pixelClock,
            HorizontalSyncNumerator = hNumerator,
            HorizontalSyncDenominator = hDenominator,
            VerticalSyncNumerator = vNumerator,
            VerticalSyncDenominator = vDenominator,
            ActiveWidth = activeWidth,
            ActiveHeight = activeHeight,
            TotalWidth = totalWidth,
            TotalHeight = totalHeight,
            PathRefreshNumerator = refreshNumerator,
            PathRefreshDenominator = refreshDenominator,
            ScanLineOrdering = scanLine,
            Status = status,
        };
    }

    private static (uint?, uint?) SanitizeRational(uint? numerator, uint? denominator,
        string field, bool required, List<string> issues)
    {
        if (numerator is > 0 && denominator is > 0) return (numerator, denominator);
        if (required || numerator is not null || denominator is not null) issues.Add(field);
        return (null, null);
    }

    private static (uint?, uint?) SanitizeRegion(uint? width, uint? height,
        string field, bool required, List<string> issues)
    {
        if (width is > 0 and <= MaximumDimension && height is > 0 and <= MaximumDimension)
            return (width, height);
        if (required || width is not null || height is not null) issues.Add(field);
        return (null, null);
    }
}

internal readonly record struct AdapterCapture(string Name, string Error);

internal sealed class AdapterNameCache(int maximumEntries)
{
    private readonly Dictionary<(uint LowPart, int HighPart), AdapterCapture> records = [];

    internal AdapterCapture Read(uint lowPart, int highPart, Func<AdapterCapture> read)
    {
        if (records.TryGetValue((lowPart, highPart), out AdapterCapture cached)) return cached;
        AdapterCapture result = read();
        string safeName = DiagnosticValues.Text(result.Name);
        result = result with { Name = safeName };
        if (result.Error.Length == 0 && !safeName.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            && records.Count < maximumEntries)
            records.Add((lowPart, highPart), result);
        return result;
    }
}

internal sealed record LiveDisplayScan(
    IReadOnlyList<LiveDisplayDiagnostics> Displays, string Failure = "", bool HasUnresolvedPaths = false);

internal static class LiveDisplayDiagnosticsReader
{
    private static int activeRead;
    private static readonly AdapterNameCache adapterCache = new(32);

    private const uint QueryOnlyActivePaths = 0x00000002;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const int MaximumPaths = 64;
    private const int MaximumModes = 256;
    private const int TopologyAttempts = 3;
    private const uint GetTargetName = 2;
    private const uint GetAdapterName = 4;
    private const uint GetAdvancedColorInfo2 = 15;
    private const int ModeInfoTarget = 2;
    private const uint InvalidModeIndex = uint.MaxValue;

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint SpdrpDeviceDescription = 0x00000000;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint RegSz = 1;
    private static readonly Guid MonitorInterfaceClass = new("e6f07b5f-ee97-4a90-b076-33f57bf4eaa7");
    private static readonly Guid AdapterInterfaceClass = new("5b45201d-f2f2-4f3b-85bb-30ff1f953599");
    private static readonly IntPtr InvalidHandleValue = new(-1);

    internal static (int Path, int Mode, int TargetName, int Hdr, int NvTarget, int NvVrr) AbiSizes =>
        (Marshal.SizeOf<DisplayConfigPathInfo>(), Marshal.SizeOf<DisplayConfigModeInfo>(),
         Marshal.SizeOf<DisplayConfigTargetDeviceName>(), Marshal.SizeOf<DisplayConfigAdvancedColorInfo2>(),
         Marshal.SizeOf<NvTargetInfoData>(), Marshal.SizeOf<NvGetVrrInfo>());

    internal static (uint NvTarget, uint NvVrr) NvApiVersions =>
        (MakeNvApiVersion(Marshal.SizeOf<NvTargetInfoData>(), 1),
         MakeNvApiVersion(Marshal.SizeOf<NvGetVrrInfo>(), 1));

    internal static int AdapterNameAbiSize => Marshal.SizeOf<DisplayConfigAdapterName>();

    internal static LiveDisplayScan Read() => Read(null);

    internal static LiveDisplayScan Read(string? recordingInstanceId)
    {
        // A timed-out recorder read may still be inside the installed driver.
        // Refresh and later recordings must not pile up additional native calls.
        if (Interlocked.CompareExchange(ref activeRead, 1, 0) != 0)
            return new([], "LiveCapture:Busy");
        try { return ReadCore(recordingInstanceId); }
        finally { Volatile.Write(ref activeRead, 0); }
    }

    private static LiveDisplayScan ReadCore(string? recordingInstanceId)
    {
        TopologyRead topology;
        try
        {
            topology = ReadTopology();
        }
        catch (NativeReadException e)
        {
            return new([], Error(e.Category, e.Status));
        }
        catch (Exception e) when (IsExpected(e))
        {
            return new([], "Topology:Unavailable");
        }

        var candidates = new List<DisplayCandidate>(topology.Paths.Count);
        string bindingFailure = "";
        Guid monitorInterfaceClass = MonitorInterfaceClass;
        IntPtr monitorSet = SetupDiGetClassDevsW(ref monitorInterfaceClass, null, IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (monitorSet == InvalidHandleValue)
        {
            bindingFailure = Error("InterfaceSet", Marshal.GetLastWin32Error());
        }

        try
        {
            var adapters = new Dictionary<Luid, AdapterCapture>();
            foreach (DisplayPath path in topology.Paths)
            {
                DisplayConfigTargetDeviceName targetName = new()
                {
                    Header = new()
                    {
                        Type = GetTargetName,
                        Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                        AdapterId = path.AdapterId,
                        Id = path.TargetId,
                    },
                    MonitorFriendlyDeviceName = "",
                    MonitorDevicePath = "",
                };

                int targetStatus = DisplayConfigGetDeviceInfo(ref targetName);
                string pathError = targetStatus == 0 ? "" : Error("TargetName", targetStatus);
                string interfacePath = targetStatus == 0 ? targetName.MonitorDevicePath ?? "" : "";
                string instanceId = "";
                if (targetStatus == 0 && monitorSet != InvalidHandleValue)
                {
                    (instanceId, string resolveError) = ResolveInstanceId(monitorSet, interfacePath);
                    pathError = JoinErrors(pathError, resolveError);
                }
                else if (targetStatus == 0)
                {
                    pathError = JoinErrors(pathError, bindingFailure);
                }

                if (!adapters.TryGetValue(path.AdapterId, out AdapterCapture adapter))
                {
                    adapter = ReadAdapterCached(path.AdapterId);
                    adapters.Add(path.AdapterId, adapter);
                }
                candidates.Add(new(
                    path.AdapterId.LowPart,
                    path.AdapterId.HighPart,
                    path.TargetId,
                    interfacePath,
                    instanceId,
                    targetStatus == 0 ? SanitizeName(targetName.MonitorFriendlyDeviceName) : "Unknown",
                    path.Width,
                    path.Height,
                    path.RefreshNumerator,
                    path.RefreshDenominator,
                    path.OutputTechnology,
                    pathError,
                    path.SignalTiming,
                    adapter.Name,
                    adapter.Error));
            }
        }
        finally
        {
            if (monitorSet != InvalidHandleValue)
            {
                SetupDiDestroyDeviceInfoList(monitorSet);
            }
        }

        using NvApiSession nvapi = NvApiSession.Open();
        return BuildScan(candidates, ReadHdr, nvapi.Read, bindingFailure, recordingInstanceId, nvapi.ReadScdc);
    }

    internal static LiveDisplayScan BuildScan(
        IReadOnlyList<DisplayCandidate> candidates,
        Func<DisplayCandidate, HdrCapture> readHdr,
        Func<DisplayCandidate, VrrCapture> readVrr,
        string failure = "",
        string? recordingInstanceId = null,
        Func<DisplayCandidate, ScdcObservation>? readScdc = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(readHdr);
        ArgumentNullException.ThrowIfNull(readVrr);

        var displays = new List<LiveDisplayDiagnostics>(candidates.Count);
        var ambiguous = candidates
            .Where(candidate => candidate.InstanceId.Length != 0)
            .GroupBy(candidate => candidate.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (DisplayCandidate candidate in candidates)
        {
            if (candidate.InstanceId.Length != 0 && ambiguous.Contains(candidate.InstanceId))
            {
                if (displays.Any(display => display.InstanceId.Equals(candidate.InstanceId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                displays.Add(new(candidate.InstanceId, candidate.MonitorName, "Unknown", "Unknown", "Unknown",
                    "Unknown", "Unknown", "Unknown", "Unknown",
                    JoinErrors(candidate.Error, "Binding:Ambiguous"), ScdcObservation.Unknown(
                        candidate.InstanceId.Equals(recordingInstanceId, StringComparison.OrdinalIgnoreCase)
                            ? "Scdc:Ambiguous" : "Scdc:NotRequested")));
                continue;
            }

            HdrCapture hdr;
            try
            {
                hdr = readHdr(candidate);
            }
            catch (Exception e) when (IsExpected(e))
            {
                hdr = new("Unknown", "Hdr:Unavailable");
            }

            VrrCapture vrr;
            try
            {
                vrr = readVrr(candidate);
            }
            catch (Exception e) when (IsExpected(e))
            {
                vrr = VrrCapture.Unknown("Nvapi:Unavailable");
            }

            ScdcObservation scdc = ScdcObservation.Unknown("Scdc:NotRequested");
            if (!string.IsNullOrWhiteSpace(recordingInstanceId)
                && candidate.InstanceId.Equals(recordingInstanceId, StringComparison.OrdinalIgnoreCase))
            {
                if (candidate.OutputTechnology != 5)
                    scdc = ScdcObservation.Unknown("Scdc:NotHdmi");
                else if (candidate.Error.Length != 0)
                    scdc = ScdcObservation.Unknown("Scdc:BindingUnavailable");
                else if (readScdc is null)
                    scdc = ScdcObservation.Unknown("Scdc:Unavailable");
                else
                {
                    try { scdc = readScdc(candidate); }
                    catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                    { scdc = ScdcObservation.Unknown("Scdc:Unavailable"); }
                }
            }

            displays.Add(new(
                candidate.InstanceId,
                candidate.MonitorName,
                FormatMode(candidate.Width, candidate.Height, candidate.RefreshNumerator, candidate.RefreshDenominator),
                FormatConnection(candidate.OutputTechnology),
                hdr.Value,
                vrr.Capability,
                vrr.Requested,
                vrr.Enabled,
                vrr.Active,
                JoinErrors(candidate.Error, candidate.AdapterError, hdr.Error, vrr.Error), scdc,
                candidate.SignalTiming?.Sanitized(), DiagnosticValues.Text(hdr.ColorEncoding),
                hdr.BitsPerColorChannel is >= 1 and <= 64 ? hdr.BitsPerColorChannel : null,
                DiagnosticValues.Text(candidate.DrivingAdapter)));
        }

        bool unresolved = candidates.Any(candidate => candidate.InstanceId.Length == 0);
        if (unresolved)
        {
            // These failures belong to unresolved paths, not the selected monitor.
            // Retain bounded numeric categories without exposing interface paths.
            var errors = candidates.Where(candidate => candidate.InstanceId.Length == 0)
                .Select(candidate => candidate.Error.Length == 0 ? "Binding:Unavailable" : DiagnosticValues.Status(candidate.Error))
                .Distinct(StringComparer.Ordinal).Take(2).ToArray();
            failure = JoinErrors(failure, "BindingIncomplete", JoinErrors(errors));
        }
        return new(displays, failure, unresolved);
    }

    internal static HdrCapture DecodeHdr(uint flags, uint activeColorMode,
        uint colorEncoding = uint.MaxValue, uint bitsPerColorChannel = 0)
    {
        string color = ColorEncodingName(colorEncoding);
        uint? bits = bitsPerColorChannel is >= 1 and <= 64 ? bitsPerColorChannel : null;
        string colorError = colorEncoding == uint.MaxValue && bitsPerColorChannel == 0
            ? "" : bits is null ? "Color:InvalidData" : "";
        bool active = (flags & 0x00000002) != 0;
        bool supported = (flags & 0x00000010) != 0;
        bool userEnabled = (flags & 0x00000020) != 0;
        if (activeColorMode > 2 || userEnabled && !supported || activeColorMode == 2 && (!active || !supported))
        {
            return new("Unknown", JoinErrors("Hdr:InvalidData", colorError), color, bits);
        }

        string activeText = active && activeColorMode == 2 ? "active" : "inactive";
        return new($"{activeText}, {(supported ? "supported" : "not supported")}, user setting {(userEnabled ? "enabled" : "disabled")}",
            colorError, color, bits);
    }

    internal static string ColorEncodingName(uint value) => value switch
    {
        0 => "RGB",
        1 => "YCbCr444",
        2 => "YCbCr422",
        3 => "YCbCr420",
        4 => "Intensity",
        uint.MaxValue => "Unknown",
        _ => $"Unknown({value})",
    };

    internal static VrrCapture DecodeVrr(int status, uint displayCount, uint flags)
    {
        if (status != 0)
        {
            return VrrCapture.Unknown(Error("NvapiTarget", status));
        }
        if (displayCount != 1)
        {
            return VrrCapture.Unknown(Error("NvapiTargetCount", displayCount));
        }

        return new(
            YesNo((flags & 0x00000002) != 0),
            YesNo((flags & 0x00000004) != 0),
            YesNo((flags & 0x00000001) != 0),
            YesNo((flags & 0x00000010) != 0),
            "");
    }

    private static TopologyRead ReadTopology()
    {
        for (int attempt = 0; attempt < TopologyAttempts; attempt++)
        {
            int status = GetDisplayConfigBufferSizes(QueryOnlyActivePaths, out uint pathCount, out uint modeCount);
            if (status != 0)
            {
                throw new NativeReadException("TopologySizes", status);
            }
            if (pathCount > MaximumPaths || modeCount > MaximumModes)
            {
                throw new NativeReadException("TopologyBounds", 0);
            }
            if (pathCount == 0)
            {
                return new([]);
            }

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];
            uint returnedPaths = pathCount;
            uint returnedModes = modeCount;
            status = QueryDisplayConfig(QueryOnlyActivePaths, ref returnedPaths, paths,
                ref returnedModes, modes, IntPtr.Zero);
            if (status == ErrorInsufficientBuffer)
            {
                continue;
            }
            if (status != 0)
            {
                throw new NativeReadException("TopologyQuery", status);
            }
            if (returnedPaths > pathCount || returnedModes > modeCount)
            {
                throw new NativeReadException("TopologyChanged", 0);
            }

            var result = new List<DisplayPath>((int)returnedPaths);
            for (int index = 0; index < returnedPaths; index++)
            {
                DisplayConfigPathInfo path = paths[index];
                uint width = 0;
                uint height = 0;
                uint refreshNumerator = path.TargetInfo.RefreshRate.Numerator;
                uint refreshDenominator = path.TargetInfo.RefreshRate.Denominator;
                SignalTimingObservation timing = new(
                    null, null, null, null, null, null, null, null, null,
                    refreshNumerator, refreshDenominator, "Unknown", "SignalTiming:ModeUnavailable");
                uint modeIndex = path.TargetInfo.ModeInfoIndex;
                if (modeIndex != InvalidModeIndex && modeIndex < returnedModes)
                {
                    DisplayConfigModeInfo mode = modes[modeIndex];
                    if (mode.InfoType == ModeInfoTarget && mode.Id == path.TargetInfo.Id
                        && mode.AdapterId.Equals(path.TargetInfo.AdapterId))
                    {
                        DisplayConfigVideoSignalInfo signal = mode.TargetMode.TargetVideoSignalInfo;
                        width = signal.ActiveSize.Width;
                        height = signal.ActiveSize.Height;
                        timing = CreateSignalTiming(signal.PixelRate,
                            signal.HSyncFrequency.Numerator, signal.HSyncFrequency.Denominator,
                            signal.VSyncFrequency.Numerator, signal.VSyncFrequency.Denominator,
                            signal.ActiveSize.Width, signal.ActiveSize.Height,
                            signal.TotalSize.Width, signal.TotalSize.Height,
                            refreshNumerator, refreshDenominator, signal.ScanLineOrdering);
                        if (refreshNumerator == 0 || refreshDenominator == 0)
                        {
                            refreshNumerator = mode.TargetMode.TargetVideoSignalInfo.VSyncFrequency.Numerator;
                            refreshDenominator = mode.TargetMode.TargetVideoSignalInfo.VSyncFrequency.Denominator;
                        }
                    }
                }

                result.Add(new(path.TargetInfo.AdapterId, path.TargetInfo.Id, width, height,
                    refreshNumerator, refreshDenominator, path.TargetInfo.OutputTechnology,
                    timing.Sanitized()));
            }
            return new(result);
        }

        throw new NativeReadException("TopologyRetry", ErrorInsufficientBuffer);
    }

    private static (string InstanceId, string Error) ResolveInstanceId(IntPtr set, string interfacePath)
    {
        if (string.IsNullOrWhiteSpace(interfacePath))
        {
            return ("", "Binding:Unavailable");
        }

        DeviceInterfaceData interfaceData = new() { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
        if (!SetupDiOpenDeviceInterfaceW(set, interfacePath, 0, ref interfaceData))
        {
            return ("", Error("BindingOpen", Marshal.GetLastWin32Error()));
        }

        DeviceInfoData deviceInfo = new() { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
        SetupDiGetDeviceInterfaceDetailW(set, ref interfaceData, IntPtr.Zero, 0, out uint requiredSize, ref deviceInfo);
        int sizeError = Marshal.GetLastWin32Error();
        if (sizeError != ErrorInsufficientBuffer || requiredSize < 8 || requiredSize > 4096)
        {
            return ("", Error("BindingSize", sizeError));
        }

        IntPtr detail = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            deviceInfo.Size = (uint)Marshal.SizeOf<DeviceInfoData>();
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref interfaceData, detail, requiredSize, out _, ref deviceInfo))
            {
                return ("", Error("BindingDetail", Marshal.GetLastWin32Error()));
            }

            var instance = new StringBuilder(512);
            if (!SetupDiGetDeviceInstanceIdW(set, ref deviceInfo, instance, instance.Capacity, out _))
            {
                return ("", Error("BindingInstance", Marshal.GetLastWin32Error()));
            }
            return (instance.ToString(), "");
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    internal static SignalTimingObservation CreateSignalTiming(ulong pixelClockHz,
        uint horizontalSyncNumerator, uint horizontalSyncDenominator,
        uint verticalSyncNumerator, uint verticalSyncDenominator,
        uint activeWidth, uint activeHeight, uint totalWidth, uint totalHeight,
        uint pathRefreshNumerator, uint pathRefreshDenominator, uint scanLineOrdering) => new SignalTimingObservation(
            pixelClockHz,
            horizontalSyncNumerator, horizontalSyncDenominator,
            verticalSyncNumerator, verticalSyncDenominator,
            activeWidth, activeHeight, totalWidth, totalHeight,
            pathRefreshNumerator, pathRefreshDenominator,
            scanLineOrdering switch
            {
                1 => "Progressive",
                2 => "Interlaced upper-field-first",
                3 => "Interlaced lower-field-first",
                _ => "Unknown",
            },
            "SignalTiming:Available").Sanitized();

    private static AdapterCapture ReadAdapter(Luid adapterId)
    {
        DisplayConfigAdapterName adapter = new()
        {
            Header = new()
            {
                Type = GetAdapterName,
                Size = (uint)Marshal.SizeOf<DisplayConfigAdapterName>(),
                AdapterId = adapterId,
            },
            AdapterDevicePath = "",
        };
        int status = DisplayConfigGetDeviceInfo(ref adapter);
        if (status != 0 || string.IsNullOrWhiteSpace(adapter.AdapterDevicePath))
            return new("Unknown", Error("AdapterName", status));

        Guid adapterInterfaceClass = AdapterInterfaceClass;
        IntPtr set = SetupDiGetClassDevsW(ref adapterInterfaceClass, null, IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (set == InvalidHandleValue)
            return new("Unknown", Error("AdapterSet", Marshal.GetLastWin32Error()));
        try
        {
            DeviceInterfaceData interfaceData = new() { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
            if (!SetupDiOpenDeviceInterfaceW(set, adapter.AdapterDevicePath, 0, ref interfaceData))
                return new("Unknown", Error("AdapterOpen", Marshal.GetLastWin32Error()));

            DeviceInfoData deviceInfo = new() { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
            SetupDiGetDeviceInterfaceDetailW(set, ref interfaceData, IntPtr.Zero, 0,
                out uint requiredSize, ref deviceInfo);
            int sizeError = Marshal.GetLastWin32Error();
            if (sizeError != ErrorInsufficientBuffer || requiredSize < 8 || requiredSize > 4096)
                return new("Unknown", Error("AdapterDetailSize", sizeError));

            IntPtr detail = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                deviceInfo.Size = (uint)Marshal.SizeOf<DeviceInfoData>();
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref interfaceData, detail, requiredSize,
                    out _, ref deviceInfo))
                    return new("Unknown", Error("AdapterDetail", Marshal.GetLastWin32Error()));
                return ReadAdapterName(set, ref deviceInfo);
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static AdapterCapture ReadAdapterCached(Luid adapterId)
        => adapterCache.Read(adapterId.LowPart, adapterId.HighPart, () => ReadAdapter(adapterId));

    private static AdapterCapture ReadAdapterName(IntPtr set, ref DeviceInfoData deviceInfo)
    {
        AdapterCapture friendly = ReadDeviceString(set, ref deviceInfo, SpdrpFriendlyName);
        return friendly.Error.Length == 0 ? friendly
            : ReadDeviceString(set, ref deviceInfo, SpdrpDeviceDescription);
    }

    private static AdapterCapture ReadDeviceString(IntPtr set, ref DeviceInfoData deviceInfo, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref deviceInfo, property, out uint kind,
            buffer, (uint)buffer.Length, out uint requiredSize))
            return new("Unknown", Error("AdapterProperty", Marshal.GetLastWin32Error()));
        if (kind != RegSz || requiredSize is < 2 or > 1024 || (requiredSize & 1) != 0)
            return new("Unknown", "AdapterProperty:InvalidData");
        string name = SanitizeName(Encoding.Unicode.GetString(buffer, 0, (int)requiredSize).TrimEnd('\0'));
        return name == "Unknown" ? new("Unknown", "AdapterProperty:InvalidData") : new(name, "");
    }

    private static HdrCapture ReadHdr(DisplayCandidate candidate)
    {
        DisplayConfigAdvancedColorInfo2 info = new()
        {
            Header = new()
            {
                Type = GetAdvancedColorInfo2,
                Size = (uint)Marshal.SizeOf<DisplayConfigAdvancedColorInfo2>(),
                AdapterId = new(candidate.AdapterLowPart, candidate.AdapterHighPart),
                Id = candidate.TargetId,
            },
        };
        int status = DisplayConfigGetDeviceInfo(ref info);
        return status == 0 ? DecodeHdr(info.Flags, info.ActiveColorMode,
            info.ColorEncoding, info.BitsPerColorChannel)
            : new("Unknown", Error("HdrQuery", status));
    }

    private static string FormatMode(uint width, uint height, uint numerator, uint denominator)
    {
        if (width == 0 || height == 0 || denominator == 0 || numerator == 0)
        {
            return "Unknown";
        }

        double rate = numerator / (double)denominator;
        if (!double.IsFinite(rate) || rate is < 1 or > 1000)
        {
            return "Unknown";
        }
        string refresh = Math.Abs(rate - Math.Round(rate)) < 0.005
            ? Math.Round(rate).ToString(CultureInfo.InvariantCulture)
            : rate.ToString("0.##", CultureInfo.InvariantCulture);
        return $"{width} x {height}, {refresh} Hz";
    }

    private static string FormatConnection(int technology) => technology switch
    {
        -1 => "Other",
        0 => "VGA",
        1 => "S-Video",
        2 => "Composite video",
        3 => "Component video",
        4 => "DVI",
        5 => "HDMI",
        8 => "D-JPN",
        9 => "SDI",
        10 => "DisplayPort",
        11 or 6 or 13 or unchecked((int)0x80000000) => "Internal",
        12 => "UDI",
        14 => "SDTV dongle",
        15 => "Miracast",
        16 => "Indirect wired",
        17 => "Virtual display",
        18 => "DisplayPort USB tunnel",
        _ => $"Unknown (technology {technology})",
    };

    internal static string SanitizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var result = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = result.Length != 0;
                continue;
            }
            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(character);
        }
        return result.Length == 0 ? "Unknown" : result.ToString();
    }

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string Error(string category, long status) => status == 0 ? category : $"{category}:{status}";

    private static string JoinErrors(params string[] errors) =>
        string.Join("; ", errors.Where(error => !string.IsNullOrWhiteSpace(error)).Distinct(StringComparer.Ordinal));

    private static uint MakeNvApiVersion(int size, int version) => checked((uint)(size | version << 16));

    private static bool IsExpected(Exception exception) => exception is Win32Exception or IOException
        or InvalidOperationException or ArgumentException or ExternalException;

    internal sealed record DisplayCandidate(
        uint AdapterLowPart,
        int AdapterHighPart,
        uint TargetId,
        string InterfacePath,
        string InstanceId,
        string MonitorName,
        uint Width,
        uint Height,
        uint RefreshNumerator,
        uint RefreshDenominator,
        int OutputTechnology,
        string Error = "",
        SignalTimingObservation? SignalTiming = null,
        string DrivingAdapter = "Unknown",
        string AdapterError = "");

    internal readonly record struct HdrCapture(string Value, string Error,
        string ColorEncoding = "Unknown", uint? BitsPerColorChannel = null);

    internal readonly record struct VrrCapture(
        string Capability,
        string Requested,
        string Enabled,
        string Active,
        string Error)
    {
        internal static VrrCapture Unknown(string error) => new("Unknown", "Unknown", "Unknown", "Unknown", error);
    }

    private sealed record TopologyRead(IReadOnlyList<DisplayPath> Paths);

    private sealed record DisplayPath(
        Luid AdapterId,
        uint TargetId,
        uint Width,
        uint Height,
        uint RefreshNumerator,
        uint RefreshDenominator,
        int OutputTechnology,
        SignalTimingObservation SignalTiming);

    private sealed class NativeReadException(string category, long status) : Exception
    {
        internal string Category { get; } = category;
        internal long Status { get; } = status;
    }

    private sealed class NvApiSession : IDisposable
    {
        private const uint InitializeId = 0x0150e828;
        private const uint UnloadId = 0xd22bdd7e;
        private const uint GetDisplayIdsFromTargetId = 0xe7e5f89e;
        private const uint GetVrrInfoId = 0xdf8fda57;
        private const int MaximumNvDisplays = 128;

        private IntPtr _library;
        private NvApiUnload? _unload;
        private NvApiGetDisplayIdsFromTarget? _getDisplays;
        private NvApiGetVrrInfo? _getVrr;
        private ScdcReader? _scdcReader;
        private bool _initialized;

        private NvApiSession() { }

        internal string Failure { get; private set; } = "";

        internal static NvApiSession Open()
        {
            var session = new NvApiSession();
            if (!Environment.Is64BitProcess)
            {
                session.Failure = "Nvapi:UnsupportedProcess";
                return session;
            }

            string libraryPath = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
            try
            {
                if (!Path.IsPathFullyQualified(libraryPath) || !NativeLibrary.TryLoad(libraryPath, out session._library))
                {
                    session.Failure = "Nvapi:Unavailable";
                    return session;
                }
                if (!NativeLibrary.TryGetExport(session._library, "nvapi_QueryInterface", out IntPtr queryPointer))
                {
                    session.Failure = "Nvapi:InterfaceUnavailable";
                    return session;
                }

                var query = Marshal.GetDelegateForFunctionPointer<NvApiQueryInterface>(queryPointer);
                NvApiInitialize? initialize = GetDelegate<NvApiInitialize>(query, InitializeId);
                session._unload = GetDelegate<NvApiUnload>(query, UnloadId);
                session._getDisplays = GetDelegate<NvApiGetDisplayIdsFromTarget>(query, GetDisplayIdsFromTargetId);
                session._getVrr = GetDelegate<NvApiGetVrrInfo>(query, GetVrrInfoId);
                if (initialize is null || session._unload is null || session._getDisplays is null)
                {
                    session.Failure = "Nvapi:EntryPointUnavailable";
                    return session;
                }

                int status = initialize();
                if (status != 0)
                {
                    session.Failure = Error("NvapiInit", status);
                    return session;
                }
                session._initialized = true;
                session._scdcReader = new ScdcReader(id => query(id));
                return session;
            }
            catch (Exception e) when (IsExpected(e))
            {
                session.Failure = "Nvapi:Unavailable";
                return session;
            }
        }

        internal VrrCapture Read(DisplayCandidate candidate)
        {
            if (!_initialized || _getDisplays is null || _getVrr is null)
            {
                return VrrCapture.Unknown(Failure.Length == 0 ? "Nvapi:Unavailable" : Failure);
            }

            NvTargetInfoData target = new()
            {
                Version = NvApiVersions.NvTarget,
                AdapterId = new(candidate.AdapterLowPart, candidate.AdapterHighPart),
                TargetId = candidate.TargetId,
                DisplayIds = new uint[MaximumNvDisplays],
                Reserved = new uint[4],
            };
            int status = _getDisplays(ref target);
            if (status != 0 || target.DisplayIdCount != 1 || target.DisplayIds is null)
            {
                return DecodeVrr(status, target.DisplayIdCount, 0);
            }

            NvGetVrrInfo info = new()
            {
                Version = NvApiVersions.NvVrr,
                Reserved = new uint[4],
            };
            status = _getVrr(target.DisplayIds[0], ref info);
            return status == 0 ? DecodeVrr(0, 1, info.Flags)
                : VrrCapture.Unknown(Error("NvapiVrr", status));
        }

        internal ScdcObservation ReadScdc(DisplayCandidate candidate)
        {
            if (!_initialized || _getDisplays is null || _scdcReader is null)
                return ScdcObservation.Unknown(Failure.Length == 0 ? "Scdc:Unavailable" : "Scdc:" + Failure);
            if (!TargetPathMatches(candidate))
                return ScdcObservation.Unknown("Scdc:TargetChanged");
            NvTargetInfoData target = new()
            {
                Version = NvApiVersions.NvTarget,
                AdapterId = new(candidate.AdapterLowPart, candidate.AdapterHighPart),
                TargetId = candidate.TargetId,
                DisplayIds = new uint[MaximumNvDisplays],
                Reserved = new uint[4],
            };
            int status = _getDisplays(ref target);
            if (status != 0)
                return ScdcObservation.Unknown(Error("ScdcTarget", status));
            if (target.DisplayIdCount == 0)
                return ScdcObservation.Unknown("Scdc:TargetUnavailable");
            if (target.DisplayIdCount != 1)
                return ScdcObservation.Unknown("Scdc:TargetAmbiguous");
            if (target.DisplayIds is null || target.DisplayIds.Length == 0 || target.DisplayIds[0] == 0)
                return ScdcObservation.Unknown("Scdc:InvalidTarget");
            uint displayId = target.DisplayIds[0];
            ScdcObservation observation = _scdcReader.Read(displayId);
            // Discard a read spanning a Windows target/monitor or NVIDIA mapping change.
            target = new()
            {
                Version = NvApiVersions.NvTarget,
                AdapterId = new(candidate.AdapterLowPart, candidate.AdapterHighPart),
                TargetId = candidate.TargetId,
                DisplayIds = new uint[MaximumNvDisplays],
                Reserved = new uint[4],
            };
            status = _getDisplays(ref target);
            if (status != 0 || target.DisplayIdCount != 1 || target.DisplayIds is null
                || target.DisplayIds[0] != displayId || !TargetPathMatches(candidate))
                return ScdcObservation.Unknown("Scdc:TargetChanged");
            return observation;
        }

        private static bool TargetPathMatches(DisplayCandidate candidate)
        {
            if (candidate.InterfacePath.Length == 0) return false;
            DisplayConfigTargetDeviceName target = new()
            {
                Header = new()
                {
                    Type = GetTargetName,
                    Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                    AdapterId = new(candidate.AdapterLowPart, candidate.AdapterHighPart),
                    Id = candidate.TargetId,
                },
                MonitorFriendlyDeviceName = "",
                MonitorDevicePath = "",
            };
            return DisplayConfigGetDeviceInfo(ref target) == 0
                && candidate.InterfacePath.Equals(target.MonitorDevicePath, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            if (_initialized && _unload is not null)
            {
                try { _unload(); }
                catch (Exception e) when (IsExpected(e)) { }
            }
            _initialized = false;
            if (_library != IntPtr.Zero)
            {
                NativeLibrary.Free(_library);
                _library = IntPtr.Zero;
            }
        }

        private static T? GetDelegate<T>(NvApiQueryInterface query, uint id) where T : Delegate
        {
            IntPtr pointer = query(id);
            return pointer == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Luid
    {
        internal readonly uint LowPart;
        internal readonly int HighPart;

        internal Luid(uint lowPart, int highPart)
        {
            LowPart = lowPart;
            HighPart = highPart;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        internal uint Numerator;
        internal uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRegion
    {
        internal uint Width;
        internal uint Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigVideoSignalInfo
    {
        internal ulong PixelRate;
        internal DisplayConfigRational HSyncFrequency;
        internal DisplayConfigRational VSyncFrequency;
        internal DisplayConfigRegion ActiveSize;
        internal DisplayConfigRegion TotalSize;
        internal uint VideoStandard;
        internal uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigTargetMode
    {
        internal DisplayConfigVideoSignalInfo TargetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayConfigModeInfo
    {
        [FieldOffset(0)] internal int InfoType;
        [FieldOffset(4)] internal uint Id;
        [FieldOffset(8)] internal Luid AdapterId;
        [FieldOffset(16)] internal DisplayConfigTargetMode TargetMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        internal Luid AdapterId;
        internal uint Id;
        internal uint ModeInfoIndex;
        internal uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        internal Luid AdapterId;
        internal uint Id;
        internal uint ModeInfoIndex;
        internal int OutputTechnology;
        internal uint Rotation;
        internal uint Scaling;
        internal DisplayConfigRational RefreshRate;
        internal uint ScanLineOrdering;
        internal int TargetAvailable;
        internal uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        internal DisplayConfigPathSourceInfo SourceInfo;
        internal DisplayConfigPathTargetInfo TargetInfo;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        internal uint Type;
        internal uint Size;
        internal Luid AdapterId;
        internal uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        internal DisplayConfigDeviceInfoHeader Header;
        internal uint Flags;
        internal int OutputTechnology;
        internal ushort EdidManufactureId;
        internal ushort EdidProductCodeId;
        internal uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigAdapterName
    {
        internal DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string AdapterDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigAdvancedColorInfo2
    {
        internal DisplayConfigDeviceInfoHeader Header;
        internal uint Flags;
        internal uint ColorEncoding;
        internal uint BitsPerColorChannel;
        internal uint ActiveColorMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        internal uint Size;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DeviceInstance;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvTargetInfoData
    {
        internal uint Version;
        internal Luid AdapterId;
        internal uint TargetId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] internal uint[] DisplayIds;
        internal uint DisplayIdCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] internal uint[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvGetVrrInfo
    {
        internal uint Version;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] internal uint[] Reserved;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NvApiQueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiInitialize();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiUnload();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiGetDisplayIdsFromTarget(ref NvTargetInfoData target);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiGetVrrInfo(uint displayId, ref NvGetVrrInfo info);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [In, Out] DisplayConfigPathInfo[] paths, ref uint modeCount,
        [In, Out] DisplayConfigModeInfo[] modes, IntPtr currentTopologyId);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName request);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigAdvancedColorInfo2 request);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigAdapterName request);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInterfaceW(IntPtr set, string devicePath, uint flags,
        ref DeviceInterfaceData interfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref DeviceInterfaceData interfaceData,
        IntPtr detailData, uint detailDataSize, out uint requiredSize, ref DeviceInfoData deviceInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfoData deviceInfoData,
        StringBuilder instanceId, int instanceIdSize, out int requiredSize);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfoData deviceInfoData,
        uint property, out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize,
        out uint requiredSize);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
