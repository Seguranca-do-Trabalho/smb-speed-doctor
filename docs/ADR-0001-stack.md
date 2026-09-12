# ADR-0001 — Product Stack and Architecture

**Status:** Accepted  
**Date:** 2026-08-22  
**Author:** forg3  

## Context

- Architectural decision for SMB Speed Doctor: .NET vs Rust/Tauri.
- Dual executable requirement: GUI and CLI built from shared logic.
- Product format is a signed executable, not a raw script.
- Core diagnostic logic must be testable without Windows (Linux CI/development).

## Decision

1. **Stack**: C# / .NET 8, pure class library (`Core`) + two executables (`Cli`, `Gui`), targeting `win-x64`.
2. **Rationale**: Native access to WMI, Win32 API, SMB cmdlets, and WinForms without extra runtime dependencies; cross-compilation from Linux.
3. **Consequences**:
   - Diagnostic engine isolated in `Core` (interfaces + domain models) → testable on Linux CI.
   - CLI (`SmbSpeedDoctor.Cli.exe`) provides `--json` and exit codes for RMM integration.
   - GUI (`SmbSpeedDoctor.Gui.exe`) provides a single-window interface displaying findings and speed metrics.
   - MIT-licensed PowerShell wrapper for RMM integration.
   - Standard build via `dotnet publish -r win-x64`.

## Rejected Alternatives

- **Rust/Tauri**: Smaller binary size, but sacrifices seamless WMI/Win32 integration and Windows management ecosystems. Critical performance requirements do not warrant this trade-off.
- **Pure PowerShell Script**: Cannot be distributed as a standalone binary in enterprise app catalogs, lacks rich UI, and does not scale as an independent tool.
