using Microsoft.Win32;
using OpenBlade.HdmiFix.Core;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.HdmiFix.Desktop;

internal sealed class MonitorRegistry : IMonitorOverrideStore
{
    private const string DisplayRoot = @"SYSTEM\CurrentControlSet\Enum\DISPLAY";

    public IReadOnlyList<string> EnumerateSavedInstances(out bool incomplete)
    {
        using var root = Registry.LocalMachine.OpenSubKey(DisplayRoot);
        if (root is null) { incomplete = false; return []; }
        return EnumerateSavedInstances(root.GetSubKeyNames(), model =>
        {
            using var modelKey = root.OpenSubKey(model);
            return modelKey?.GetSubKeyNames();
        }, out incomplete);
    }

    internal static IReadOnlyList<string> EnumerateSavedInstances(IEnumerable<string> models,
        Func<string, string[]?> readInstances, out bool incomplete)
    {
        incomplete = false;
        var result = new List<string>();
        foreach (var model in models.Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var instances = readInstances(model);
                if (instances is null) { incomplete = true; continue; }
                foreach (var instance in instances.Order(StringComparer.OrdinalIgnoreCase))
                    result.Add($@"DISPLAY\{model}\{instance}");
            }
            catch (Exception e) when (PatcherBackend.IsExpected(e)) { incomplete = true; }
        }
        return result;
    }

    public MonitorState Read(string instanceId)
    {
        try
        {
            try { MonitorInstance.Validate(instanceId); }
            catch (InvalidDataException) { throw new SavedMonitorReadException(SavedMonitorReadFailure.InvalidInstance); }
            using var key = Registry.LocalMachine.OpenSubKey(ParametersPath(instanceId));
            if (key is null) throw new SavedMonitorReadException(SavedMonitorReadFailure.DeviceParametersMissing);
            var edid = ValidateSavedEdid(instanceId, key.GetValue("EDID"));
            using var overrides = key.OpenSubKey("EDID_OVERRIDE");
            var blocks = new Dictionary<int, byte[]>();
            if (overrides is not null)
            {
                if (overrides.SubKeyCount != 0 || overrides.ValueCount > 256)
                    throw new SavedMonitorReadException(SavedMonitorReadFailure.OverrideUnrecognized);
                foreach (var name in overrides.GetValueNames())
                {
                    var value = ValidateOverrideValue(name, overrides.GetValueKind(name), overrides.GetValue(name));
                    blocks.Add(value.Key, value.Value);
                }
            }
            return new(instanceId, edid, new(overrides is not null, blocks));
        }
        catch (SavedMonitorReadException) { throw; }
        catch (Exception e) when (PatcherBackend.IsExpected(e))
        {
            throw new SavedMonitorReadException(SavedMonitorReadErrors.Classify(e));
        }
    }

    internal static byte[] ValidateSavedEdid(string instanceId, object? value)
    {
        if (value is null) throw new SavedMonitorReadException(SavedMonitorReadFailure.EdidMissing);
        if (value is not byte[] edid || edid.Length > 32768)
            throw new SavedMonitorReadException(SavedMonitorReadFailure.EdidInvalid);
        if (EdidPatchPlanner.MonitorModelId(edid).Length == 0)
        {
            var incomplete = edid.Length < 128 || edid.Length % 128 != 0
                || (edid[126] + 1) * 128 > edid.Length;
            throw new SavedMonitorReadException(incomplete
                ? SavedMonitorReadFailure.EdidIncomplete : SavedMonitorReadFailure.EdidInvalid);
        }
        try { MonitorInstance.ValidateEdid(instanceId, edid); }
        catch (InvalidDataException) { throw new SavedMonitorReadException(SavedMonitorReadFailure.IdentityMismatch); }
        return edid;
    }

    internal static KeyValuePair<int, byte[]> ValidateOverrideValue(string name, RegistryValueKind kind, object? value)
    {
        if (!int.TryParse(name, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number)
            || number is < 0 or > 255 || name != number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || kind != RegistryValueKind.Binary || value is not byte[] data || data.Length != 128)
            throw new SavedMonitorReadException(SavedMonitorReadFailure.OverrideUnrecognized);
        return new(number, data);
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
