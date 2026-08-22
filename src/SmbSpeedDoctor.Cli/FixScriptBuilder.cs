// Criado por André Santo (forg3) | junkyardgoodies.app
// Licença: MIT
//
// Item 1 — monta o conteúdo do script PowerShell de correção a partir do
// diagnóstico. O app NUNCA executa o script: apenas o escreve em disco.
// Toda ação exige revisão humana, elevação de admin e rodada manual.

using System.Text;
using SmbSpeedDoctor.Core;

namespace SmbSpeedDoctor.Cli;

/// <summary>Gera script .ps1 com comandos de remediação + bloco de rollback.</summary>
public static class FixScriptBuilder
{
    public static string Build(DiagnosisResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# =============================================================");
        sb.AppendLine("# SMB Speed Doctor — Script de correção gerado automaticamente");
        sb.AppendLine("# Criado por André Santo (forg3) | junkyardgoodies.app");
        sb.AppendLine("# Licença: MIT — SEM GARANTIA. Revise cada comando antes de rodar.");
        sb.AppendLine("# =============================================================");
        sb.AppendLine($"# Gerado em: {DateTime.UtcNow:O} (UTC)");
        sb.AppendLine($"# Diagnóstico: dominant={result.Dominant} severity={result.Severity} confiança={result.ConfidencePct}%");
        sb.AppendLine($"# Resumo: {result.OneLineSummary ?? "(sem resumo)"}");
        sb.AppendLine();

        if (result.RecommendedRemediation is not { } rem)
        {
            sb.AppendLine("# Nenhuma remediação recomendada para este diagnóstico.");
            sb.AppendLine("# O scan não identificou gargalo com correção aplicável.");
            return sb.ToString();
        }

        sb.AppendLine($"# Remediação: {rem.Title}");
        sb.AppendLine($"# Descrição : {rem.Description}");
        sb.AppendLine();
        sb.AppendLine("#Requires -RunAsAdministrator");
        sb.AppendLine("[CmdletBinding()]");
        sb.AppendLine("param([switch]$WhatIf)");
        sb.AppendLine();
        sb.AppendLine("if (-not $WhatIf) {");
        sb.AppendLine("    Write-Host 'Rode novamente com -WhatIf para simular.' -ForegroundColor Yellow;");
        sb.AppendLine("    return;");
        sb.AppendLine("}");
        sb.AppendLine("Write-Host '=== MODO SIMULAÇÃO (nenhuma alteração real) ===' -ForegroundColor Cyan;");
        sb.AppendLine();
        sb.AppendLine("# ---- AÇÕES DE CORREÇÃO ----");
        foreach (var cmd in rem.Commands)
            sb.AppendLine($"{Esc(cmd)} # -WhatIf aplica here");
        sb.AppendLine();
        sb.AppendLine("# ---- ROLLBACK (reverter ao estado anterior) ----");
        sb.AppendLine($"# {rem.RollbackDescription}");
        sb.AppendLine("# Descomente as linhas abaixo apenas se precisar reverter:");
        foreach (var line in RollbackHints(rem.Id))
            sb.AppendLine($"# {line}");
        return sb.ToString();
    }

    /// <summary>
    /// Comandos de reversão por família de remediação. Espelham os comandos de
    /// aplicação; nada aqui é executado pelo app.
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
            "# Reversão: não há alteração de configuração — o gargalo é físico.",
            "# Avalie mover a carga para outro disco/SSD.",
        },
        _ => new[]
        {
            "# Sem reversão automatizada conhecida para esta remediação.",
        },
    };

    private static string Esc(string s) => s.Replace("\"", "`\"");
}
