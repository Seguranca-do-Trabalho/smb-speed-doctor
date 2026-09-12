# Proposal B — Boundary Normalization in Constructor

**Date:** 2026-08-22  
**Author:** forg3  
**License:** MIT (FOSS)  
**Status:** Applied in combination with Proposal A.

---

## Core Idea

Normalize parameters at the boundary in the `WindowsScanner` constructor:

```csharp
_path = sharePath ?? string.Empty;
var isExplicit = !string.IsNullOrWhiteSpace(targetServer) && targetServer != "loopback";
_target = isExplicit
    ? targetServer!
    : (ExtractServerFromUnc(_path) ?? "loopback");
```

With boundary normalization, internal references to `_path` and `_target` are guaranteed non-null.

## Outcome

Combined with targeted null-safety in `Collect()` (belt and suspenders), ensuring robust defense against null inputs.
