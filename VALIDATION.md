# Validation

## Software checks

The profile caps both maximum HDMI FRL fields while retaining DSC on monitors with the matching structured capabilities. Monitor brands and models are unrestricted, and the app displays an unvalidated warning before patching. High-refresh, HDR, VRR, and HDMI audio have not been validated with this app.

Local checks cover:

- Complete EDID parsing, block checksums, EDID-to-instance manufacturer/product identity, structured Forum capability lookup, DSC admission, unsupported layouts, and unchanged bytes outside the two fields and affected checksum.
- Durable backup ordering, state drift before mutation, interruption before/after a value write, restoration, foreign overrides, wrong monitor instances, invalid registry targets, and corrupt backup contents.
- WPF construction and synthetic-data rendering at normal and minimum widths, plus expanded diagnostics.
- A self-contained Windows x64 payload.

The Release solution build completed with zero warnings/errors, and 255 tests passed (111 core and 144 desktop). Regression checks cover other monitor manufacturers, exact EDID-to-instance binding, invalid manufacturer encoding, cross-model patch/restore, strict recovery for other monitors, CTA zero-offset padding, recovery of version-1 backups, the 512-active-backup boundary, private account records, and changed cached EDID data. Temporary Git fixtures verify that packaging rejects dirty source and commit changes while permitting ignored build output. A read-only scan on the development machine returned both a connected internal display and a previously connected saved record. No G93SC entry was present. Normal/minimum and expanded preview screenshots used synthetic data; the real scan applied no override.

Fixtures are synthetic and do not contain a real monitor serial number. A rendered preview does not validate cached EDID availability on a particular computer, UAC, installed registry permissions, or actual HDMI link negotiation.

## Physical validation pending

Before marking a release validated, record the exact source commit, laptop model/SKU, GPU, Windows and graphics-driver versions, monitor model/firmware, cable, port/adapter, and selected saved connection. Keep identifying raw data private.

With the monitor unplugged, inspect the actual historical entry, save its confirmed original, apply one typed patch after fresh admission, and verify the override by readback. In an attended test, reconnect and check the monitor's intended resolution and refresh rate, HDR, VRR using monitor feedback, HDMI audio, and stability during representative use. Record what was exercised. Disconnect, restore the original override, and verify restoration by readback. Confirm reboot/reconnect behavior only after the initial test succeeds.

No physical patch, UAC mutation, reconnect, or sleep/resume trial is authorized by a software build or preview. Sleep/resume testing requires fresh approval from a present user immediately beforehand.

## Connection diagnostics

Tests cover Windows and NVIDIA ABI sizes, exact display-instance binding, ambiguous paths, distinct HDR/VRR states, unknown queries, disconnected entries, independent read failures and copied-report privacy. A read-only integration scan captured the development laptop, Windows build, both GPU driver versions and the internal display's current mode/HDR. The NVIDIA query could not resolve that internal target and returned unknown VRR fields. No mutation was attempted.

User testing with 0.2.5 found that the patch applies and the laptop no longer hard crashes, but the G95 picture still blanks roughly every five seconds at both 120 Hz and 240 Hz, during use and idle. Further user testing found that enabling Adaptive Sync on the monitor stabilized the image and allowed benchmarks at 240 Hz. That result does not establish the underlying cause. The stability investigation is tracked in [issue #6](https://github.com/OpenBladeProject/openblade-hdmi-fix/issues/6). The diagnostic additions help collect evidence; they do not resolve that blanking.
Review follow-ups cover unresolved active-path errors, recognized connector types including DisplayPort USB tunnels, and recoverable failure isolation for both optional readers. Fault injection verifies that one failed reader preserves the other reader's data and that a failed refresh cannot reuse a former live snapshot. Fatal memory and native-corruption errors are not masked as ordinary missing data.

## Connection recording checks

Recorder tests exercise a disconnected baseline becoming the exact connected instance, same-model isolation, failed and ambiguous reads reporting unknown connection state, durable blackout/device/display markers, optional evidence failures, prompt cancellation with a stuck reader, and isolation from a previous recording's outstanding read. Event tests cover bounded server-side queries, safe metadata parsing, inaccessible logs, truncation and fixed display-log arguments.

A read-only WPF integration check exercised Start, Mark blackout, Stop and save, and close during recording. Patch, Restore, Refresh and monitor selection were disabled during capture and restored afterward. Both stop and close produced partial ZIPs containing only the four allowed report files; the private Windows display log was captured separately. Patch and Restore were never called. Normal-width, minimum-width recording, and expanded diagnostic previews were inspected.

No affected monitor was plugged in for these checks. Windows logs and display snapshots cannot confirm HDMI FRL/DSC negotiation or a physical blackout; that remains part of the user's attended recording.

Claude reviewed the recording feature at `05bbdf1`. Follow-up regressions preserve blackout capacity during notification bursts, keep terminal records and accepted marks when timeline limits are reached, maintain timestamp order for marks during reads, report the saving phase, preserve user-entered punctuation, and retain the ZIP when the private log folder cannot be created. Recorder setup now runs off the WPF dispatcher. Read-only GUI checks also exercised automatic finalization with a shortened test duration and delayed optional log capture; marking and stopping were disabled during saving.

## SCDC recording checks

Native transport tests verify the public NVAPI interface IDs and 64-byte x64 I2C structure, the three permitted register reads, FRL rate decoding, version and return-data validation, target changes, missing interfaces and failed reads. Synthetic delegates exercise the native signatures without issuing physical I2C transactions. Recorder regressions cover selection-only queries, skipped ambiguous/non-HDMI targets, retained mode/HDR/VRR evidence, sanitized SCDC JSON and active DSC remaining unknown.

The Release build completed with zero warnings/errors. A read-only WPF test exercised recording and saving with a synthetic selected instance; it could not match a real HDMI target and issued no SCDC reads. A successful SCDC read on the G95 remains unverified. SCDC configuration and lock flags are observations and do not confirm active DSC or a stable negotiated link.

Claude Opus reviewed the SCDC change at `107ab64`. Follow-ups separate the two-second read budget from the 500 ms cadence, refresh the target-mapping output before rechecking it, accept optional speed/port normalization, distinguish unavailable targets from ambiguous targets, and keep summary/status wording honest when no SCDC observation was made. Synthetic slow-read and normalized-output regressions exercise those paths. Actual driver normalization on the G95 has not been observed. Review claims about helper duplication, payload size and hypothetical direct x86 callers were not treated as current defects. Claude reported limited inspection of removed behavior under its read-only tool restrictions; the exact diff was also checked by Codex.
