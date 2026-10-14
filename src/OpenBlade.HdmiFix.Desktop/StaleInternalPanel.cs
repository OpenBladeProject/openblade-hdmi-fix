using OpenBlade.HdmiFix.Core;
using System.Security.Cryptography;

namespace OpenBlade.HdmiFix.Desktop;

internal static class StaleInternalPanel
{
    internal static string? BaseFingerprint(string instanceId, byte[] edid)
    {
        if (edid.Length is < 128 or > 32768 || (edid.Take(128).Sum(x => (int)x) & 255) != 0)
            return null;
        // Validate the base independently of missing extensions. This temporary
        // copy is never returned as capabilities or used to plan an override.
        var baseBlock = edid[..128];
        baseBlock[126] = 0;
        baseBlock[127] = unchecked((byte)-baseBlock.Take(127).Sum(x => (int)x));
        try { MonitorInstance.ValidateEdid(instanceId, baseBlock); }
        catch (InvalidDataException) { return null; }
        return Convert.ToHexString(SHA256.HashData(edid.AsSpan(0, 128)));
    }

    internal static bool Matches(string instanceId, string fingerprint, HashSet<string>? present,
        LiveDisplayScan live, IReadOnlyDictionary<string, MonitorState> states)
    {
        if (present is null || present.Contains(instanceId) || live.HasUnresolvedPaths || live.Failure.Length != 0)
            return false;
        // All active paths need their own readable identity. A matching external
        // or duplicate path makes this presentation inference ambiguous.
        var matches = new List<LiveDisplayDiagnostics>();
        foreach (var display in live.Displays)
        {
            if (!present.Contains(display.InstanceId) || !states.TryGetValue(display.InstanceId, out var state))
                return false;
            if (EdidPatchPlanner.MonitorModelId(state.Edid).Length == 0)
                return false;
            if (BaseFingerprint(display.InstanceId, state.Edid) == fingerprint)
                matches.Add(display);
        }
        return matches.Count == 1 && matches[0].Connection == "Internal";
    }

    internal static MonitorView Describe(MonitorView view) => view with
    {
        Status = "Stale built-in display entry",
        Details = "This inactive entry matches your connected built-in screen. Windows saved only part of its capability data here. Select the affected HDMI monitor instead. Patch and Restore are unavailable for this entry.",
        IsStaleInternalPanel = true,
        CanPatch = false,
        CanRestore = false,
        BackupPath = null,
    };
}
