---
title: Record the chosen active HDMI connection and preserve capture warnings
author: Jack
date: 2026-10-31
branch: fix/recording-monitor-match
touches:
  - Directory.Build.props
  - docs/recovery.md
  - docs/ru-RU/recovery.md
  - docs/ru-RU/technical.md
  - docs/technical.md
  - docs/zh-CN/recovery.md
  - docs/zh-CN/technical.md
  - src/OpenBlade.HdmiFix.Desktop/ConnectionRecording.cs
  - src/OpenBlade.HdmiFix.Desktop/MainWindow.xaml
  - src/OpenBlade.HdmiFix.Desktop/MainWindow.xaml.cs
---

## Decided
Keep recording selection separate from the saved connection used for Patch and Restore. Offer only uniquely bound active HDMI instances, refresh the snapshot before starting, and require explicit choice when the saved instance is not active. Preserve exact full Windows instance equality for recording, SCDC, and mutation admission.

## Tried and ruled out
The supplied older blackout ZIP showed every live display unselected and SCDC NotRequested. Source inspection found that saved registry and SetupAPI readers already use full case-insensitive DISPLAY instance IDs. A model-name fallback or speculative ID rewrite would not establish physical identity and could capture the wrong monitor.

## Watch out
A failure for another active display must not invalidate a verified unique selected path. Count coverage only after durable sample append; recovered summaries use the sanitized saved prefix. Warn during missing or unverified capture and retain final warnings for gaps or no captured samples. WPF choice records need a safe display label and ToString fallback so custom ComboBox templates cannot show raw record fields. Synthetic checks do not establish G95C link stability.
