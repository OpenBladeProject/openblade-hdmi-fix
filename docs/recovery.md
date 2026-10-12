# Restore and recovery

Keep the affected monitor unplugged until the app verifies Patch or Restore.

If administrator approval is canceled, the app refreshes its saved monitor status. If an operation was interrupted, its original backup remains available. Select the same connection and choose **Restore** when the button is available. This also handles an operation that created an empty override key before stopping.

If Restore is unavailable, keep the backup folder and use **Copy details** to report the problem. An EDID or override changed by another tool cannot be restored automatically. Use the tool that made that change to recover its original state first. Avoid another patch attempt until the saved data can be verified.

Use the Windows account that created the backup. The app stores backups outside its extracted folder, so downloading another copy does not lose them. A missing or invalid backup prevents automatic restoration of the installed override.

Do not upload backup JSON, temporary backup files, raw EDID, or registry exports. They can contain monitor serial numbers and machine-specific identifiers. Copied diagnostics omit those fields.

If reconnecting still crashes the display stack, unplug the monitor. Recover through the laptop's internal display and reopen the app to inspect the saved connection. The app does not reconnect monitors, restart display devices, reset the GPU, or change display modes for you.
