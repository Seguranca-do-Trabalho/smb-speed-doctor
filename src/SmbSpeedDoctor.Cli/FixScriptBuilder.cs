// Created by forg3
// License: MIT
//
// Builds PowerShell remediation script content from diagnosis.
// The application NEVER executes the script: it only writes it to disk.
// Every action requires human review, administrator elevation, and manual execution.

using System.Text;
using SmbSpeedDoctor.Core;

namespace SmbSpeedDoctor.Cli;

/// <summary>Generates .ps1 script with remediation commands + rollback block.</summary>
public static class FixScriptBuilder
{
    public static string Build(DiagnosisResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# =============================================================");
        sb.AppendLine("# SMB Speed Doctor — Automatically generated remediation script");
        sb.AppendLine("# Created by forg3");
        sb.AppendLine("# License: MIT — WITHOUT WARRANTY. Review each command before running.");
        sb.AppendLine("# =============================================================");
        sb.AppendLine($"# Generated at: {DateTime.UtcNow:O} (UTC)");
        sb.AppendLine($"# Diagnosis: dominant={result.Dominant} severity={result.Severity} confidence={result.ConfidencePct}%");
        sb.AppendLine($"# Summary: {result.OneLineSummary ?? "(no summary)"}");
        sb.AppendLine();

        if (result.RecommendedRemediation is not { } rem)
        {
            sb.AppendLine("# No remediation recommended for this diagnosis.");
            sb.AppendLine("# The scan did not identify a bottleneck with an applicable fix.");
            return sb.ToString();
        }

        sb.AppendLine($"# Remediation: {rem.Title}");
        sb.AppendLine($"# Description: {rem.Description}");
        sb.AppendLine();
        sb.AppendLine("#Requires -RunAsAdministrator");
        sb.AppendLine("[CmdletBinding()]");
        sb.AppendLine("param([switch]$WhatIf)");
        sb.AppendLine();
        sb.AppendLine("if (-not $WhatIf) {");
        sb.AppendLine("    Write-Host 'Run again with -WhatIf to simulate.' -ForegroundColor Yellow;");
        sb.AppendLine("    return;");
        sb.AppendLine("}");
        sb.AppendLine("Write-Host '=== SIMULATION MODE (no changes will be made) ===' -ForegroundColor Cyan;");
        sb.AppendLine();
        sb.AppendLine("# ---- REMEDIATION ACTIONS ----");
        foreach (var cmd in rem.Commands)
            sb.AppendLine($"{Esc(cmd)} # -WhatIf applies here");
        sb.AppendLine();
        sb.AppendLine("# ---- ROLLBACK (revert to previous state) ----");
        sb.AppendLine($"# {rem.RollbackDescription}");
        sb.AppendLine("# Uncomment the lines below only if you need to rollback:");
        foreach (var line in RollbackHints(rem.Id))
            sb.AppendLine($"# {line}");
        return sb.ToString();
    }

    /// <summary>
    /// Rollback commands per remediation family. Mirror application
    /// commands; nothing here is executed by the app.
    /// </summary>
    private static IEnumerable<string> RollbackHints(string remediationId) => remediationId switch
    {
        "SMB_SIGNING" => new[]
        {
            "Set-SmbClientConfiguration -RequireSecuritySignature $true -Confirm:$false",
            "Set-SmbClientConfiguration -EnableSecuritySignature $true -Confirm:$false",
        },
        "SMB_ENCRYPTION" => new[]
        {
            "Set-SmbClientConfiguration -EncryptData $false -Confirm:$false",
        },
        "MULTICHANNEL" => new[]
        {
            "Set-SmbClientConfiguration -EnableMultiChannel $false -Confirm:$false",
        },
        "NETWORK_OPTIMIZE" => new[]
        {
            "netsh int ipv4 set subinterface '<interface>' mtu=1500 store=persistent",
        },
        "DISK_TARGET" or "DISK_SOURCE" => new[]
        {
            "# Rollback: no configuration change — the bottleneck is physical.",
            "# Consider moving the workload to another disk/SSD.",
        },
        _ => new[]
        {
            "# No automated rollback known for this remediation.",
        },
    };

    private static string Esc(string s) => s.Replace("\"", "`\"");
}
