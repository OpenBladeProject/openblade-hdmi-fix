# Restore and recovery

Keep the affected monitor unplugged until the app verifies Patch or Restore.

If administrator approval is canceled, the app refreshes its saved monitor status. If an operation was interrupted, its original backup remains available. Select the same connection and choose **Restore** when the button is available. This also handles an operation that created an empty override key before stopping.

If Restore is unavailable, keep the backup folder and use **Copy details** to report the problem. An EDID or override changed by another tool cannot be restored automatically. Use the tool that made that change to recover its original state first. Avoid another patch attempt until the saved data can be verified.

Use the Windows account that created the backup. The app stores backups outside its extracted folder, so downloading another copy does not lose them. A missing or invalid backup prevents automatic restoration of the installed override.

Backups from unfinished operations remain in the main backup folder. Finished backups are kept in an account-specific `history-` folder. The limit of 512 active backups prevents a new patch; it does not prevent Restore. If backups from another account are present, use that account to finish its recovery before patching again. Your own backups remain available for Restore.

If the app shows **Saved capabilities changed**, its installed override matches a backup, but Windows has different EDID data for that connection. Keep the backup folder and copy the details to report the change. Restore stays unavailable until the monitor's identity and saved data can be verified.

Do not upload backup JSON, temporary backup files, raw EDID, or registry exports. They can contain monitor serial numbers and machine-specific identifiers. Copied diagnostics omit those fields.

If reconnecting still crashes the display stack, unplug the monitor. Recover through the laptop's internal display and reopen the app to inspect the saved connection. The app does not reconnect monitors, restart display devices, reset the GPU, or change display modes for you.

## Saved entry unavailable

Keep the monitor unplugged. Select its saved connection and read the reason on the card. Open **Backups and diagnostics**, choose **Copy details**, and include that report and the monitor's exact model number when reporting the problem.

The report distinguishes missing or incomplete saved capability data, invalid data, a monitor identity mismatch, denied access, and an unrecognized override. Keep any existing overrides and original backups until the cause is known. The report omits raw EDID, serials, device instance IDs, and local paths.

The report includes the stored EDID length, declared block count, header and identity checks, every complete block's checksum, readable HDMI capability fields, and override status. It also includes other saved connections for the same monitor, so you can send one report without reconnecting it or inspecting each connection yourself. Capability fields read from invalid data are observations; they do not enable Patch.

A monitor can declare additional EDID blocks through an HDMI Forum extension-count override (HF-EEODB). The app checks that declaration, validates every block, and preserves the additional blocks when patching. A longer saved entry without a valid declaration remains unavailable. Copied details show both the base and effective block counts.

The extension-count rules follow the [Linux DRM EDID reader](https://raw.githubusercontent.com/torvalds/linux/master/drivers/gpu/drm/drm_edid.c) and [edid-decode validation](https://raw.githubusercontent.com/gjasny/v4l-utils/master/utils/edid-decode/parse-cta-block.cpp): one three-byte declaration at the start of the first CTA extension, with the stored length matching its count. The app requires CTA revision 3 or later for this declaration and requires every saved block to pass its checksum.
