---
title: Make the WPF preflight regression independent of desktop composition
author: Jack
date: 2026-11-02
branch: fix/recording-preflight-status
touches:
  - VALIDATION.md
  - tests/OpenBlade.HdmiFix.Desktop.Tests/RecordingPreflightWindowTests.cs
---

## Decided
Run the preflight GUI regression against initialized WPF controls and the real Loaded routed event without showing a window. Keep separately rendered normal and minimum-width checks for visual evidence. The regression still uses actual Start/Refresh handlers and controlled asynchronous scan completions.

## Tried and ruled out
A hidden shown window passed locally but the hosted Windows runner timed out before completing its Loaded-dependent startup. Increasing the timeout or skipping hosted verification would not resolve that rendering dependency.

## Watch out
Own one STA Application and Dispatcher, explicitly shut both down, and retain phase-specific bounded timeout diagnostics. Do not create native monitor queries or mutation operations in the fake backend. A CI control-state test does not replace actual rendered visual checks or physical HDMI validation.
