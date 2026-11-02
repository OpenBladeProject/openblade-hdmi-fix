---
title: Keep saved recording waits independent of unrelated live query failures
author: Jack
date: 2026-11-02
branch: fix/recording-preflight-status
touches:
  - VALIDATION.md
  - docs/ru-RU/VALIDATION.md
  - docs/zh-CN/VALIDATION.md
  - src/OpenBlade.HdmiFix.Desktop/MainWindow.xaml.cs
  - src/OpenBlade.HdmiFix.Desktop/RecordingTargets.cs
  - tests/OpenBlade.HdmiFix.Desktop.Tests/RecordingTargetTests.cs
---

## Decided
A saved recording target may wait for its exact instance despite unrelated live-query failures when the saved scan succeeded. Do not silently downgrade a requested active target into that wait mode. Offer verified listed choices during partial-query failure, preserve completed-operation status if a later scan fails, and use selection-neutral status text when no refresh occurred.

## Tried and ruled out
The first 0.3.4 preflight gate blocked every inactive target on any live-query error. Claude identified the lost saved-wait path; synthetic UI admission probes confirmed it. Keep the full saved-scan failure gate and mutation pause, but distinguish that failure from an incomplete live query.

## Watch out
A full saved-scan exception must not promise recording is available. Failed live-query status may coexist with verified active choices or an explicitly chosen saved wait. Exact identity and the recorder's existing bounded native-read ownership guard remain required. Duplication and hypothetical future hash divergence were not treated as current failures.
