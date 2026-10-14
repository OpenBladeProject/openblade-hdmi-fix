using System.ComponentModel;
using System.Security;

namespace OpenBlade.HdmiFix.Desktop;

public enum SavedMonitorReadFailure
{
    InvalidInstance,
    DeviceParametersMissing,
    EdidMissing,
    EdidIncomplete,
    EdidInvalid,
    IdentityMismatch,
    OverrideUnrecognized,
    AccessDenied,
    ReadFailed,
}

internal sealed class SavedMonitorReadException(SavedMonitorReadFailure reason, string inspection = "", string? baseFingerprint = null)
    : IOException(SavedMonitorReadErrors.Describe(reason))
{
    internal SavedMonitorReadFailure Reason { get; } = reason;
    internal string Inspection { get; } = inspection;
    // Private scan evidence only; never included in diagnostics or patch admission.
    internal string? BaseFingerprint { get; } = baseFingerprint;
}

internal static class SavedMonitorReadErrors
{
    internal static SavedMonitorReadFailure Classify(Exception error) => error switch
    {
        SavedMonitorReadException known => known.Reason,
        UnauthorizedAccessException or SecurityException => SavedMonitorReadFailure.AccessDenied,
        Win32Exception { NativeErrorCode: 5 } => SavedMonitorReadFailure.AccessDenied,
        _ => SavedMonitorReadFailure.ReadFailed,
    };

    // Fixed messages only: native exception text can contain paths or device identifiers.
    internal static string Describe(SavedMonitorReadFailure reason) => reason switch
    {
        SavedMonitorReadFailure.InvalidInstance => "This saved entry does not identify a standard monitor connection.",
        SavedMonitorReadFailure.DeviceParametersMissing => "Windows has no device parameters for this saved connection.",
        SavedMonitorReadFailure.EdidMissing => "Windows has no saved capability data (EDID) for this connection.",
        SavedMonitorReadFailure.EdidIncomplete => "The saved capability data (EDID) has missing or incomplete blocks.",
        SavedMonitorReadFailure.EdidInvalid => "The saved capability data (EDID) is incomplete or invalid.",
        SavedMonitorReadFailure.IdentityMismatch => "The saved capability data identifies a different monitor than this connection.",
        SavedMonitorReadFailure.OverrideUnrecognized => "This connection has an override format the app cannot recognize. Keep the override and any backups.",
        SavedMonitorReadFailure.AccessDenied => "Windows denied access to this saved monitor entry.",
        _ => "The app couldn't read this saved monitor entry.",
    };
}
