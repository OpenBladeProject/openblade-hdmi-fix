using System.Text;

namespace OpenBlade.HdmiFix.Core;

public enum EdidBackupComparisonKind
{
    ExactOriginal,
    ExactExpectedPatched,
    OtherChanges,
    Unavailable,
}

public enum EdidBackupComparisonUnavailableReason
{
    None,
    CurrentInstanceInvalid,
    CurrentEdidUnavailable,
    CurrentEdidTooLarge,
    CurrentEdidInvalid,
    CurrentEdidIdentityMismatch,
    CurrentOverrideInvalid,
    NoEligibleBackup,
    InvalidBackup,
    AmbiguousBackups,
}

public sealed record EdidBackupDifference(
    IReadOnlyList<int> ChangedBlockNumbers,
    int ChangedByteCount,
    int AddedByteCount,
    int RemovedByteCount,
    bool BaseIdentityChanged,
    bool SerialAreaChanged)
{
    public int TotalDifferentByteCount => ChangedByteCount + AddedByteCount + RemovedByteCount;
}

public sealed record EdidBackupComparisonResult(
    EdidBackupComparisonKind Kind,
    string Summary,
    EdidBackupComparisonUnavailableReason UnavailableReason,
    int CurrentLength,
    int? OriginalLength,
    int? ExpectedPatchedLength,
    bool EdidMatchesOriginal,
    bool EdidMatchesExpectedPatched,
    bool CurrentOverridesMatchOriginal,
    bool CurrentOverridesMatchPatched,
    EdidBackupDifference? FromOriginal,
    EdidBackupDifference? FromExpectedPatched)
{
    public string ToDiagnosticText()
    {
        var report = new StringBuilder();
        report.AppendLine($"Saved EDID comparison: {DisplayKind(Kind)}");
        report.AppendLine(Summary);
        report.AppendLine($"Current EDID bytes: {CurrentLength}");
        if (OriginalLength is int originalLength)
            report.AppendLine($"Saved original EDID bytes: {originalLength}");
        if (ExpectedPatchedLength is int expectedLength)
            report.AppendLine($"Expected patched EDID bytes: {expectedLength}");
        if (Kind == EdidBackupComparisonKind.Unavailable)
            return report.Append($"Comparison unavailable reason: {UnavailableReason}").ToString();

        report.AppendLine("Current override matches saved original: " + YesNo(CurrentOverridesMatchOriginal));
        report.AppendLine("Current override matches saved patch: " + YesNo(CurrentOverridesMatchPatched));
        AppendDifference(report, "Compared with saved original", FromOriginal!);
        AppendDifference(report, "Compared with expected patched EDID", FromExpectedPatched!);
        report.AppendLine("The active driver's EDID and the blackout cause remain unverified.");
        return report.ToString().TrimEnd();
    }

    private static void AppendDifference(StringBuilder report, string label, EdidBackupDifference difference)
    {
        string blocks = difference.ChangedBlockNumbers.Count == 0
            ? "none"
            : string.Join(", ", difference.ChangedBlockNumbers);
        report.AppendLine($"{label}: changed blocks {blocks}; changed bytes {difference.ChangedByteCount}; added bytes {difference.AddedByteCount}; removed bytes {difference.RemovedByteCount}.");
        report.AppendLine($"{label}: base identity fields changed {YesNo(difference.BaseIdentityChanged)}; serial-related fields changed {YesNo(difference.SerialAreaChanged)}.");
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string DisplayKind(EdidBackupComparisonKind kind) => kind switch
    {
        EdidBackupComparisonKind.ExactOriginal => "exact original",
        EdidBackupComparisonKind.ExactExpectedPatched => "exact expected patch",
        EdidBackupComparisonKind.OtherChanges => "other changes",
        _ => "unavailable",
    };
}

public static class EdidBackupComparison
{
    private const int BlockSize = 128;
    private const int MaximumEdidBytes = 32768;

    public static EdidBackupComparisonResult Compare(
        MonitorState state,
        IReadOnlyList<PatchBackup> records)
    {
        if (state is null)
            return Unavailable(EdidBackupComparisonUnavailableReason.CurrentEdidUnavailable, 0,
                "The current saved monitor state is unavailable.");
        int currentLength = state.Edid?.Length ?? 0;
        if (state.Edid is null || state.Edid.Length == 0)
            return Unavailable(EdidBackupComparisonUnavailableReason.CurrentEdidUnavailable, currentLength,
                "The current saved EDID is unavailable.");
        if (state.Edid.Length > MaximumEdidBytes)
            return Unavailable(EdidBackupComparisonUnavailableReason.CurrentEdidTooLarge, currentLength,
                "The current saved EDID exceeds the 32768-byte diagnostic limit.");

        try
        {
            MonitorInstance.Validate(state.InstanceId);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NullReferenceException)
        {
            return Unavailable(EdidBackupComparisonUnavailableReason.CurrentInstanceInvalid, currentLength,
                "The selected saved monitor instance is invalid.");
        }

        string currentModel = EdidPatchPlanner.MonitorModelId(state.Edid);
        if (currentModel.Length == 0)
            return Unavailable(EdidBackupComparisonUnavailableReason.CurrentEdidInvalid, currentLength,
                "The current saved EDID is invalid, incomplete, or has a bad checksum.");
        if (!state.InstanceId.Split('\\')[1].Equals(currentModel, StringComparison.OrdinalIgnoreCase))
            return Unavailable(EdidBackupComparisonUnavailableReason.CurrentEdidIdentityMismatch, currentLength,
                "The current saved EDID identity does not match the selected monitor instance.");

        string currentOverrideFingerprint;
        try
        {
            currentOverrideFingerprint = state.Overrides.Fingerprint();
        }
        catch (Exception e) when (e is ArgumentException or NullReferenceException)
        {
            return Unavailable(EdidBackupComparisonUnavailableReason.CurrentOverrideInvalid, currentLength,
                "The current override state is incomplete or invalid.");
        }

        if (records is null)
            return Unavailable(EdidBackupComparisonUnavailableReason.NoEligibleBackup, currentLength,
                "No active backup is available for this exact saved monitor instance.");

        PatchBackup[] candidates = records
            .Where(record => record is not null
                && record.Stage != "Restored"
                && string.Equals(record.InstanceId, state.InstanceId, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0)
            return Unavailable(EdidBackupComparisonUnavailableReason.NoEligibleBackup, currentLength,
                "No active backup is available for this exact saved monitor instance.");

        foreach (PatchBackup candidate in candidates)
        {
            try
            {
                candidate.Validate();
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or NullReferenceException)
            {
                return Unavailable(EdidBackupComparisonUnavailableReason.InvalidBackup, currentLength,
                    "An active backup for this exact saved monitor instance is invalid or inconsistent.");
            }
        }
        if (candidates.Length != 1)
            return Unavailable(EdidBackupComparisonUnavailableReason.AmbiguousBackups, currentLength,
                "Multiple active backups exist for this exact saved monitor instance, so no backup was selected.");

        PatchBackup backup = candidates[0];
        byte[] expectedPatched = BuildExpectedPatchedEdid(backup);
        bool edidMatchesOriginal = state.Edid.AsSpan().SequenceEqual(backup.OriginalEdid);
        bool edidMatchesExpected = state.Edid.AsSpan().SequenceEqual(expectedPatched);
        bool overridesMatchOriginal = currentOverrideFingerprint == backup.OriginalOverrides.Fingerprint();
        bool overridesMatchPatched = currentOverrideFingerprint == backup.PatchedOverrides.Fingerprint();
        EdidBackupDifference fromOriginal = Difference(backup.OriginalEdid, state.Edid);
        EdidBackupDifference fromExpected = Difference(expectedPatched, state.Edid);

        EdidBackupComparisonKind kind;
        string summary;
        if (edidMatchesOriginal)
        {
            kind = EdidBackupComparisonKind.ExactOriginal;
            summary = overridesMatchOriginal
                ? "The saved EDID and installed override match the original backup."
                : overridesMatchPatched
                    ? "The saved EDID matches the original backup. The installed override matches this app's saved patch."
                    : "The saved EDID matches the original backup. The installed override differs from both saved states.";
        }
        else if (edidMatchesExpected)
        {
            kind = EdidBackupComparisonKind.ExactExpectedPatched;
            summary = overridesMatchPatched
                ? "Every saved EDID byte matches the expected patch. The installed override also matches. This is consistent with Windows caching this app's patched EDID."
                : overridesMatchOriginal
                    ? "Every saved EDID byte matches the expected patch. The installed override matches the original backup."
                    : "Every saved EDID byte matches the expected patch. The installed override differs from both saved states.";
        }
        else
        {
            kind = EdidBackupComparisonKind.OtherChanges;
            summary = overridesMatchPatched
                ? "The saved EDID differs from both the original backup and the expected patch. The installed override matches this app's saved patch."
                : overridesMatchOriginal
                    ? "The saved EDID differs from both the original backup and the expected patch. The installed override matches the original backup."
                    : "The saved EDID differs from both the original backup and the expected patch. The installed override differs from both saved states.";
        }

        return new EdidBackupComparisonResult(
            kind,
            summary,
            EdidBackupComparisonUnavailableReason.None,
            currentLength,
            backup.OriginalEdid.Length,
            expectedPatched.Length,
            edidMatchesOriginal,
            edidMatchesExpected,
            overridesMatchOriginal,
            overridesMatchPatched,
            fromOriginal,
            fromExpected);
    }

    private static byte[] BuildExpectedPatchedEdid(PatchBackup backup)
    {
        byte[] expected = backup.OriginalEdid.ToArray();
        foreach ((int blockNumber, byte[] block) in backup.PatchedOverrides.Blocks)
            block.CopyTo(expected, blockNumber * BlockSize);
        return expected;
    }

    private static EdidBackupDifference Difference(byte[] reference, byte[] current)
    {
        int overlap = Math.Min(reference.Length, current.Length);
        int changedBytes = 0;
        var changedBlocks = new SortedSet<int>();
        for (int index = 0; index < overlap; index++)
        {
            if (reference[index] == current[index]) continue;
            changedBytes++;
            changedBlocks.Add(index / BlockSize);
        }
        for (int index = overlap; index < Math.Max(reference.Length, current.Length); index++)
            changedBlocks.Add(index / BlockSize);

        return new EdidBackupDifference(
            Array.AsReadOnly(changedBlocks.ToArray()),
            changedBytes,
            Math.Max(0, current.Length - reference.Length),
            Math.Max(0, reference.Length - current.Length),
            RegionChanged(reference, current, 8, 4),
            SerialAreaChanged(reference, current));
    }

    private static bool SerialAreaChanged(byte[] reference, byte[] current)
    {
        if (RegionChanged(reference, current, 12, 4)) return true;
        foreach (int offset in new[] { 54, 72, 90, 108 })
        {
            bool referenceSerial = IsSerialDescriptor(reference, offset);
            bool currentSerial = IsSerialDescriptor(current, offset);
            if ((referenceSerial || currentSerial) && RegionChanged(reference, current, offset, 18))
                return true;
        }
        return false;
    }

    private static bool IsSerialDescriptor(byte[] edid, int offset) =>
        edid.Length >= offset + 5
        && edid[offset] == 0
        && edid[offset + 1] == 0
        && edid[offset + 2] == 0
        && edid[offset + 3] == 0xFF
        && edid[offset + 4] == 0;

    private static bool RegionChanged(byte[] left, byte[] right, int offset, int length)
    {
        if (left.Length < offset + length || right.Length < offset + length)
            return left.Length != right.Length;
        return !left.AsSpan(offset, length).SequenceEqual(right.AsSpan(offset, length));
    }

    private static EdidBackupComparisonResult Unavailable(
        EdidBackupComparisonUnavailableReason reason,
        int currentLength,
        string summary) =>
        new(
            EdidBackupComparisonKind.Unavailable,
            summary,
            reason,
            currentLength,
            null,
            null,
            false,
            false,
            false,
            false,
            null,
            null);
}
