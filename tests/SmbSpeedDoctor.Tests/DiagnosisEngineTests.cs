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
        FileCount: 10);

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
}
