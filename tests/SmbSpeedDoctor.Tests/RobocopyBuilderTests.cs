using Xunit;
using SmbSpeedDoctor.Core;
using SmbSpeedDoctor.Cli;

namespace SmbSpeedDoctor.Tests;

// Criado por André Santo (forg3) | junkyardgoodies.app
// Item 9 — testes do gerador robocopy profiled.

public class RobocopyBuilderTests
{
    private static ScanData Scan(
        int fileCount = 10,
        double avgBytes = 512L * 1024 * 1024,
        double loss = 0.0,
        double latency = 5,
        bool signing = true,
        long observed = 38_000_000)
        => new(
            LatencyMs: latency,
            RawThroughputBps: 2_400_000_000,
            MtuBytes: 1500,
            PacketLossRatio: loss,
            LinkSpeedBps: 2_500_000_000,
            NegotiatedDialect: "3.1.1",
            SigningEnabled: signing,
            EncryptionEnabled: false,
            Multichannel: false,
            ActiveChannels: 1,
            CpuUtilization: 0.15,
            SourceDiskBusyRatio: 0.2,
            TargetDiskBusyRatio: 0.25,
            SourceDiskReadBps: 2_000_000_000,
            TargetDiskWriteBps: 2_000_000_000,
            AvFilterOnSharePath: false,
            ObservedCopyThroughputBps: observed,
            AverageFileBytes: avgBytes,
            FileCount: fileCount);

    private static DiagnosisResult Diag(ScanData d)
        => new("s", Bottleneck.SmbSigning, Severity.Warning, 60, Array.Empty<Finding>(), null,
               new CopyMethodProfile("x", "y"));

    [Fact]
    public void Arquivos_grandes_poucos_usa_J_sem_MT()
    {
        var plan = RobocopyBuilder.Build(Scan(fileCount: 10), Diag(null!));

        Assert.Contains("/J", plan.MethodName);
        Assert.DoesNotContain("/MT:", plan.MethodName);
        Assert.Contains("unbuffered", plan.Rationale);
    }

    [Fact]
    public void Muitos_arquivos_pequenos_usa_MT_paralelo()
    {
        var plan = RobocopyBuilder.Build(
            Scan(fileCount: 50_000, avgBytes: 48 * 1024), Diag(null!));

        Assert.Contains("/MT:", plan.MethodName);
        Assert.Contains("paraleliza seeks", plan.Rationale);
        Assert.True(plan.EstimatedThroughputMBps > 0); // estimativa populada
    }

    [Fact]
    public void Link_instavel_adiciona_ZB()
    {
        var loss = RobocopyBuilder.Build(Scan(loss: 0.02), Diag(null!));
        Assert.Contains("/ZB", loss.MethodName);
        Assert.Contains("/ZB retomável", loss.Rationale);

        var lat = RobocopyBuilder.Build(Scan(latency: 80), Diag(null!));
        Assert.Contains("/ZB", lat.MethodName);
    }

    [Fact]
    public void Signing_e_criptografia_aparecem_no_rationale()
    {
        var plan = RobocopyBuilder.Build(Scan(signing: true), Diag(null!));
        Assert.Contains("assinatura SMB ativa", plan.Rationale);
    }

    [Fact]
    public void Estimativa_respeita_o_teto_da_copia_observada()
    {
        // Cópia observada de ~38 MB/s deve limitar a estimativa mesmo com link de 2,5G
        var plan = RobocopyBuilder.Build(Scan(observed: 38_000_000), Diag(null!));
        Assert.InRange(plan.EstimatedThroughputMBps, 30.0, 40.0);
    }
}
