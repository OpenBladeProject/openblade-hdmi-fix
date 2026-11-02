---
title: Preserve recording query failure status and recover saved presentation
author: Jack
date: 2026-11-02
branch: fix/recording-preflight-status
touches:
  - Directory.Build.props
  - VALIDATION.md
  - docs/recovery.md
  - docs/ru-RU/VALIDATION.md
  - docs/ru-RU/recovery.md
  - docs/ru-RU/technical.md
  - docs/technical.md
  - docs/zh-CN/VALIDATION.md
  - docs/zh-CN/recovery.md
  - docs/zh-CN/technical.md
---

## Decided
Keep live-query failure status separate from the active-target list. Only a verified active target may bypass an unrelated path failure; an unverified preflight offers Refresh or wait rather than a nonexistent selection. Preserve the saved monitor presentation after a full scan exception and pause mutation until a successful saved scan. Clear earlier preflight errors after verified recovery.

## Tried and ruled out
A case-sensitive lowercase unknown check lost valid saved names because the native reader uses capitalized Unknown. Treat unknown sentinels case-insensitively while keeping genuine live names. An empty active-target list alone cannot distinguish a healthy empty topology from a failed read.

## Watch out
Empty hints must reflect actual choices, including active-only and no-monitor cases. Do not infer physical identity from names or change patch and restore target admission. Do not resurrect the unverified recovery incompatibility or ambiguity candidates rejected in the exact-version review.
