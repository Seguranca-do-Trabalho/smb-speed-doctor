# ADR-0002 — Separation of Pure Domain from Windows-Dependent Collection

**Status:** Accepted  
**Date:** 2026-08-23  
**Author:** forg3  
**Supersedes:** None. Complements ADR-0001 (stack).  

---

## Context

The initial codebase was organized into three projects: `Core`, `Cli` (both targeting `net8.0`), and `Gui` (`net8.0-windows`). `Core` contained the domain model, correlation engine, baseline storage, **and** WMI collection (`System.Management`).

`net8.0` is a cross-platform target framework. However, WMI only exists on Windows. This resulted in **42 CA1416 warnings** ("... is only supported on: 'windows'") on every build.

This setup had two issues:
1. **The promised portability was illusory.** Marking `Core` as `net8.0` told consumers that the library could run anywhere. Invoking `WindowsScanner.Collect()` on Linux threw a `PlatformNotSupportedException` at runtime on the first WMI call.
2. 42 persistent warnings caused warning fatigue, masking legitimate compiler warnings (such as 10 nullability warnings).

## Decision

Split into two distinct projects along the platform boundary:

| Project | Target Framework | Contents |
|---|---|---|
| `SmbSpeedDoctor.Core` | `net8.0` | `Domain`, `DiagnosisEngine`, `BaselineStore`, `Collectors` (interfaces + mocks). Free of WMI, P/Invoke, and OS-specific APIs. |
| `SmbSpeedDoctor.Core.Windows` | `net8.0-windows` | `WindowsScanner` (WMI), `RealCopyProbe`. References `Core`. |

`Cli` targets `net8.0-windows` (depends on Windows collection; published for `win-x64`). `Gui` targets `net8.0-windows` and references `Core.Windows`. `EnableWindowsTargeting=true` enables cross-compilation from Linux.

## Alternatives Considered

| Alternative | Reason for Rejection |
|---|---|
| `[SupportedOSPlatform("windows")]` on `WindowsScanner` | Silences warnings but keeps OS-specific code inside the "pure" library. Does not create a clean path for Linux collectors. |
| Changing `Core` TFM to `net8.0-windows` | Tags pure domain logic as Windows-only, preventing domain tests from running cross-platform. |
| Keeping as-is | Persistent warnings mask new issues and perpetuate false portability claims. |

## Consequences

**Positive:**
- Build warnings reduced from 52 to **0**.
- Platform boundary is enforced by compiler: `Core` will not compile if platform-specific APIs are introduced.
- Pure domain is isolated and easily testable anywhere.
- Future `SmbSpeedDoctor.Core.Linux` collector becomes an additive extension rather than a refactoring.

**Trade-offs:**
- Test project targets `net8.0-windows` because it exercises Windows collection alongside pure domain tests.
