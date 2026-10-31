using System.Security.Cryptography;
using System.Text;

namespace OpenBlade.HdmiFix.Desktop;

public sealed record RecordingTargetChoice(MonitorView Monitor, bool IsActive)
{
    public override string ToString() => DisplayLabel;
    public string DisplayLabel => $"{(IsActive ? "Connected HDMI" : "Saved connection")} · {Monitor.Name} · {Monitor.InstanceLabel}";
}

internal static class RecordingTargets
{
    // These choices are for read-only recording. Never substitute a different
    // instance in Patch/Restore, or infer physical identity from a model name.
    internal static IReadOnlyList<MonitorView> FromSnapshot(IReadOnlyList<MonitorView> saved, LiveDisplayScan live)
    {
        var targets = new List<MonitorView>();
        foreach (var group in live.Displays.Where(d => !string.IsNullOrWhiteSpace(d.InstanceId))
            .GroupBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase))
        {
            LiveDisplayDiagnostics[] paths = group.Take(2).ToArray();
            if (paths.Length != 1) continue;
            var display = paths[0];
            string[] parts = display.InstanceId.Split('\\');
            if (parts.Length != 3 || !parts[0].Equals("DISPLAY", StringComparison.OrdinalIgnoreCase)
                || parts.Any(string.IsNullOrWhiteSpace) || display.Connection != "HDMI"
                || display.Error.Contains("Binding:Ambiguous", StringComparison.Ordinal)) continue;
            string name = DiagnosticValues.Text(display.MonitorName);
            string label = "Connection " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(display.InstanceId)))[..8];
            MonitorView[] matches = saved.Where(s => s.InstanceId.Equals(display.InstanceId,
                StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length > 1) continue;
            var target = matches.SingleOrDefault() ?? new MonitorView(display.InstanceId, name,
                "Connected", label, "Live connection", "This connection is available for read-only recording.", false, false, null);
            targets.Add(target with { Name = name == "unknown" ? target.Name : name,
                ConnectionLabel = "Connected", CanPatch = false, CanRestore = false });
        }
        return targets.OrderBy(t => t.DisplayLabel, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<RecordingTargetChoice> Choices(MonitorView? saved, IReadOnlyList<MonitorView> active)
    {
        var choices = active.Select(t => new RecordingTargetChoice(t, true)).ToList();
        if (saved is not null && !choices.Any(c => c.Monitor.InstanceId.Equals(saved.InstanceId,
            StringComparison.OrdinalIgnoreCase))) choices.Add(new(saved, false));
        return choices;
    }

    internal static RecordingTargetChoice? Default(MonitorView? saved, IReadOnlyList<RecordingTargetChoice> choices) =>
        saved is null ? null : choices.FirstOrDefault(c => c.IsActive && c.Monitor.InstanceId.Equals(saved.InstanceId,
            StringComparison.OrdinalIgnoreCase)) ?? (choices.Any(c => c.IsActive) ? null : choices.FirstOrDefault());
}
