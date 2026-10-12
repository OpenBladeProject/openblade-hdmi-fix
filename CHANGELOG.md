# Changelog

## Unreleased

### Added

- Allow monitors from any brand with matching saved HDMI capabilities, show an unvalidated-monitor warning, and verify each selected connection against its EDID identity. Simplify the README for laptop owners. ([#2](https://github.com/OpenBladeProject/openblade-hdmi-fix/pull/2))

- Add a graphical HDMI workaround for saved Samsung G93SC connections, with historical monitor selection, original backups, Restore, and diagnostics. ([#2](https://github.com/OpenBladeProject/openblade-hdmi-fix/pull/2))

### Fixed

- Refuse CTA padding as patchable capability data, retain recovery for version-1 backups, and keep Restore available when the active backup limit is reached. Explain account-specific backups and changed saved capabilities, preserve finished backups in history, and require clean source for packaged builds. ([#2](https://github.com/OpenBladeProject/openblade-hdmi-fix/pull/2))
