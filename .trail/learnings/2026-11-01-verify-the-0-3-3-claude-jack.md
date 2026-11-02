---
title: Verify the 0.3.3 Claude review against both versions and real producers
author: Jack
date: 2026-11-01
---

## Decided
Verify external review candidates against the exact base and head and the production producer contracts. The completed Claude 0.3.3 review yields confirmed issues in the unknown-name fallback, failure-specific recording preflight feedback, and empty-selection hints. Actual WPF probes reproduce those cases without native recording or mutation.

## Tried and ruled out
The claimed new recovery incompatibility is unsupported: both versions explicitly serialize unknown selectedConnected as null with JsonIgnore Never, and the parser requirement predates 0.3.3. The claimed sanitized ambiguity failure relies on candidate errors that the real successful instance resolver does not produce. Identical duplicate hashing and a proposed typed ambiguity refactor do not establish current defects.

## Watch out
Failed active topology reads must not be described as a verified absence or followed by an instruction to select a connected option that is not listed. Preserve fail-closed target admission while improving error guidance. RunAsync catches ordinary nonfatal operational failures, and an accurate zero-sample coverage warning does not blame the monitor for a disk error. Claude's limited diff inspection and unverified candidates must not be reported as confirmed regressions.
