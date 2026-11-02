---
title: Clear preflight errors when saved data waits become ready
author: Jack
date: 2026-11-02
branch: fix/recording-preflight-status
touches:
  - VALIDATION.md
  - docs/ru-RU/VALIDATION.md
  - docs/zh-CN/VALIDATION.md
  - src/OpenBlade.HdmiFix.Desktop/MainWindow.xaml.cs
  - src/OpenBlade.HdmiFix.Desktop/RecordingTargets.cs
  - tests/OpenBlade.HdmiFix.Desktop.Tests/RecordingPreflightWindowTests.cs
---

## Decided
Clear a stale saved-scan error when the saved scan recovers and the currently chosen connection is a valid saved-data wait, even if an unrelated live-query failure remains. Say ready to wait for readable data rather than claiming an active physical connection or a refresh that did not occur.

## Tried and ruled out
Checking only an empty live-failure string or an active selection kept old saved-scan errors visible after partial recovery. The saved-scan success flag and the shared exact saved-wait predicate provide the necessary readiness evidence.

## Watch out
The tracked hidden-WPF regression exercises this full UI sequence with controlled scan completions, not source-text assertions. It owns one STA Application/Dispatcher and never invokes native recording or mutations. Keep saved-instance admission distinct from unavailable live data and do not promise waiting after a full saved-scan failure.
