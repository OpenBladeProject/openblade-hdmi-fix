# OpenBlade HDMI Fix agent guide

Keep this a small graphical Windows utility for Razer Blade owners. Follow the existing WPF interface and plain language used by Synapse Blade Blocker. Users must be able to select previously connected monitors without reconnecting a crashing display.

Work in a dedicated main-based Git worktree. Preserve unrelated work. Use purpose-prefixed branches and Conventional Commits. New fixes belong in a pull request. Keep a pull request draft while required exact-head checks or physical validation remain incomplete.

Keep EDID parsing and patch planning pure. The first profile supports only SAM7463 with the reviewed structured HDMI Forum block, DSC enabled, and the expected FRL fields. Never search and replace raw byte patterns, ship another machine's EDID, copy assumptions to another monitor, or weaken admission to make a configuration appear supported.

The GUI runs normally. Read-only inspection may enumerate all saved monitors. Patch and Restore require a disconnected selected monitor, a fresh state check, UAC through the fixed child operation, a durable original backup, and verified readback. Never add a generic elevated runner, automatic UAC approval, device resets, background reapplication, or registry ACL changes to force access.

After uncertain submission or readback, stop. Preserve the original and reconcile through read-only inspection. Restore only matching verified data; preserve foreign overrides and never delete an entire override key. Do not perform actual override writes or reconnect testing without separate explicit physical-validation authorization. Attended sleep/resume requires fresh approval.

Run focused tests, then the Release solution build and tests. Publish through `build/Publish.ps1`. Inspect the rendered WPF window at normal/minimum widths and expanded diagnostics for UI changes. Normalize all touched files to CRLF before committing and check the complete diff.

Update CHANGELOG.md for user-visible changes with implementing PR links. Version is in Directory.Build.props. Use GPL-3.0-only for independently written project code. Keep raw EDID, serial numbers, user paths, and backups out of Git and public reports.
