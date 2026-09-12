// Created by forg3
// License: MIT
using Xunit;
using SmbSpeedDoctor.Core;
using SmbSpeedDoctor.Cli;

namespace SmbSpeedDoctor.Tests;

public class FixScriptBuilderTests
{
    private static DiagnosisResult ResultWithRemediation(string id, string rollbackDesc, string[] commands)
        => new(
            OneLineSummary: "test",
            Dominant: Bottleneck.SmbSigning,
            Severity: Severity.Warning,
            ConfidencePct: 70,
            Findings: Array.Empty<Finding>(),
            RecommendedRemediation: new Remediation(
                Id: id,
                Title: "Test Title",
                Description: "Test Description",
                RollbackDescription: rollbackDesc,
                Commands: commands),
            RecommendedMethod: new CopyMethodProfile("robocopy /J /ZB", "rationale"));

    [Fact]
    public void Script_contains_author_license_and_warning()
    {
        var result = ResultWithRemediation("SMB_SIGNING", "rollback signing",
            new[] { "Set-SmbClientConfiguration -RequireSecuritySignature $false" });

        string s = FixScriptBuilder.Build(result);

        Assert.Contains("forg3", s);
        Assert.Contains("MIT", s);
        Assert.Contains("#Requires -RunAsAdministrator", s);
        Assert.Contains("-WhatIf", s);          // simulation required
        Assert.Contains("SIMULATION", s);
    }

    [Fact]
    public void Script_embeds_application_and_rollback_commands()
    {
        var result = ResultWithRemediation("SMB_SIGNING", "revert signing to previous standard",
            new[] { "cmd-apply-1", "cmd-apply-2" });

        string s = FixScriptBuilder.Build(result);

        Assert.Contains("cmd-apply-1", s);
        Assert.Contains("cmd-apply-2", s);
        Assert.Contains("ROLLBACK", s);
        Assert.Contains("revert signing to previous standard", s);
        // SMB_SIGNING family rollback command present commented out
        Assert.Contains("# Set-SmbClientConfiguration -RequireSecuritySignature $true", s);
    }

    [Fact]
    public void Without_remediation_script_states_no_action()
    {
        var result = new DiagnosisResult(
            "ok", Bottleneck.None, Severity.Ok, 95,
            Array.Empty<Finding>(), null,
            new CopyMethodProfile("robocopy /J /ZB", "r"));

        string s = FixScriptBuilder.Build(result);

        Assert.Contains("No remediation recommended", s);
    }

    [Fact]
    public void Rollback_hints_by_family()
    {
        var enc = ResultWithRemediation("SMB_ENCRYPTION", "r", new[] { "x" });
        Assert.Contains("# Set-SmbClientConfiguration -EncryptData $false", FixScriptBuilder.Build(enc));

        var mc = ResultWithRemediation("MULTICHANNEL", "r", new[] { "x" });
        Assert.Contains("# Set-SmbClientConfiguration -EnableMultiChannel $false", FixScriptBuilder.Build(mc));
    }
}
