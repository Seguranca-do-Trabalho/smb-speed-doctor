using Xunit;
using SmbSpeedDoctor.Core;
using SmbSpeedDoctor.Cli;

namespace SmbSpeedDoctor.Tests;

// Criado por André Santo (forg3) | junkyardgoodies.app

public class FixScriptBuilderTests
{
    private static DiagnosisResult ResultWithRemediation(string id, string rollbackDesc, string[] commands)
        => new(
            OneLineSummary: "teste",
            Dominant: Bottleneck.SmbSigning,
            Severity: Severity.Warning,
            ConfidencePct: 70,
            Findings: Array.Empty<Finding>(),
            RecommendedRemediation: new Remediation(
                Id: id,
                Title: "Título teste",
                Description: "Descrição teste",
                RollbackDescription: rollbackDesc,
                Commands: commands),
            RecommendedMethod: new CopyMethodProfile("robocopy /J /ZB", "rationale"));

    [Fact]
    public void Script_contem_autoria_licenca_e_aviso()
    {
        var result = ResultWithRemediation("SMB_SIGNING", "reverter assinatura",
            new[] { "Set-SmbClientConfiguration -RequireSecuritySignature $false" });

        string s = FixScriptBuilder.Build(result);

        Assert.Contains("André Santo (forg3) | junkyardgoodies.app", s);
        Assert.Contains("MIT", s);
        Assert.Contains("#Requires -RunAsAdministrator", s);
        Assert.Contains("-WhatIf", s);          // simulação obrigatória
        Assert.Contains("SIMULAÇÃO", s);
    }

    [Fact]
    public void Script_embute_comandos_de_aplicacao_e_rollback()
    {
        var result = ResultWithRemediation("SMB_SIGNING", "reverter assinatura ao padrão anterior",
            new[] { "cmd-aplicar-1", "cmd-aplicar-2" });

        string s = FixScriptBuilder.Build(result);

        Assert.Contains("cmd-aplicar-1", s);
        Assert.Contains("cmd-aplicar-2", s);
        Assert.Contains("ROLLBACK", s);
        Assert.Contains("reverter assinatura ao padrão anterior", s);
        // Comando de reversão da família SMB_SIGNING presente comentado
        Assert.Contains("# Set-SmbClientConfiguration -RequireSecuritySignature $true", s);
    }

    [Fact]
    public void Sem_remediacao_o_script_diz_que_nao_ha_acao()
    {
        var result = new DiagnosisResult(
            "ok", Bottleneck.None, Severity.Ok, 95,
            Array.Empty<Finding>(), null,
            new CopyMethodProfile("robocopy /J /ZB", "r"));

        string s = FixScriptBuilder.Build(result);

        Assert.Contains("Nenhuma remediação recomendada", s);
    }

    [Fact]
    public void Rollback_hints_por_familia()
    {
        var enc = ResultWithRemediation("SMB_ENCRYPTION", "r", new[] { "x" });
        Assert.Contains("# Set-SmbClientConfiguration -EncryptData $false", FixScriptBuilder.Build(enc));

        var mc = ResultWithRemediation("MULTICHANNEL", "r", new[] { "x" });
        Assert.Contains("# Set-SmbClientConfiguration -EnableMultiChannel $false", FixScriptBuilder.Build(mc));
    }
}
