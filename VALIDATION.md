# Validation

## Software checks

The profile caps the G93SC's two maximum HDMI FRL fields while retaining DSC. High-refresh, HDR, VRR, and HDMI audio have not been validated with this app.

Local checks cover:

- Complete EDID parsing, block checksums, exact SAM7463 identity, structured Forum capability lookup, DSC admission, unsupported layouts, and unchanged bytes outside the two fields and affected checksum.
- Durable backup ordering, state drift before mutation, interruption before/after a value write, restoration, foreign overrides, wrong monitor instances, invalid registry targets, and corrupt backup contents.
- WPF construction and synthetic-data rendering at normal and minimum widths, plus expanded diagnostics.
- A self-contained Windows x64 payload.

The Release solution build completed with zero warnings/errors, and 39 focused tests passed. A read-only scan on the development machine returned both a connected internal display and a previously connected saved record. No G93SC entry was present. Normal/minimum and expanded preview screenshots used synthetic data; the real scan applied no override.

Fixtures are synthetic and do not contain a real monitor serial number. A rendered preview does not validate cached EDID availability on a particular computer, UAC, installed registry permissions, or actual HDMI link negotiation.

## Physical validation pending

Before marking a release validated, record the exact source commit, laptop model/SKU, GPU, Windows and graphics-driver versions, G93SC firmware, cable, port/adapter, and selected saved connection. Keep identifying raw data private.

With the monitor unplugged, inspect the actual historical entry, save its confirmed original, apply one typed patch after fresh admission, and verify the override by readback. In an attended test, reconnect and check 5120×1440 at the intended refresh rate, HDR, VRR using monitor feedback, HDMI audio, and stability during representative use. Record what was exercised. Disconnect, restore the original override, and verify restoration by readback. Confirm reboot/reconnect behavior only after the initial test succeeds.

No physical patch, UAC mutation, reconnect, or sleep/resume trial is authorized by a software build or preview. Sleep/resume testing requires fresh approval from a present user immediately beforehand.
