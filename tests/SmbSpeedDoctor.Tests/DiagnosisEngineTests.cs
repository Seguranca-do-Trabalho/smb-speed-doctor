// Criado por André Santo (forg3) | junkyardgoodies.app
using Xunit;
using SmbSpeedDoctor.Core;

namespace SmbSpeedDoctor.Tests;

/// <summary>
/// Testes do motor de correlação. Cada cenário reproduz um perfil real de
/// campo: assinatura SMB no 24H2, disco lento, MTU fragmentado, carga de
/// muitos arquivos pequenos etc.
/// </summary>
public class DiagnosisEngineTests
{
    private static ScanData Baseline => new(
        LatencyMs: 0.4,
        RawThroughputBps: 2_400_000_000,   // 2,5 GbE ~ saturado
        MtuBytes: 1500,
        PacketLossRatio: 0.0,
        LinkSpeedBps: 2_500_000_000,
        NegotiatedDialect: "3.1.1",
        SigningEnabled: true,
        EncryptionEnabled: false,
        Multichannel: false,
        ActiveChannels: 1,
        CpuUtilization: 0.15,
        SourceDiskBusyRatio: 0.20,
        TargetDiskBusyRatio: 0.25,
        SourceDiskReadBps: 2_000_000_000,
        TargetDiskWriteBps: 2_000_000_000,
        AvFilterOnSharePath: false,
        ObservedCopyThroughputBps: 38_000_000, // ~38 MB/s (BYTES/s) — cópia SMB colapsa no 24H2 com signing
        AverageFileBytes: 512L * 1024 * 1024,
        FileCount: 10,
        // Estes cenários descrevem cópias REALMENTE medidas; sem isso o motor
        // (corretamente) se recusa a concluir qualquer coisa sobre throughput.
        ThroughputQuality: MeasurementQuality.Measured);

    [Fact]
    public void Perfil_saudavel_nao_aponta_gargalo()
    {
        var healthy = Baseline with { ObservedCopyThroughputBps = 290_000_000 };
        var result = new DiagnosisEngine().Diagnose(healthy);

        Assert.Equal(Bottleneck.None, result.Dominant);
        Assert.Equal(Severity.Ok, result.Severity);
        Assert.Contains("sem gargalo dominante", result.OneLineSummary);
        Assert.Equal(0, ExitCodeMapper.For(result));
    }

    [Fact]
    public void Assinatura_SMB_com_rede_saturada_e_gargalo_dominante()
    {
        // Cenário 24H2: rede entrega ~2 Gb/s mas a cópia SMB cai para 38 MB/s.
        var result = new DiagnosisEngine().Diagnose(Baseline);

        Assert.Equal(Bottleneck.SmbSigning, result.Dominant);
        Assert.Equal(Severity.Critical, result.Severity);
        Assert.Equal(2, ExitCodeMapper.For(result)); // exit code de gargalo p/ RMM
        Assert.Contains("assinatura", result.OneLineSummary.ToLowerInvariant());
        Assert.NotNull(result.RecommendedRemediation);
        // Toda remediação carrega rollback explícito (decisão do produto).
        Assert.False(string.IsNullOrWhiteSpace(result.RecommendedRemediation!.RollbackDescription));
    }

    [Fact]
    public void Criptografia_SMB_suplanta_assinatura_quando_ativa()
    {
        var data = Baseline with { EncryptionEnabled = true };
        var result = new DiagnosisEngine().Diagnose(data);

        Assert.Equal(Bottleneck.SmbEncryption, result.Dominant);
    }

    [Fact]
    public void Disco_destino_lento_supera_causa_SMB()
    {
        var data = Baseline with
        {
            TargetDiskWriteBps = 90_000_000,          // ~90 MB/s (HDD)
            TargetDiskBusyRatio = 0.98,
            ObservedCopyThroughputBps = 85_000_000,
        };
        var result = new DiagnosisEngine().Diagnose(data);

        Assert.Equal(Bottleneck.DiskTarget, result.Dominant);
    }

    [Fact]
    public void Muitos_arquivos_pequenos_explica_throughput_baixo()
    {
        var data = Baseline with
        {
            AverageFileBytes = 48 * 1024,
            FileCount = 50_000,
            ObservedCopyThroughputBps = 60_000_000,
        };
        var result = new DiagnosisEngine().Diagnose(data);

        Assert.Equal(Bottleneck.Workload, result.Dominant);
        Assert.Contains("arquivos pequenos", result.OneLineSummary);
    }

    [Fact]
    public void Perda_de_pacote_e_reportada_como_gargalo_de_rede()
    {
        var data = Baseline with { PacketLossRatio = 0.03, ObservedCopyThroughputBps = 120_000_000 };
        var result = new DiagnosisEngine().Diagnose(data);

        Assert.Equal(Bottleneck.Network, result.Dominant);
    }

