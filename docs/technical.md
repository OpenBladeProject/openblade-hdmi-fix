# How the patch works

Windows stores monitor instances under `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY`. The app enumerates those saved entries, including disconnected monitors, then reads each entry's `Device Parameters\EDID`. SetupAPI's present monitor list supplies the connection label. A failure to obtain that list disables mutation.

The patch accepts any monitor manufacturer and product with a single HDMI Forum VSDB with OUI `0xC45DD8`, DSC 1.2 support, and two FRL fields at code 5. It validates the EDID header, manufacturer encoding, exact extension count, every 128-byte checksum, and CTA data-block boundaries. The manufacturer and product decoded from the EDID must match the selected saved Windows instance. Every eligible monitor displays an unvalidated warning; this app has no physically validated monitor models. Multiple Forum blocks, an alternative Forum SCDB, truncated data, and unexpected rate pairs are refused. Code 4 in both fields is reported as already capped.

A CTA data-block offset of zero means no data blocks or detailed timing descriptors. New patches require zero padding in that extension and never interpret it as capabilities. Schema-version-1 backup verification retains the earlier parser's zero-offset behavior only for recovery of an existing exact SAM7463 two-field override. Other monitor backups use the strict current parser. It cannot enable a new patch; restoration still requires the identical saved EDID and override.

Within the structured Forum block, the high nibbles at indexes 7 and 12 (counting its CTA block header) change from 5 to 4. Their low nibbles remain intact. The patch also recalculates the containing extension checksum. Every other byte remains unchanged, including the base EDID, HDMI physical address, DSC flags and parameters, timings, HDR, audio, VRR, and unknown extensions.

The field definitions were cross-checked against the [Linux DRM EDID parser](https://github.com/torvalds/linux/blob/master/drivers/gpu/drm/drm_edid.c). This is an implementation reference, not evidence of Windows display stability.

## Override scope

The app writes only the changed extension as one numbered 128-byte `REG_BINARY` value under the exact selected instance's `EDID_OVERRIDE`. [Microsoft documents block-level EDID overrides](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/overriding-monitor-edids); unchanged blocks continue to come from the monitor's EDID. Existing override values are refused unless they match this app's verified recovery record. The app never replaces another tool's override wholesale.

Saved records do not reliably identify a physical HDMI port or adapter. The app does not infer that a cached record proves a direct HDMI connection. Separate records are identified with stable connection labels; the selected instance is shown in the confirmation. A newly enumerated instance needs its own inspection. No automatic reapplication, device restart, reconnect, or graphics-driver reset occurs.

## Administrator operation and recovery

The WPF window runs at normal integrity. Patch and Restore start a short-lived copy of the same executable through Windows UAC. Its internal entry point accepts only the fixed operation, exact `DISPLAY\<manufacturer><product>\<instance>` identity, bound to the cached EDID, displayed EDID/override fingerprints, a backup ID for Restore, and the initiating user SID. It never accepts a registry root, command, or output path.

The child reconstructs the patch, checks Razer Blade identity, verifies disconnection again, checks the displayed state, and saves a durable `Prepared` record before writing. The registry store repeats disconnection and expected-state checks immediately before mutation. A named mutex serializes this app's administrator operations. Readback verifies the complete override state before recording `Applied`; the normal parent independently checks it.

Registry values are atomic, but Windows provides no conditional compare-and-set operation across independent override tools. Do not run another EDID editor or driver updater during Patch or Restore. If unexpected data appears, the app stops and keeps the original. It does not replay an uncertain write or overwrite foreign values to force recovery.

Restore accepts only the expected patched state, the saved original state, or an empty key left by an interrupted apply. It removes only this tool's value, never the entire registry key. An inert empty key is accepted as restoration when the original had no override values. If other values or a different EDID appear, Restore is unavailable.

Backups use atomic file replacement and a flushed temporary file. The backup directory and records must be administrator-owned with no untrusted write permissions or reparse points. Payload files grant read access to the initiating Windows account, Administrators, and SYSTEM. Their raw EDID and instance identity are local recovery data; copied diagnostics omit them. A process crash may leave a protected `.tmp` file. Preserve the folder for investigation; temporary files are never used as recovery records.

The helper applies the initiating account's explicit read grant when inspecting payloads, even with administrator credentials. Private active records block new patches and produce an account-specific explanation; they do not disable restoration of that account's own backups. Discovery has no record-count cutoff. Only new backups are limited to 512 active records, checked before any registry mutation under the operation mutex. Completed records move into an account-specific `history-` folder with the same three principals as the payload and remain readable by ID. Legacy completed records in the main folder are excluded from the active count.

GPU, driver, monitor firmware, and exact laptop variants belong in physical-validation records. Razer Blade detection is an application scope check, not a claim that every Blade model has been tested.

## Read-only connection diagnostics

Each Refresh captures host information, present display-adapter driver keys through SetupAPI, and active Windows DisplayConfig paths. Live observations are associated only with the full selected monitor instance, not its model code or a neighboring saved connection. Copy details uses that snapshot and does not query or mutate devices again.

HDR comes from Windows' advanced-color-info version 2 query. NVIDIA VRR comes from the installed system NVAPI library, using the DisplayConfig adapter LUID and target ID. Capability, requested state, enabled state and instantaneous VRR activity are separate observations. These fields do not prove which HDMI FRL rate or DSC mode the driver negotiated. A missing library, unsupported query, incomplete topology or ambiguous mapping leaves the affected observation unknown.

Only approved descriptive fields and generated status values appear in the report. Device-interface paths and adapter identities remain private matching data; serials, raw EDID, registry paths and native exception text are omitted. These diagnostic reads do not change Patch or Restore admission.

Unresolved active paths retain bounded error categories and numeric statuses in the report. They leave matching unknown while preserving independent exact matches. Host and live-display capture have separate failure guards so a recoverable optional-reader failure cannot stop saved-monitor discovery or retain stale live observations.

## Recording a connection

Record connection samples the selected full monitor instance and active display topology for 60 seconds. It records UTC timestamps and elapsed time, Windows display/device notifications, mode/HDR/NVIDIA VRR observations, query failures and user blackout marks. Optional model, firmware and cable fields are identified as user-provided context. A newly connected matching instance is recognized even when the selection came from a disconnected scan. Connection state is unknown when reads fail or matching is incomplete or ambiguous. Other displays receive recording-local labels; raw instance IDs are not written.

The timeline is saved incrementally in the recording folder under `%LocalAppData%\OpenBlade.HdmiFix\Recordings`. Stop and save or closing the window ends sampling and retains partial results. Patch, Restore, Refresh and monitor selection are unavailable during recording. Recording does not alter EDID, display settings, drivers or device state.

The shareable ZIP contains the baseline report, timeline, summary and bounded Windows event metadata for the capture interval. Event payloads and messages are omitted. Failed or limited queries are reported explicitly. A separate `private/display-log.dat`, collected by the installed Windows `dispdiag.exe`, may contain identifying system data and is excluded from the ZIP. No data is uploaded automatically.

These observations do not measure HDMI FRL negotiation, DSC activity or the physical picture. A display can blank without a Windows topology notification; user marks correlate visible blackouts with recorded observations. Event IDs alone do not identify a particular GPU timeout cause.
