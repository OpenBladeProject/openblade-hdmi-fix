using Microsoft.Win32;
using OpenBlade.HdmiFix.Core;

namespace OpenBlade.HdmiFix.Desktop;

internal static class SavedEntryInspection
{
    internal const string ComparisonReadUnavailable = "Saved EDID comparison: unavailable\nComparison reason: The saved entry could not be fully read and validated.";

    internal static string DescribeComparison(MonitorState state, BackupInventory inventory, bool trustedBackups)
    {
        if (!trustedBackups)
            return "Saved EDID comparison: unavailable\nComparison reason: The backup folder could not be verified.";
        if (inventory.HasPrivateBackups && !inventory.Records.Any(backup =>
            string.Equals(backup.InstanceId, state.InstanceId, StringComparison.Ordinal)))
            return "Saved EDID comparison: unavailable\nComparison reason: An original backup may belong to another Windows account.";
        return EdidBackupComparison.Compare(state, inventory.Records).ToDiagnosticText();
    }

    internal static string DescribeEdid(string instanceId, object? value, RegistryValueKind? kind)
    {
        string report = "EDID registry type: " + (kind?.ToString() ?? "missing");
        if (value is not byte[] edid)
            return report + "\nEDID inspection: " + (value is null ? "no saved data" : "value is not binary data");
        report += "\nSaved EDID monitor name: " + DiagnosticValues.Text(EdidPatchPlanner.MonitorName(edid), 13);
        report += "\n" + EdidPatchPlanner.InspectionText(edid);
        var model = EdidPatchPlanner.MonitorModelId(edid);
        report += "\nSaved connection identity: " + (model.Length == 0 ? "not validated"
            : instanceId.Split('\\')[1].Equals(model, StringComparison.OrdinalIgnoreCase) ? "matches" : "mismatch");
        return report;
    }
}