    [Fact]
    public void Dialeto_smb1_e_bloqueante()
    {
        var data = Baseline with { NegotiatedDialect = "1.0" };
        var result = new DiagnosisEngine().Diagnose(data);

        var f = Assert.Single(result.Findings, x => x.Metric == "NegotiatedDialect");
        Assert.Equal(Severity.Critical, f.Severity);
    }

    [Fact]
    public void Metodo_de_copia_para_arquivos_grandes_e_nao_buffered()
    {
        var result = new DiagnosisEngine().Diagnose(Baseline);
        Assert.Contains("/J", result.RecommendedMethod.Rationale);
    }

    [Fact]
    public void Metodo_de_copia_para_muitos_arquivos_pequenos_e_paralelo()
    {
        var data = Baseline with
        {
            AverageFileBytes = 32 * 1024,
            FileCount = 80_000,
            ObservedCopyThroughputBps = 40_000_000,
        };
        var result = new DiagnosisEngine().Diagnose(data);
        Assert.Equal("robocopy paralelo (/MT)", result.RecommendedMethod.MethodName);
    }

    // ---------------------------------------------------------------------
    // Regressão de campo: `scan` SEM --path não executa cópia de teste. O
    // throughput vinha de NIC ociosa (~886 B/s) e o motor concluía "assinatura
    // SMB crítica" (exit 2), recomendando DESLIGAR a assinatura — um downgrade
    // de segurança apoiado em nenhuma medição. Reproduzido na máquina real.
    // ---------------------------------------------------------------------

    private static ScanData SemMedicao => Baseline with
    {
        ObservedCopyThroughputBps = 886,   // valor real observado no bug
        ThroughputQuality = MeasurementQuality.Unavailable,
    };

    [Fact]
    public void Sem_medicao_real_nao_acusa_assinatura_como_gargalo()
    {
        var result = new DiagnosisEngine().Diagnose(SemMedicao);

        Assert.NotEqual(Bottleneck.SmbSigning, result.Dominant);
        Assert.NotEqual(Severity.Critical, result.Severity);
        Assert.NotEqual(2, ExitCodeMapper.For(result));
    }

    [Fact]
    public void Sem_medicao_real_nao_recomenda_desabilitar_assinatura()
    {
        var result = new DiagnosisEngine().Diagnose(SemMedicao);

        // Nenhuma recomendação de downgrade de segurança sem evidência.
        Assert.Null(result.RecommendedRemediation);
    }

    [Fact]
    public void Sem_medicao_real_declara_a_limitacao_e_baixa_a_confianca()
    {
        var result = new DiagnosisEngine().Diagnose(SemMedicao);

        Assert.Contains(result.Findings, f => f.Metric == "ThroughputQuality");
        // Ausência de evidência não é evidência de ausência: não pode alegar 95%.
        Assert.True(result.ConfidencePct < 95,
            $"confiança deveria cair sem medição, veio {result.ConfidencePct}");
    }

    [Fact]
    public void Sem_medicao_real_ainda_reporta_achados_independentes_de_throughput()
    {
        // Perda de pacote é medida direta — não depende de cópia. O scan rápido
        // continua útil; só não conclui o que depende de throughput.
        var data = SemMedicao with { PacketLossRatio = 0.03 };
        var result = new DiagnosisEngine().Diagnose(data);

        Assert.Equal(Bottleneck.Network, result.Dominant);
    }

    [Fact]
    public void Dialeto_desconhecido_nao_e_interpretado_como_moderno()
    {
        // Fail-open: dado ausente virava parecer positivo ("dialeto moderno").
        var data = Baseline with { NegotiatedDialect = "desconhecido" };
        var result = new DiagnosisEngine().Diagnose(data);

        var f = Assert.Single(result.Findings, x => x.Metric == "NegotiatedDialect");
        Assert.DoesNotContain("moderno", f.Interpretation);
    }

    [Fact]
    public void Multichannel_desabilitado_nao_e_contabilizado_como_assinatura()
    {
        // O achado de Multichannel somava no score de SmbSigning: o relatório
        // listava um problema e culpava outro.
        var data = Baseline with
        {
            SigningEnabled = false,        // isola o efeito do multichannel
            Multichannel = false,
            ActiveChannels = 1,
            ObservedCopyThroughputBps = 38_000_000,
        };
        var result = new DiagnosisEngine().Diagnose(data);

        Assert.Equal(Bottleneck.SmbMultichannel, result.Dominant);
        Assert.NotEqual(Bottleneck.SmbSigning, result.Dominant);
    }
}
