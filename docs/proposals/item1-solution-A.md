# Proposal A — Targeted Null-Safety in WindowsScanner.Collect()

**Date:** 2026-08-22  
**Issue:** `smbdoctor-cli fix --export` without `--path` caused `NullReferenceException` in `WindowsScanner.Collect()`.  
**Stack:** `WindowsScanner.cs:465` — `_path.Length > 0` when `_path` is `null`.  
**Author:** forg3  
**License:** MIT (FOSS)  

---

## 1. Analysis

### Root Cause

`Program.cs` called:

```csharp
var scan = new WindowsScanner(sharePath: path, noCopy: noCopy).Collect();
// path: string? returned by ParsePath(args)
```

When `--path` is omitted, `ParsePath` returns `null`. The constructor assigned `_path = sharePath;`, storing `null` in `_path`.

Inside `Collect()`, the line:
```csharp
int fileCount = _path.Length > 0 ? workload.GetFileCount(_path) : 0;
```
evaluated `.Length` on `null`, throwing `NullReferenceException`.

## 2. Solution

Add null-safe checks at all points of use within `Collect()`:
- Use `_path ?? string.Empty` or `_path?.Length > 0`.
- Combine with constructor normalization so `_path` is never null.
