using Microsoft.Win32;
using OpenBlade.HdmiFix.Core;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.HdmiFix.Desktop;

internal sealed class MonitorRegistry : IMonitorOverrideStore
{
    private const string DisplayRoot = @"SYSTEM\CurrentControlSet\Enum\DISPLAY";

    public IReadOnlyList<string> EnumerateSavedInstances()
    {
        using var root = Registry.LocalMachine.OpenSubKey(DisplayRoot);
        if (root is null) return [];
        var result = new List<string>();
        foreach (var model in root.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            using var modelKey = root.OpenSubKey(model);
            if (modelKey is null) continue;
            foreach (var instance in modelKey.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
                result.Add($@"DISPLAY\{model}\{instance}");
        }
        return result;
    }

    public MonitorState Read(string instanceId)
    {
        ValidateReadPath(instanceId);
        using var key = Registry.LocalMachine.OpenSubKey(ParametersPath(instanceId));
        if (key is null) throw new InvalidDataException("Windows has no cached device parameters for this entry.");
        if (key.GetValue("EDID") is not byte[] edid || edid.Length > 32768)
            throw new InvalidDataException("Windows has no complete cached EDID for this entry.");
        using var overrides = key.OpenSubKey("EDID_OVERRIDE");
        var blocks = new Dictionary<int, byte[]>();
        if (overrides is not null)
        {
            if (overrides.SubKeyCount != 0 || overrides.ValueCount > 256)
                throw new InvalidDataException("An unfamiliar override layout is present. No changes are allowed.");
            foreach (var name in overrides.GetValueNames())
            {
                if (!int.TryParse(name, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var number)
                    || number is < 0 or > 255 || name != number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    || overrides.GetValueKind(name) != RegistryValueKind.Binary
                    || overrides.GetValue(name) is not byte[] data || data.Length != 128)
                    throw new InvalidDataException("An unfamiliar EDID override is present. Restore it with its owning tool.");
                blocks.Add(number, data);
            }
        }
        return new(instanceId, edid, new(overrides is not null, blocks));
    }

    public void WriteBlock(string instanceId, int block, byte[] value, string expectedFingerprint)
    {
        MonitorInstance.Validate(instanceId);
        EnsureDisconnected(instanceId);
        if (block is < 1 or > 255 || value.Length != 128 || value.Sum(x => (int)x) % 256 != 0)
            throw new InvalidDataException("The generated extension override is invalid.");
        VerifyExpected(instanceId, expectedFingerprint);
        using var parameters = Registry.LocalMachine.OpenSubKey(ParametersPath(instanceId), writable: true)
            ?? throw new InvalidDataException("The selected saved monitor no longer exists.");
        using var overrides = parameters.CreateSubKey("EDID_OVERRIDE", writable: true);
        overrides.SetValue(block.ToString(System.Globalization.CultureInfo.InvariantCulture), value, RegistryValueKind.Binary);
        overrides.Flush();
    }

    public void RemoveBlock(string instanceId, int block, string expectedFingerprint)
    {
        MonitorInstance.Validate(instanceId);
        EnsureDisconnected(instanceId);
        VerifyExpected(instanceId, expectedFingerprint);
        using var parameters = Registry.LocalMachine.OpenSubKey(ParametersPath(instanceId), writable: true)
            ?? throw new InvalidDataException("The selected saved monitor no longer exists.");
        using (var overrides = parameters.OpenSubKey("EDID_OVERRIDE", writable: true))
        {
            overrides?.DeleteValue(block.ToString(System.Globalization.CultureInfo.InvariantCulture), throwOnMissingValue: false);
            overrides?.Flush();
        }
        // Never delete the key: another tool may add a value concurrently. An empty key is inert.
    }

    private void VerifyExpected(string instanceId, string expectedFingerprint)
    {
        if (Read(instanceId).Fingerprint != expectedFingerprint)
            throw new InvalidOperationException("The saved monitor changed. Refresh before trying again.");
    }

    public static bool IsRazerBlade()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        return bios?.GetValue("SystemManufacturer") is string maker && maker.Contains("Razer", StringComparison.OrdinalIgnoreCase)
            && bios.GetValue("SystemProductName") is string model && model.Contains("Blade", StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureDisconnected(string instanceId)
    {
        if (PresentMonitorDevices.Read().Contains(instanceId))
            throw new InvalidOperationException("Disconnect the affected monitor before Patch or Restore. The app does not restart devices.");
    }

    private static string ParametersPath(string instanceId) => $@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters";
    private static void ValidateReadPath(string instanceId)
    {
        var parts = instanceId.Split('\\');
        if (parts.Length != 3 || parts[0] != "DISPLAY" || parts.Skip(1).Any(p => p.Length == 0
            || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('&' or '_' or '-' or '.')) || p is "." or ".."))
            throw new InvalidDataException("The saved monitor entry has an invalid identity.");
    }
}

internal static class PresentMonitorDevices
{
    private static readonly Guid MonitorClass = new("4d36e96e-e325-11ce-bfc1-08002be10318");
    public static HashSet<string> Read()
    {
        var guid = MonitorClass;
        var handle = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 2); // DIGCF_PRESENT
        if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint i = 0; ; i++)
            {
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(handle, i, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break; // ERROR_NO_MORE_ITEMS
                    throw new Win32Exception(error);
                }
                var text = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceId(handle, ref info, text, text.Capacity, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                result.Add(text.ToString());
            }
            return result;
        }
        finally { SetupDiDestroyDeviceInfoList(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DeviceInstance; public UIntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo data, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
