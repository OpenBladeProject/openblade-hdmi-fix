# Changelog

## Unreleased

## [0.2.4] - 2026-10-13

### Fixed

- Recognize valid HDMI Forum extension-count overrides, so monitors with additional saved EDID blocks can be patched without removing those blocks. Verify the full declared data and preserve its identity and checksums. ([#5](https://github.com/OpenBladeProject/openblade-hdmi-fix/pull/5))

## [0.2.3] - 2026-10-13

### Fixed

- Include all EDID validation checks, HDMI capability observations, override status, and other saved connections for the same monitor in one copied report. Keep the monitor unplugged while collecting it. ([#5](https://github.com/OpenBladeProject/openblade-hdmi-fix/pull/5))

## [0.2.2] - 2026-10-13

### Fixed

- Show why a saved monitor entry is unavailable and include that reason in copied diagnostics, so it can be checked while the monitor stays unplugged. Keep private device data out of the report. ([#5](https://github.com/OpenBladeProject/openblade-hdmi-fix/pull/5))

## [0.2.1] - 2026-10-13

### Fixed

- Keep saved monitors visible when an entry or backup fails validation, and handle administrator-operation validation errors through the existing recovery messages. Preserve readable connections when another saved model is inaccessible, and keep backup guidance accurate when inventory cannot be verified. Refuse existing release archives and checksums before packaging starts, and preserve checksums created during a build. ([#2](https://github.com/OpenBladeProject/openblade-hdmi-fix/pull/2))

## [0.2.0] - 2026-10-12

### Added

- Allow monitors from any brand with matching saved HDMI capabilities, show an unvalidated-monitor warning, and verify each selected connection against its EDID identity. Simplify the README for laptop owners. ([#2](https://github.com/OpenBladeProject/openblade-hdmi-fix/pull/2))

- Add a graphical HDMI workaround for saved Samsung G93SC connections, with historical monitor selection, original backups, Restore, and diagnostics. ([#2](https://github.com/OpenBladeProject/openblade-hdmi-fix/pull/2))

### Fixed

- Refuse CTA padding as patchable capability data, retain recovery for version-1 backups, and keep Restore available when the active backup limit is reached. Explain account-specific backups and changed saved capabilities, preserve finished backups in history, and require clean source for packaged builds. ([#2](https://github.com/OpenBladeProject/openblade-hdmi-fix/pull/2))

[0.2.1]: https://github.com/OpenBladeProject/openblade-hdmi-fix/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/OpenBladeProject/openblade-hdmi-fix/releases/tag/v0.2.0

[0.2.2]: https://github.com/OpenBladeProject/openblade-hdmi-fix/compare/v0.2.1...v0.2.2

[0.2.3]: https://github.com/OpenBladeProject/openblade-hdmi-fix/compare/v0.2.2...v0.2.3

[0.2.4]: https://github.com/OpenBladeProject/openblade-hdmi-fix/compare/v0.2.3...v0.2.4
