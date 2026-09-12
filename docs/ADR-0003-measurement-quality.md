# ADR-0003 — Measurement Provenance as a Prerequisite for Diagnosis

**Status:** Accepted  
**Date:** 2026-08-23  
**Author:** forg3  

---

## Context

Running `smbdoctor-cli scan --json` on a Windows 11 machine with an idle network and **without** `--path` previously returned:

```json
{
  "exitCode": 2,
  "dominant": "SmbSigning",
  "severity": "Critical",
  "confidence": 60,
  "summary": "Network is 1.0 Gb/s, but SMB copy drops to 1 kB/s. Dominant bottleneck is SMB signing.",
  "remediation": {
    "Id": "SMB_SIGNING_DISABLE_CLIENT",
    "Commands": ["Set-SmbClientConfiguration -RequireSecuritySignature $false", "..."]
  }
}
```

No share was tested, no copy was executed, yet the tool declared a critical bottleneck, returned exit code 2, and recommended **disabling SMB signing** — a serious security downgrade.

### Root Cause

Without `--path`, `RealCopyProbe.Decide()` fell back to NIC throughput approximation:

```csharp
private static double EstimateObservedCopy(double rawNicBps)
    => rawNicBps / 8.0;
```

On an idle network, background NIC traffic yielded ~886 bytes/s. The engine interpreted this as measured copy throughput, calculated link efficiency as ~0.000007, and assigned a critical severity score to SMB signing.

**"Not measured" and "measured near zero" reached the engine as the exact same number.** The engine could not distinguish between absence of evidence and evidence of a bottleneck.

## Decision

Explicitly model measurement **provenance** in the domain:

```csharp
public enum MeasurementQuality
{
    Unavailable,   // No valid measurement — cannot support throughput-dependent conclusions
    Approximated,  // Estimated from NIC traffic above credibility floor
    Measured,      // Real test copy executed and timed
}
```

Rules enforced:

1. `ScanData.ThroughputQuality` accompanies `ObservedCopyThroughputBps`, with **safe default `Unavailable`**.
2. Any rule drawing conclusions from throughput requires `HasUsableThroughput`.
3. **Direct signals** remain valid without measurement (packet loss, link speed, dialect, encryption, antivirus). Quick scan remains informative.
4. Without measurement, the report includes a finding (`ThroughputQuality = unavailable`) explaining what was not measured and how to measure it. Confidence is lowered accordingly.
5. `RealCopyProbe.ResolveQuality()` centralizes provenance resolution. Credibility floor for approximation is **1 MB/s** (`MinCredibleApproximationBps`). Traffic below this floor is treated as idle noise.

### Related Fixes

- **Unknown dialect treated as healthy:** Previously fell into a default switch case and was reported as "modern dialect". Now reported as "unknown".
- **Multichannel added to signing score:** Previously increased the `SmbSigning` score. Now assigned to its own `Bottleneck.SmbMultichannel` with distinct remediation.
- **Legacy dialect (SMB1/2.0):** Moved to `Bottleneck.Protocol`.

## Consequences

- `scan` without `--path` no longer recommends security downgrades without evidence.
- Default exit code without `--path` changed from **2** to **0**.
- Fully measured scenarios remain unchanged: 95% confidence and complete diagnosis.
