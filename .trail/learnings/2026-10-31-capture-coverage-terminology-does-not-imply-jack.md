---
title: Capture coverage terminology does not imply identity fallback
author: Jack
date: 2026-10-31
branch: fix/recording-monitor-match
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
Recording status messages and regression tests may describe absent or unverified capture. Monitor model fields are optional user context; recording targets are still chosen by exact Windows instance identity. The prior learning's diagnosis text describes evidence, not a forbidden vocabulary.

## Tried and ruled out
No additional alternatives were adopted.

## Watch out
The lexical learning checker flagged documentation, full-ID source comments and failure-case tests as though they reintroduced model-name matching. Manual review found those conflicts false: active choices preserve exact identity, mutation targeting is unchanged, and tests deliberately reproduce missing capture. Keep the interpretation in the PR description rather than weakening admission or hiding failure cases.
