# CHANGELOG — SMB Speed Doctor

Format: Keep a Changelog.
Author: forg3.
License: MIT (FOSS).

## [1.2.0] — 2026-08-23

Correction cycle based on audits with **real application execution**. The four primary issues below passed through the previous test suite without detection because they used mocks rather than exercising the real binary.

### Fixed — Security

- **Critical false positive recommending security downgrade.** `scan --json` without `--path` does not execute a test copy; throughput came from the idle NIC (~886 B/s) and the engine concluded `dominant: SmbSigning`, `severity: Critical`, exit code 2, recommending `Set-SmbClientConfiguration -RequireSecuritySignature $false`. No share had been tested.
  Cause: "not measured" and "measured near zero" reached the engine as the exact same number.
  Fix: `MeasurementQuality` (`Measured`/`Approximated`/`Unavailable`) in `ScanData`, with safe default `Unavailable`; all throughput-derived rules now require valid measurement. See [`docs/ADR-0003-measurement-quality.md`](docs/ADR-0003-measurement-quality.md).
- **`Set-SmbSigningOptimized.ps1 -Subnet` promised non-existent scoping.** The parameter was only printed to the console — `Set-SmbClientConfiguration` is machine-wide and Windows does not offer client SMB signing policies per subnet. In practice, signing was disabled for **all** connections, including untrusted networks. Parameter removed; `-Apply` now requires `-AcceptGlobalSecurityImpact`; documentation rewritten.
- **Plaintext credential in repository.** `docs/AUDIT-1.1.0.md` cited the password of test user `hermes-smb`. Removed from text and report retracted.

### Fixed — Remediation Kit Compatibility with Standard Windows

Discovered when running scripts **elevated under Windows PowerShell 5.1** (default shell on Windows 10/11 and used by RMMs).

- **Scripts failed to compile in PowerShell 5.1.** Files were UTF-8 **without BOM**; 5.1 assumes ANSI, turning accents into mojibake and corrupting string terminators. All `.ps1` files are now saved as **UTF-8 with BOM**, which works across both PS 5.1 and PS 7+.
- **`Optimize-NicTuning.ps1` used ternary operator `? :`**, exclusive to PowerShell 7 — syntax error in 5.1 even with BOM. Rewritten with standard `if/else`.
- **`Optimize-NicTuning.ps1 -Status` threw exception** ("call method on null-valued expression"): searched for English string `'Receive Window Auto-Tuning'` in `netsh` output, which is localized. On non-English Windows, `$autotune` was `$null` and `.Trim()` failed. Switched to `Get-NetTCPSetting` (language-independent), with tolerant fallbacks and `N/A` fallback.
- **`Enable-SmbMultichannel.ps1 -Status` printed empty count** ("Only   active NIC detected"): with a single NIC return was scalar and `.Count` was empty in 5.1. Fixed with `@()` array wrapping.
- **`-Apply` and `-Rollback` hung indefinitely in non-interactive sessions.** `Set-SmbClientConfiguration` prompts for confirmation by default; scripts did not pass `-Confirm:$false`. In non-interactive RMM execution, scripts hung on invisible prompts — including during `-Rollback`. Fixed across all 4 invocations.

### Fixed — Functionality

- **RMM wrapper execution.** `scripts/smbdoctor-rmm.ps1` previously used `@rem` (batch syntax) causing immediate parse errors in PowerShell. Rewritten with `#`, binary search in both `Program Files` and `PATH`, `-Path` parameter, non-JSON output handling, and proper exit code propagation.
- **Unknown dialect reported as healthy** (fail-open): `"unknown"` previously fell into `_ =>` switch arm and became *"modern dialect"*. Now reported as "undetermined".
- **Multichannel findings added to SMB signing score:** the report listed `Multichannel` but blamed `SmbSigning`, recommending disabling signing for an unrelated issue. Added `Bottleneck.SmbMultichannel` with its own remediation.
- **Legacy dialect (SMB1/2.0) attributed to `SmbSigning`:** moved to `Bottleneck.Protocol`, with dedicated protocol remediation.
- **Spurious error in report without `--path`:** `GetAverageFileSize(null)` was called without a guard, throwing internally and logging `"workload: Value cannot be null"` in `CollectionErrors`. Symmetrical guard applied.

### Added

- **Path input field in GUI.** `MainForm` now supports specifying a target share. Without a path, it explicitly reports that throughput was not measured.
- **Displayed measured throughput in GUI.** Added dedicated label highlighting measured MB/s and measurement provenance.
- **JSON serialization of measurement and notes.** `measuredThroughputMBps`, `throughputQuality`, and `collectionNotes` added to JSON schema.
- **Target extracted from UNC share.** When `targetServer` is not explicitly passed to `WindowsScanner`, it is extracted from `sharePath` rather than defaulting to `loopback`.
- **Local link validation.** Warns when target is routed over VPN/WAN rather than on a directly connected subnet.
- **Comparative baselines.** `BaselineStore` supports saving and comparing scan results with `IMPROVED`, `REGRESSED`, or `STABLE` verdicts.

### Changed — Architecture

- **Platform separation (ADR-0002):** Split monolithic `Core` into portable domain `SmbSpeedDoctor.Core` (`net8.0`) and Windows-specific `SmbSpeedDoctor.Core.Windows` (`net8.0-windows`).
- **Build warnings reduced to ZERO:** Eliminated all 52 build warnings.
- **Automated test suite expanded to 53 tests.**

---

## [1.1.0] — 2026-08-22

### Added
- `--save` and `--compare` flags in CLI for baseline comparisons.
- `fix --export` command generating automated PowerShell remediation script.
- `RobocopyBuilder` for profiled robocopy transfer commands.
- `RealCopyProbe` for measuring actual disk write/read throughput on target shares.

### Fixed
- Fixed NRE when running `fix --export` without `--path`.
- Fixed CLI `--help` text to document all options.

---

## [1.0.0] — 2026-08-22

Initial release of SMB Speed Doctor.
- Core diagnostic engine across network, SMB, disk, and CPU layers.
- Windows collectors using WMI and network statistics.
- Basic WinForms GUI and console CLI with JSON output for RMM integration.
- Initial set of PowerShell remediation scripts.
