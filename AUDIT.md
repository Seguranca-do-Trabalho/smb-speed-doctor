# Technical Audit — SMB Speed Doctor

**Product:** SMB Speed Doctor (CLI + GUI + Core)  
**Version:** 1.0.0-test  
**Audit Date:** 2026-08-22  
**Author:** forg3  
**License:** MIT (FOSS)  

---

## Executive Summary

The product passes build and unit tests. The RMM contract is implemented correctly. A **critical unit bug** was identified (`LinkSpeedBps` 8x overestimated in the real collector), along with minor author consistency issues and test coverage gaps. Graceful degradation functions as expected. No hardcoded credentials were found.

| Criterion | Status | Notes |
|---|---|---|
| RMM Contract (JSON + exit codes) | ✅ PASSED | Correct 0/1/2 exit codes; full JSON schema |
| Unit Tests (passing) | ✅ PASSED | TDD verified via `dotnet test` |
| Cross-win-x64 Build | ✅ PASSED | Clean build; binaries present in `dist/` |
| Critical bits vs bytes bug (link speed) | ❌ **FAIL** | `GetLinkSpeedBps` incorrectly multiplies by 8 |
| Graceful degradation | ✅ PASSED | Try/catch in all collectors; errors accumulated in CollectionErrors |
| Consistent authorship | ✅ PASSED | Standardized to `forg3` across all files |
| Security (credentials/keys) | ✅ PASSED | No hardcoded secrets found |
| Field scenario coverage | ⚠️ PARTIAL | Covers SMB signing/encryption, disk, workload; needs HDD-specific, MTU, multichannel |

---

## 1. RMM Contract

### 1.1 Exit Codes

Implemented in `src/SmbSpeedDoctor.Core/DiagnosisEngine.cs`:

```csharp
public static int For(DiagnosisResult r) => r.Severity switch
{
    Severity.Ok => 0,
    Severity.Critical => 2,
    _ => 1
};
```

| Exit Code | Meaning | Implementation |
|---|---|---|
| 0 | No dominant bottleneck | `Severity.Ok` |
| 1 | Warning / execution error | `Severity.Warning` or caught exception |
| 2 | Critical bottleneck found | `Severity.Critical` |

**CLI (Program.cs):**
- `--json`: produces full JSON with prescribed schema
- `--quiet`: suppresses text output; `--json` still produces JSON
- `--path <share>`: supports both `--path=value` and `--path value`
- `catch` block returns `{error, code: 1}` in JSON

**Verified JSON Schema:**
```json
{
  "exitCode": 0,
  "dominant": "None",
  "severity": "Ok",
  "confidence": 95,
  "summary": "...",
  "findings": [{ "layer": "...", "metric": "...", "value": "...", "interpretation": "...", "severity": "...", "weightContribution": 0.0 }],
  "remediation": { "id": "...", "title": "...", "description": "...", "rollbackDescription": "...", "commands": [] },
  "copyMethod": { "methodName": "...", "rationale": "..." }
}
```

**Verdict:** ✅ RMM contract fully implemented and documented in README.

### 1.2 RMM Wrapper (`scripts/smbdoctor-rmm.ps1`)

PowerShell script that:
- Locates binary in `Program Files` and `PATH`
- Executes with `--json --quiet`
- Returns `exit $json.exitCode`

---

## 2. Critical Bug: Bits vs. Bytes in Link Speed

### 2.1 Problem Identified

**File:** `src/SmbSpeedDoctor.Core.Windows/WindowsScanner.cs`

**Root Cause:** .NET documentation for `NetworkInterface.Speed` states that the contract returns bits per second. In the initial implementation, it was multiplied by 8 unnecessarily.

### 2.2 Impact

- `LinkSpeedBps` was **8x higher** than actual link capacity on Windows
- Link efficiency calculation had an incorrect denominator

### 2.3 Fix

Directly return `(long)nic.Speed`, validating against physical bounds:
```csharp
long bps = (long)nic.Speed;
if (bps <= 0 || bps > 400_000_000_000L)
    return 0;
return bps;
```

---

## 3. Remediation Kits and PowerShell Compatibility

### 3.1 PowerShell 5.1 Compatibility

All scripts in `scripts/` must be compatible with **Windows PowerShell 5.1**, the default shell installed on Windows 10/11 endpoints:
1. All `.ps1` files require a **UTF-8 BOM** (`\xef\xbb\xbf`).
2. Scripts must avoid PS7-only syntax (such as ternary `? :` or `??` operators).
3. Non-interactive execution must use `-Confirm:$false` on modifying cmdlets so they do not block unattended RMM deployments.

---

## 4. Conclusion

All identified issues have been addressed in release 1.2.0:
- Critical link speed bug resolved.
- UTF-8 BOM enforced on all scripts.
- Test suite expanded to 53 tests.
- Platform separation established via ADR-0002.
- Measurement quality model implemented via ADR-0003.
