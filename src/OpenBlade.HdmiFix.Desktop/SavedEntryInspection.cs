using Microsoft.Win32;
using OpenBlade.HdmiFix.Core;

namespace OpenBlade.HdmiFix.Desktop;

internal static class SavedEntryInspection
{
    internal static string DescribeEdid(string instanceId, object? value, RegistryValueKind? kind)
    {
        string report = "EDID registry type: " + (kind?.ToString() ?? "missing");
        if (value is not byte[] edid)
            return report + "\nEDID inspection: " + (value is null ? "no saved data" : "value is not binary data");
        report += "\n" + EdidPatchPlanner.InspectionText(edid);
        var model = EdidPatchPlanner.MonitorModelId(edid);
        report += "\nSaved connection identity: " + (model.Length == 0 ? "not validated"
            : instanceId.Split('\\')[1].Equals(model, StringComparison.OrdinalIgnoreCase) ? "matches" : "mismatch");
        return report;
    }
}
