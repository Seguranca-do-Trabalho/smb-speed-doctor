# SMB Speed Doctor

**Free, open-source (MIT / FOSS)** diagnostic tool for SMB network performance bottlenecks. Collects metrics across all layers (network, SMB, source/target disk, CPU, antivirus, workload), correlates them, and identifies the dominant bottleneck in a single sentence — complete with remediation recommendations that always include a rollback path.

**Scope:** The **diagnostics run on the Windows 10/11 client**. For Samba servers, there is a **remediation script** (`Optimize-SambaServer.ps1`), but **no Linux collector** — nothing is measured on the server side. Linux collection is on the roadmap.

**Current Release:** 1.2.0 — fixes a critical false positive that recommended security downgrades without measurement (see [`docs/ADR-0003-measurement-quality.md`](docs/ADR-0003-measurement-quality.md)), platform separation ([`docs/ADR-0002`](docs/ADR-0002-platform-separation.md)), and a functional RMM wrapper. **53/53 tests passing, build with 0 warnings.** Full history in [`CHANGELOG.md`](CHANGELOG.md).

> ⚠️ **Measure with `--path`.** Without a target share, there is no real test copy and therefore **no throughput measurement** — the diagnosis will not draw conclusions about signing, multichannel, disk, or CPU, and explicitly states this in the report.

Created by forg3  
Canonical Repository: https://github.com/Seguranca-do-Trabalho/smb-speed-doctor  
License: MIT (Free and Open-Source Software)

---

## Overview

The diagnostic engine correlates five layers and pinpoints the dominant bottleneck in a single sentence, along with a confidence level:

- **Network:** negotiated link speed, actual utilization (only counts observed traffic), packet loss, filtered virtual adapters (Hyper-V / VPN / Tailscale) to prevent false positives.
- **SMB:** negotiated dialect, mandatory signing/encryption (including the Windows 11 24H2 scenario), active multichannel.
- **Disk:** saturation on copy source and destination.
- **Load:** CPU utilization and antivirus minifilter activity.
- **Workload:** many small files vs. few large files alters recommendations (profiled robocopy).

The real test copy (`RealCopyProbe`) writes and reads a probe file on the target share and measures actual MB/s — not an estimate. If the copy fails, it falls back to NIC approximation and logs the reason in `collectionErrors`.

### Measurement Provenance

Every scan explicitly declares **where** the throughput number came from, and the engine only concludes what evidence supports:

| Provenance | Condition | What the engine concludes |
|---|---|---|
| `Measured` | Real test copy executed | Full diagnosis |
| `Approximated` | No copy, but network traffic above 1 MB/s | Full diagnosis, with lower confidence |
| `Unavailable` | No copy and idle network | **Nothing** dependent on throughput |

With `Unavailable`, direct signals remain valid (packet loss, link speed, dialect, encryption, antivirus) — the quick scan remains useful. What no longer happens is the tool concluding "SMB signing is the bottleneck" from an idle network. Details in [`docs/ADR-0003`](docs/ADR-0003-measurement-quality.md).

## Usage

### Building

Prerequisite: .NET 8 SDK (installed on this host in `~/.dotnet`).

```bash
./build.sh                        # restore + build Debug + unit tests
./build.sh publish                # win-x64 to dist/ (CLI) and dist/Gui/ (GUI)
./build.sh publish-selfcontained  # win-x64 without .NET runtime dependency
```

Standard `publish` is **framework-dependent**: the target machine requires the **.NET 8 Desktop Runtime (x64)**. For endpoints without .NET installed, use `publish-selfcontained` (~150 MB per app, no prerequisites).

> ⚠️ **Never publish over an existing `dist/` directory.** `dotnet publish` does not remove leftover files. A previous self-contained publish leaves `hostfxr.dll`/`hostpolicy.dll`/`coreclr.dll` in the folder; publishing framework-dependent over it causes the `.exe` to use that local host instead of the system runtime, failing with *"You must install or update .NET to run this application"* — **on a machine that has .NET installed**. `build.sh` automatically cleans `dist/`; if publishing manually, delete `dist/` first.

### Windows CLI (Terminal / RMM)

Copy `dist/` to the Windows machine. Binary: `SmbSpeedDoctor.Cli.exe`.

```text
scan [--json] [--quiet] [--path <share>] [--no-copy]
     [--save <file.json>] [--compare <file.json>]
fix  --export <script.ps1>
```

| Command | Description |
|---|---|
| `SmbSpeedDoctor.Cli.exe scan` | Human-readable diagnosis |
| `SmbSpeedDoctor.Cli.exe scan --json` | Full JSON output (RMM integration) |
| `SmbSpeedDoctor.Cli.exe scan --path \\server\share` | Focuses on a specific UNC share |
| `SmbSpeedDoctor.Cli.exe scan --no-copy` | Quick scan, disables real test copy |
| `SmbSpeedDoctor.Cli.exe scan --save before.json` | Saves baseline for future comparison |
| `SmbSpeedDoctor.Cli.exe scan --compare before.json` | Compares with baseline: IMPROVED / REGRESSED / STABLE per metric |
| `SmbSpeedDoctor.Cli.exe fix --export fix.ps1` | Generates PowerShell remediation script (**does NOT execute anything**) |

