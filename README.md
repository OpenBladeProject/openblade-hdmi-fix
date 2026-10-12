# OpenBlade HDMI Fix

Patch a Samsung G93SC monitor's saved HDMI capabilities while the monitor stays unplugged. The app lists connected and previously connected monitors. It saves an original backup so you can restore the connection later.

The workaround caps the monitor's advertised maximum HDMI link rates at FRL8 while keeping DSC enabled.

## Requirements

- Windows 11 x64 on a Razer Blade.
- A previously connected Samsung G93SC (`SAM7463`) with complete capability data saved by Windows, DSC enabled, and both maximum FRL fields set to FRL10.
- Administrator permission when you choose Patch or Restore.

The window uses the same light, compact layout as [Synapse Blade Blocker](https://github.com/OpenBladeProject/synapse-blade-blocker).

## Use the app

1. Keep the affected monitor unplugged.
2. Extract the complete Windows ZIP and double-click **OpenBlade.HdmiFix.exe**. Start it normally; the app asks for administrator permission when needed.
3. Select the monitor from its saved entries. Different connection records appear separately. Review the connection label and proposed patch.
4. Choose **Patch**, accept the administrator prompt, and wait for the app to verify the installed override.
5. Reconnect the monitor after the app confirms the patch. Check your resolution, refresh rate, HDR, VRR, and audio.

Only the selected saved connection is patched. Windows can create another entry after a port, adapter, or driver change. If the problem remains, unplug the monitor and check the saved entries again.

If Windows has no complete saved data, Patch stays unavailable. You do not need to reconnect a crashing monitor for inspection.

## Restore

Unplug the affected monitor and open the app under the Windows account that created the backup. Select the same saved connection and choose **Restore**. Wait for confirmation before reconnecting. Restore removes the patched value. It may leave an empty override key, which does not override any EDID block.

Backups stay in `%ProgramData%\OpenBlade.HdmiFix.Backups` when you move or replace the app folder. Keep them until you have finished using the workaround. Open **Backups and diagnostics** to find the folder or copy a report that omits raw EDID, serial numbers, device instance IDs, and local paths.

See [recovery](docs/recovery.md), [technical details](docs/technical.md), and [building](docs/building.md).

## License

GPL-3.0-only. The code is independently written, and the test fixtures are synthetic. No vendor EDID dump or firmware is bundled.
