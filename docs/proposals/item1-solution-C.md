# Proposal C — Explicit Contract with Guard Clauses

**Date:** 2026-08-22  
**Author:** forg3  
**License:** MIT (FOSS)  
**Status:** Rejected with rationale.

---

## Core Idea

Enforce a strict contract using guard clauses:
- Throw `ArgumentNullException` in `WindowsScanner` if `sharePath` is null.
- Require caller to normalize or provide a non-null string.

## Rejection Rationale

The tool strictly requires **graceful degradation**: scanning without `--path` is a common and legitimate CLI/RMM use case (quick scan for direct signals like packet loss and dialect). Throwing an exception on null `sharePath` violates graceful degradation.

Boundary normalization (Proposal B) achieves contract safety without throwing exceptions, preserving usability.
