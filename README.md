# OpenBlade HDMI Fix

English | [简体中文](README.zh-CN.md) | [Русский](README.ru-RU.md)

Apply an HDMI workaround to a saved monitor connection on a Razer Blade. The app lists monitors Windows remembers, so you can patch the affected connection while the monitor stays unplugged.

Patch lowers the monitor's advertised maximum HDMI link rates from FRL10 to FRL8 and keeps DSC enabled. It saves an original backup for Restore.

## Requirements

- Windows 11 x64 on a Razer Blade.
- A previously connected HDMI monitor with complete capability data saved by Windows.

The app checks the saved HDMI and DSC fields before enabling Patch, regardless of the monitor's brand or model. The app shows a warning that the workaround hasn't been validated on the selected monitor.

## Use the app

1. Keep the affected monitor unplugged.
2. Download the Windows ZIP from [Releases](https://github.com/OpenBladeProject/openblade-hdmi-fix/releases), extract it, and open **OpenBlade.HdmiFix.exe** in the `publish` folder. Start it normally.
3. Select the saved monitor connection and review the proposed patch. Separate connection records appear as separate entries.
4. Choose **Patch**, accept the administrator prompt, and wait for confirmation.
5. Reconnect the monitor and check resolution, refresh rate, HDR, VRR, and audio.

Only the selected connection is patched. After a port, adapter, or driver change, Windows may create another entry. If the problem remains, unplug the monitor and check its saved entries again. Missing or incompatible saved data leaves Patch unavailable.

## Restore and backups

Unplug the monitor and open the app under the Windows account that created the backup. Select the same saved connection and choose **Restore**. Wait for confirmation before reconnecting.

Backups stay in `%ProgramData%\OpenBlade.HdmiFix.Backups` when you replace the app folder. Keep them until you've finished using the workaround. **Backups and diagnostics** opens the folder or copies a report without raw EDID, serial numbers, device instance IDs, or local paths.

To investigate repeated blanking, select the affected saved connection and choose **Record connection** before plugging it in. Mark each blackout; recording stops after 60 seconds. Open the recording folder and share `connection-recording.zip`. The separate Windows display log may contain private system data.

See [recovery](docs/recovery.md), [technical details](docs/technical.md), and [building](docs/building.md).

## License

[GPL-3.0-only](LICENSE).
