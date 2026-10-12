# How the patch works

Windows stores monitor instances under `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY`. The app enumerates those saved entries, including disconnected monitors, then reads each entry's `Device Parameters\EDID`. SetupAPI's present monitor list supplies the connection label. A failure to obtain that list disables mutation.

The first profile recognizes manufacturer `SAM`, product `0x7463`, a single HDMI Forum VSDB with OUI `0xC45DD8`, DSC 1.2 support, and two FRL fields at code 5. It validates the EDID header, exact extension count, every 128-byte checksum, and CTA data-block boundaries. Multiple Forum blocks, an alternative Forum SCDB, truncated data, and unexpected rate pairs are refused. Code 4 in both fields is reported as already capped.

Within the structured Forum block, the high nibbles at indexes 7 and 12 (counting its CTA block header) change from 5 to 4. Their low nibbles remain intact. The patch also recalculates the containing extension checksum. Every other byte remains unchanged, including the base EDID, HDMI physical address, DSC flags and parameters, timings, HDR, audio, VRR, and unknown extensions.

The field definitions were cross-checked against the [Linux DRM EDID parser](https://github.com/torvalds/linux/blob/master/drivers/gpu/drm/drm_edid.c). This is an implementation reference, not evidence of Windows display stability.

## Override scope

The app writes only the changed extension as one numbered 128-byte `REG_BINARY` value under the exact selected instance's `EDID_OVERRIDE`. [Microsoft documents block-level EDID overrides](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/overriding-monitor-edids); unchanged blocks continue to come from the monitor's EDID. Existing override values are refused unless they match this app's verified recovery record. The app never replaces another tool's override wholesale.

Saved records do not reliably identify a physical HDMI port or adapter. The app does not infer that a cached record proves a direct HDMI connection. Separate records are identified with stable connection labels; the selected instance is shown in the confirmation. A newly enumerated instance needs its own inspection. No automatic reapplication, device restart, reconnect, or graphics-driver reset occurs.

## Administrator operation and recovery

The WPF window runs at normal integrity. Patch and Restore start a short-lived copy of the same executable through Windows UAC. Its internal entry point accepts only the fixed operation, exact `DISPLAY\SAM7463\<instance>` identity, displayed EDID/override fingerprints, a backup ID for Restore, and the initiating user SID. It never accepts a registry root, command, or output path.

The child reconstructs the patch, checks Razer Blade identity, verifies disconnection again, checks the displayed state, and saves a durable `Prepared` record before writing. The registry store repeats disconnection and expected-state checks immediately before mutation. A named mutex serializes this app's administrator operations. Readback verifies the complete override state before recording `Applied`; the normal parent independently checks it.

Registry values are atomic, but Windows provides no conditional compare-and-set operation across independent override tools. Do not run another EDID editor or driver updater during Patch or Restore. If unexpected data appears, the app stops and keeps the original. It does not replay an uncertain write or overwrite foreign values to force recovery.

Restore accepts only the expected patched state, the saved original state, or an empty key left by an interrupted apply. It removes only this tool's value, never the entire registry key. An inert empty key is accepted as restoration when the original had no override values. If other values or a different EDID appear, Restore is unavailable.

Backups use atomic file replacement and a flushed temporary file. The backup directory and records must be administrator-owned with no untrusted write permissions or reparse points. Payload files grant read access to the initiating Windows account, Administrators, and SYSTEM. Their raw EDID and instance identity are local recovery data; copied diagnostics omit them. A process crash may leave a protected `.tmp` file. Preserve the folder for investigation; temporary files are never used as recovery records.

GPU, driver, monitor firmware, and exact laptop variants belong in physical-validation records. Razer Blade detection is an application scope check, not a claim that every Blade model has been tested.
