using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OpenBlade.HdmiFix.Desktop;

internal sealed record GraphicsAdapterDiagnostic(string Name, string DriverVersion, string Provider, string Failure = "");
internal sealed record HostDiagnosticSnapshot(string LaptopModel, string WindowsBuild, string WindowsRelease,
    IReadOnlyList<GraphicsAdapterDiagnostic> Adapters, string AdapterFailure = "")
{
    internal string Format()
    {
        var lines = new List<string>
        {
            $"Laptop model: {DiagnosticValues.Text(LaptopModel)}",
            $"Windows build: {DiagnosticValues.WindowsBuild(WindowsBuild)}",
            $"Windows release: {DiagnosticValues.Text(WindowsRelease, 16)}",
        };
        for (int i = 0; i < Math.Min(Adapters.Count, 32); i++)
        {
            var adapter = Adapters[i];
            lines.Add($"GPU {i + 1}: {DiagnosticValues.Text(adapter.Name)}");
            lines.Add($"GPU {i + 1} Windows driver version: {DiagnosticValues.DriverVersion(adapter.DriverVersion)}");
            lines.Add($"GPU {i + 1} driver provider: {DiagnosticValues.Text(adapter.Provider)}");
            if (adapter.Failure.Length != 0) lines.Add($"GPU {i + 1} driver read: {DiagnosticValues.Text(adapter.Failure)}");
        }
        if (Adapters.Count == 0) lines.Add("GPU inventory: unavailable");
        if (AdapterFailure.Length != 0) lines.Add("GPU enumeration: " + DiagnosticValues.Text(AdapterFailure));
        return string.Join("\n", lines);
    }
}

internal static class DiagnosticValues
{
    internal static string Text(object? value, int limit = 160)
    {
        if (value is not string text) return "unknown";
        text = text.Trim();
        if (text.Length == 0 || text.Length > limit || text.Any(c => char.IsControl(c)
                || c is '\\' or '/' or ':' or '@' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
            return "unknown";
        return text;
    }

    internal static string Status(string value) => value.Length <= 160
        && Regex.IsMatch(value, @"^[A-Za-z0-9 (),;:\-]+$", RegexOptions.CultureInvariant)
            ? value : "unknown";
    internal static string DriverVersion(object? value) => value is string version
        && Regex.IsMatch(version, @"^\d{1,5}(?:\.\d{1,5}){3}$", RegexOptions.CultureInvariant)
            ? version : "unknown";

    internal static string WindowsBuild(object? value) => value is string version
        && Regex.IsMatch(version, @"^10\.0\.\d{1,6}\.\d{1,8}$", RegexOptions.CultureInvariant)
            ? version : "unknown";
}

internal static class HostDiagnosticsReader
{
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");

    internal static HostDiagnosticSnapshot Read()
    {
        string laptop = "unknown", build = "unknown", release = "unknown";
        try
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            laptop = DiagnosticValues.Text(bios?.GetValue("SystemProductName"));
        }
        catch (Exception e) when (PatcherBackend.IsExpected(e)) { }
        try
        {
            using var windows = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (windows?.GetValue("CurrentBuildNumber") is string number
                && Regex.IsMatch(number, @"^\d{1,6}$", RegexOptions.CultureInvariant)
                && windows.GetValue("UBR") is int revision && revision >= 0)
                build = $"10.0.{number}.{revision}";
            release = DiagnosticValues.Text(windows?.GetValue("DisplayVersion"), 16);
        }
        catch (Exception e) when (PatcherBackend.IsExpected(e)) { }
        var adapters = new List<GraphicsAdapterDiagnostic>();
        string failure = "";
        try { ReadAdapters(adapters); }
        catch (Exception e) when (PatcherBackend.IsExpected(e)) { failure = "incomplete or unavailable"; }
        return new(laptop, build, release, adapters, failure);
    }

    private static void ReadAdapters(List<GraphicsAdapterDiagnostic> adapters)
    {
        var guid = DisplayClass;
        var handle = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 2); // DIGCF_PRESENT
        if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; index <= 32; index++)
            {
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(handle, index, ref info))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) return; // ERROR_NO_MORE_ITEMS
                    throw new Win32Exception(error);
                }
                if (index == 32) throw new InvalidDataException("The adapter inventory exceeds its bounded limit.");
                try
                {
                    // Read the software key for this exact present adapter, not stale class records.
                    var keyHandle = SetupDiOpenDevRegKey(handle, ref info, 1, 0, 2, 0x20019);
                    if (keyHandle == new IntPtr(-1) || keyHandle == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    using var safe = new SafeRegistryHandle(keyHandle, ownsHandle: true);
                    using var key = RegistryKey.FromHandle(safe);
                    adapters.Add(new(DiagnosticValues.Text(key.GetValue("DriverDesc")),
                        DiagnosticValues.DriverVersion(key.GetValue("DriverVersion")),
                        DiagnosticValues.Text(key.GetValue("ProviderName"))));
                }
                catch (Exception e) when (PatcherBackend.IsExpected(e))
                {
                    adapters.Add(new("unknown", "unknown", "unknown", "unavailable"));
                }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DeviceInstance; public UIntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo data);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref DeviceInfo data, uint scope, uint profile, uint keyType, uint access);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