Exit codes contract for RMM: `0` = ok · `1` = warning/error · `2` = critical bottleneck. Unknown flags return exit 1 with usage help.

Ready-to-use RMM integration: use `scripts/smbdoctor-rmm.ps1`. It searches for the binary in both `Program Files` and in `PATH`, and accepts `-Path` to measure:

```powershell
.\smbdoctor-rmm.ps1 -Path '\\server\share'
```

Direct binary usage example:

```powershell
$out = & 'C:\tools\SmbSpeedDoctor.Cli.exe' scan --json --path '\\server\share' | ConvertFrom-Json
$out.summary      # single-sentence dominant bottleneck summary
$out.copyMethod   # profiled robocopy command + rationale + estimate
$LASTEXITCODE     # 0 ok / 1 warning / 2 critical
```

Without `--path` the scan is partial: it reports direct signals and declares in `findings` that throughput was not measured.

### GUI

Run `dist/Gui/SmbSpeedDoctor.Gui.exe`: a single window with a share path input field, a **MEASURE NOW** button, and a findings list.

**Specify the share** in the field (or click *Browse…*). Without a path, the GUI runs a partial scan and explicitly states that throughput was not measured — it never invents a verdict.

### Remediation Scripts (Always with Apply / Rollback / Status)

In `scripts/remediation/`, all scripts back up previous state and support rollback:

| Script | Purpose |
|---|---|
| `Optimize-NicTuning.ps1` | RSS, TCP autotuning, NIC power saving |
| `Enable-JumboFrames.ps1` | Tests end-to-end MTU (DF set) before applying |
| `Enable-SmbMultichannel.ps1` | Enables multichannel when feasible (multiple NICs + RSS) |
| `Set-SmbSigningOptimized.ps1` | Removes SMB signing requirement (Win11 24H2) — **GLOBAL effect**, requires `-AcceptGlobalSecurityImpact` |
| `Optimize-SambaServer.ps1` | Samba server profile (TCP_NODELAY, 128K buffers, 16K AIO) |

Standard parameters: `-Apply` applies changes (with backup), `-Rollback` reverts, `-Status` inspects current state. **All require elevated PowerShell** (Run as Administrator); `-Status` as well, due to querying system configurations.

Compatible with **Windows PowerShell 5.1** (default in Windows 10/11) and PowerShell 7. All scripts are saved with **UTF-8 BOM**.
The script generated by `fix --export` never executes on its own: run with `-WhatIf` first.

> ⚠️ **About `Set-SmbSigningOptimized.ps1`:** `Set-SmbClientConfiguration` is a **machine-wide** setting: Windows does not offer per-subnet client SMB signing policies. Applying this setting removes the requirement for **all** SMB connections from the machine — including public Wi-Fi, VPNs, and DMZs. `-Apply` requires `-AcceptGlobalSecurityImpact` to ensure deliberate acknowledgment. Do not apply on laptops that travel through untrusted networks.

### Before / After Baseline Workflow

```powershell
SmbSpeedDoctor.Cli.exe scan --save before.json --path \\server\share    # before remediation
.\scripts\remediation\Optimize-NicTuning.ps1 -Apply                    # apply fix
SmbSpeedDoctor.Cli.exe scan --compare before.json --path \\server\share # prove the gain
```

## Structure

```text
src/SmbSpeedDoctor.Core           Pure domain, net8.0 (model, engine, baseline)
src/SmbSpeedDoctor.Core.Windows   WMI/Ping collection + RealCopyProbe, net8.0-windows
src/SmbSpeedDoctor.Cli            Console CLI (--json, baselines, fix --export)
src/SmbSpeedDoctor.Gui            WinForms GUI (one button, findings list)
tests/SmbSpeedDoctor.Tests        xUnit — 53 tests
scripts/remediation/              5 Apply/Rollback/Status kits
scripts/smbdoctor-rmm.ps1         Ready-to-use RMM wrapper (MIT)
scripts/verify.ps1                Local repository verification
docs/ADR-0001-stack.md            Stack architectural decision record
docs/ADR-0002-platform-separation.md Why Core and Core.Windows are separated
docs/ADR-0003-measurement-quality.md Why diagnosis requires valid measurement
docs/AUDIT-1.1.0.md               1.1.0 audit report
docs/proposals/                   Proposals for targeted null-safety
CHANGELOG.md                      Release history
build.sh                          Build + tests + publish script
```

---

**Version:** 1.2.0 · **Author:** forg3  
**License:** MIT (FOSS)  
