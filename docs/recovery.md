# Restore and recovery

English | [简体中文](zh-CN/recovery.md) | [Русский](ru-RU/recovery.md)

Keep the affected monitor unplugged until the app verifies Patch or Restore.

If administrator approval is canceled, the app refreshes its saved monitor status. If an operation was interrupted, its original backup remains available. Select the same connection and choose **Restore** when the button is available. This also handles an operation that created an empty override key before stopping.

If Restore is unavailable, keep the backup folder and use **Copy details** to report the problem. An EDID or override changed by another tool cannot be restored automatically. Use the tool that made that change to recover its original state first. Avoid another patch attempt until the saved data can be verified.

Use the Windows account that created the backup. The app stores backups outside its extracted folder, so downloading another copy does not lose them. A missing or invalid backup prevents automatic restoration of the installed override.

Backups from unfinished operations remain in the main backup folder. Finished backups are kept in an account-specific `history-` folder. The limit of 512 active backups prevents a new patch; it does not prevent Restore. If backups from another account are present, use that account to finish its recovery before patching again. Your own backups remain available for Restore.

If the app shows **Saved capabilities changed**, its installed override matches a backup, but Windows has different EDID data for that connection. Keep the backup folder and copy the details to report the change. Restore stays unavailable until the monitor's identity and saved data can be verified.

The EDID comparison in **Copy details** checks the saved data against the original backup and the exact patch the app would produce. A complete match to the patched EDID, together with a matching installed override, is consistent with Windows caching the patch. Other changes are listed by block and byte count. Missing, unreadable or ambiguous backups leave the comparison unavailable. Raw bytes and serial values stay private. The result is also included in the recording ZIP.

Do not upload backup JSON, temporary backup files, raw EDID, or registry exports. They can contain monitor serial numbers and machine-specific identifiers. Copied diagnostics omit those fields.

If reconnecting still crashes the display stack, unplug the monitor. Recover through the laptop's internal display and reopen the app to inspect the saved connection. The app does not reconnect monitors, restart display devices, reset the GPU, or change display modes for you.

## Saved entry unavailable

Keep the monitor unplugged. Select its saved connection and read the reason on the card. Open **Backups and diagnostics**, choose **Copy details**, and include that report and the monitor's exact model number when reporting the problem.

The report distinguishes missing or incomplete saved capability data, invalid data, a monitor identity mismatch, denied access, and an unrecognized override. Keep any existing overrides and original backups until the cause is known. The report omits raw EDID, serials, device instance IDs, and local paths.

The report includes the stored EDID length, declared block count, header and identity checks, every complete block's checksum, readable HDMI capability fields, and override status. It also includes other saved connections for the same monitor, so you can send one report without reconnecting it or inspecting each connection yourself. Capability fields read from invalid data are observations; they do not enable Patch.

A monitor can declare additional EDID blocks through an HDMI Forum extension-count override (HF-EEODB). The app checks that declaration, validates every block, and preserves the additional blocks when patching. A longer saved entry without a valid declaration remains unavailable. Copied details show both the base and effective block counts.

The extension-count rules follow the [Linux DRM EDID reader](https://raw.githubusercontent.com/torvalds/linux/master/drivers/gpu/drm/drm_edid.c) and [edid-decode validation](https://raw.githubusercontent.com/gjasny/v4l-utils/master/utils/edid-decode/parse-cta-block.cpp): one three-byte declaration at the start of the first CTA extension, with the stored length matching its count. The app requires CTA revision 3 or later for this declaration and requires every saved block to pass its checksum.

## Reporting repeated blanking after patching

If the laptop stays usable but the external picture keeps going blank, open the app on the laptop display and select the affected saved connection. Start **Record connection**. Enter the monitor firmware, exact monitor model, cable, adapter and port once. Set **Adaptive Sync** to On or Off from the monitor menu, or leave it Unknown if you cannot confirm it. This setting is manual context; the app does not detect the monitor menu.

Reconnect the monitor, let the fixed 60-second session run while the picture blanks, and mark each visible blackout. Stop early only if needed, then send the one diagnostic ZIP. Its baseline already includes the details that **Copy details** would provide, so a separate copied report is unnecessary.

The recording includes the laptop model, Windows build, present graphics adapters and their Windows driver versions, and the saved EDID monitor name. For the exact active target it also records the driving GPU, bounded signal pixel clock, rational sync and path refresh rates, active and total dimensions, scan-line order, color encoding, bits per color channel, connection type, HDR, NVIDIA VRR, and read-only SCDC observations when available. The driving GPU is resolved from the active target's adapter identity; the general driver inventory can contain other GPUs. Each unavailable value remains unknown independently, and active DSC remains unknown. An enabled VRR setting can be inactive at that instant.

You can collect the system and saved-monitor information while the monitor is unplugged. Live settings are unavailable for a disconnected entry or a connection the app cannot match exactly. Unsupported or failed driver queries show unknown. The monitor's own Adaptive Sync menu setting is not exposed by these queries. EDID names can be shortened, so an exact retail model number may still need the monitor's About page.

Include whether the laptop still crashes and the refresh rates already tested. Keep the original backups. Repeated blanking is not a reason to apply another patch or disable DSC without further evidence.

## Interrupted connection recordings

The app saves the baseline report and each timeline entry as recording proceeds. If the laptop crashes, reopen the app after restarting Windows. It recovers valid saved entries into a partial diagnostic ZIP and offers **Open recording folder**. It does not resume recording or reconnect the monitor.

The recovered summary identifies an interrupted session. The final write may be missing or incomplete; recovery keeps the readable entries before it and preserves the original files. Windows events and the private display log may be unavailable if the crash occurred before collection. Data that never reached storage cannot be recovered.

**Built-in display** identifies the connected laptop screen. **Stale built-in display entry** identifies an inactive saved record matching that screen. Select the external HDMI monitor for this workaround. Neither internal-panel entry is an HDMI patch target.
