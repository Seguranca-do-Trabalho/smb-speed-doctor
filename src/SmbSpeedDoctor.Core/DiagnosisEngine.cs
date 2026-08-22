// Criado por André Santo (forg3) | junkyardgoodies.app
using SmbSpeedDoctor.Core;

namespace SmbSpeedDoctor.Core;

/// <summary>
/// Motor de correlação: cruza as coleções de todas as camadas e decide qual é
/// o gargalo dominante. Regras derivadas da experiência de campo (24H2/25H2,
/// SMB signing, HDDs locais, workload de arquivos pequenos).
/// </summary>
public sealed class DiagnosisEngine
{
    private const double DiskThroughputHddMbs = 90;   // ~90 MB/s
    private const double DiskThroughputSsdMbs = 500;  // SSD típico
    private const long MinFileForSmallWorkloadBytes = 64 * 1024;
    private const long SmallWorkloadFileCount = 10_000;

    public DiagnosisResult Diagnose(ScanData d)
    {
        var findings = new List<Finding>();
        var scores = new Dictionary<Bottleneck, double>();

        // --- Disco local (verificado PRIMEIRO para priorização) ---
        double targetLimitBps = TargetDiskLimit(d);
        double sourceLimitBps = SourceDiskLimit(d);
        bool diskIssue = false;
        bool workloadIssue = false;

        if (d.TargetDiskBusyRatio >= 0.9 && d.ObservedCopyThroughputBps < targetLimitBps * 0.95)
        {
            findings.Add(new Finding("DiskTarget", "BusyRatio", $"{d.TargetDiskBusyRatio:P0}",
                $"disco destino saturado (limite estimado {targetLimitBps / 1_000_000:F0} MB/s)",
                Severity.Critical, 8.0));
            scores[Bottleneck.DiskTarget] = 8.0;
            diskIssue = true;
        }
        if (d.SourceDiskBusyRatio >= 0.9 && d.ObservedCopyThroughputBps < sourceLimitBps * 0.95)
        {
            findings.Add(new Finding("DiskSource", "BusyRatio", $"{d.SourceDiskBusyRatio:P0}",
                $"disco origem saturado", Severity.Critical, 6.0));
            scores[Bottleneck.DiskSource] = 6.0;
            diskIssue = true;
        }

        // --- Carga ---
        if (d.AverageFileBytes < MinFileForSmallWorkloadBytes && d.FileCount >= SmallWorkloadFileCount)
        {
            double expectedThroughput = EstimateSmallFileThroughput(d);
            if (d.ObservedCopyThroughputBps > expectedThroughput * 0.8)
            {
                findings.Add(new Finding("Workload", "AvgFileSize", $"{BytesToString((long)d.AverageFileBytes)}",
                    $"carga de {d.FileCount} arquivos pequenos — throughput dentro da expectativa",
                    Severity.Warning, 0.0));
                scores[Bottleneck.Workload] = 5.0;
                workloadIssue = true;
            }
        }

        // --- Rede (perda de pacote sempre supera SMB quando crítica) ---
        if (d.PacketLossRatio >= 0.01)
        {
            double penalty = d.PacketLossRatio >= 0.03 ? 12.0 : 6.0;
            findings.Add(new Finding("Network", "PacketLossRatio",
                $"{d.PacketLossRatio:P0}", "Perda de pacotes degradando TCP/SMB",
                d.PacketLossRatio >= 0.03 ? Severity.Critical : Severity.Warning,
                penalty));
            scores[Bottleneck.Network] = Math.Max(scores.GetValueOrDefault(Bottleneck.Network), penalty);
        }
        // Enlace negociado baixo (<= 100 Mbit) em rede moderna: cabo Cat5/Cat5e velho,
        // porta switch 10/100 ou negociação duplex ruim. O link É o gargalo.
        else if (d.LinkSpeedBps > 0 && d.LinkSpeedBps <= 100_000_000)
        {
            findings.Add(new Finding("Network", "NegotiatedLinkSpeed",
                FmtLink(d.LinkSpeedBps),
                "enlace negociado muito abaixo do padrão moderno — verifique cabo (Cat6+) e porta do switch",
                Severity.Warning, 8.0));
            scores[Bottleneck.Network] = Math.Max(scores.GetValueOrDefault(Bottleneck.Network), 8.0);
        }
        // LinkUtilization: só pontua com medição de link crível (>= 10 Mbit/s e <= 400 Gbit/s)
        // E com tráfego real observado (rede ociosa não é gargalo).
        // Links virtuais reportam 100+ Gb/s nominais; sem NIC física confiável, sem peso.
        bool linkCredible = d.LinkSpeedBps >= 10_000_000 && d.LinkSpeedBps <= 400_000_000_000;
        bool hasTraffic = d.RawThroughputBps > 1_000_000 || d.ObservedCopyThroughputBps > 1_000_000;
        if (!linkCredible && d.LinkSpeedBps > 0)
        {
            findings.Add(new Finding("Network", "LinkSpeed",
                FmtLink(d.LinkSpeedBps),
                "velocidade de link fora da faixa confiável — provável adaptador virtual",
                Severity.Warning, 0));
        }
        if (linkCredible && hasTraffic && d.RawThroughputBps / d.LinkSpeedBps < 0.3
            && d.ObservedCopyThroughputBps * 8.0 < d.LinkSpeedBps * 0.15)
        {
            findings.Add(new Finding("Network", "LinkUtilization",
                $"{d.ObservedCopyThroughputBps / Math.Max(1, d.LinkSpeedBps):P1}",
                $"throughput observado muito abaixo da capacidade link",
                Severity.Warning, 3.0));
            scores[Bottleneck.Network] = Math.Max(scores.GetValueOrDefault(Bottleneck.Network), 3.0);
        }

        // --- SMB (só considera se não há disco/workload comprometendo) ---
        bool signed = d.SigningEnabled;
        bool encrypted = d.EncryptionEnabled;
        string dialect = d.NegotiatedDialect;

        findings.Add(new Finding("SMB", "NegotiatedDialect", dialect,
            dialect switch
            {
                "1.0" or "2.0" => "dialeto legado, alto overhead",
                "2.1" => "dialeto legado, overhead moderado",
                _ => "dialeto moderno"
            },
            dialect.StartsWith("1.") || dialect.StartsWith("2.0") ? Severity.Critical
                : dialect.StartsWith("2.1") ? Severity.Warning
                : Severity.Ok,
            dialect.StartsWith("1.") ? 10.0 : dialect.StartsWith("2.0") ? 6.0 : 0));

        if (dialect.StartsWith("1.") || dialect.StartsWith("2.0"))
            scores[Bottleneck.SmbSigning] = 10.0;

        if (encrypted)
        {
            findings.Add(new Finding("SMB", "EncryptionEnabled", "true",
                "criptografia SMB ativa — custo de CPU em cada I/O", Severity.Critical, 9.0));
            scores[Bottleneck.SmbEncryption] = 9.0;
        }
        else if (signed && !diskIssue && !workloadIssue)
        {
            // Só avalia assinatura se não houver disco ou workload comprometendo
            double efficiency = d.LinkSpeedBps > 0
                ? (d.ObservedCopyThroughputBps * 8.0) / d.LinkSpeedBps
                : 1.0;
            // Só aponta SMB como gargalo se eficiência for baixa (<50%)
            double smbScore = efficiency < 0.15 ? 9.0 : efficiency < 0.5 ? 6.0 : 0.0;
            // Score zero não entra no dicionário: evita dominância indevida em cenários saudáveis.
            if (smbScore > 0)
            {
                findings.Add(new Finding("SMB", "SigningEnabled", "true",
                    "assinatura SMB ativa — overhead de hash em cada pacote",
                    smbScore >= 7.0 ? Severity.Critical : smbScore >= 4.0 ? Severity.Warning : Severity.Ok,
                    smbScore));
                scores[Bottleneck.SmbSigning] = Math.Max(scores.GetValueOrDefault(Bottleneck.SmbSigning), smbScore);
            }
        }

        if (!d.Multichannel && d.LinkSpeedBps >= 1_000_000_000 && d.ActiveChannels == 1)
        {
            // Só penaliza multichannel se a eficiência geral já for baixa
            double efficiency = d.LinkSpeedBps > 0
                ? (d.ObservedCopyThroughputBps * 8.0) / d.LinkSpeedBps
                : 1.0;
            if (efficiency < 0.5 && !diskIssue && !workloadIssue)
            {
                findings.Add(new Finding("SMB", "Multichannel", "disabled",
                    "multichannel desabilitado em link >= 1 Gb/s", Severity.Warning, 3.0));
                scores[Bottleneck.SmbSigning] = Math.Max(scores.GetValueOrDefault(Bottleneck.SmbSigning),
                    (scores.TryGetValue(Bottleneck.SmbSigning, out var v) ? v : 0) + 3.0);
            }
        }

        // --- CPU ---
        if (d.CpuUtilization > 0.85 && d.ObservedCopyThroughputBps * 8.0 < d.LinkSpeedBps * 0.1)
        {
            findings.Add(new Finding("CPU", "Utilization", $"{d.CpuUtilization:P0}",
                "CPU alto durante cópia SMB", Severity.Warning, 4.0));
            scores[Bottleneck.Cpu] = 4.0;
        }

        // Antivírus
        if (d.AvFilterOnSharePath)
        {
            findings.Add(new Finding("Antivirus", "FilterOnSharePath", "true",
                "filtro de antivírus no caminho do compartilhamento SMB", Severity.Warning, 5.0));
            scores[Bottleneck.Antivirus] = 5.0;
        }

        // --- Decisão ---
        if (scores.Count == 0)
            return HealthyResult(findings);

        var dominant = scores.OrderByDescending(kv => kv.Value).First().Key;
        double confidence = Math.Clamp(scores.Values.Max() / (scores.Values.Max() + 2.0) * 100, 40, 99);
        Severity severity = dominant switch
        {
            Bottleneck.SmbSigning or Bottleneck.SmbEncryption or Bottleneck.Network
                or Bottleneck.DiskTarget or Bottleneck.DiskSource => Severity.Critical,
            _ => Severity.Warning
        };

        var remediation = dominant switch
        {
            Bottleneck.SmbSigning => Remediations.Signing(),
            Bottleneck.SmbEncryption => Remediations.Encryption(),
            Bottleneck.DiskTarget => Remediations.Disk(d.TargetDiskBusyRatio, d.TargetDiskWriteBps),
            Bottleneck.DiskSource => Remediations.Disk(d.SourceDiskBusyRatio, d.SourceDiskReadBps),
            Bottleneck.Network => Remediations.Network(d.PacketLossRatio, d.MtuBytes),
            Bottleneck.Workload => Remediations.Workload(),
            _ => null
        };

        return new DiagnosisResult(
            OneLineSummary: BuildSummary(dominant, d),
            Dominant: dominant,
            Severity: severity,
            ConfidencePct: confidence,
            Findings: findings,
            RecommendedRemediation: remediation,
            RecommendedMethod: RecommendCopyMethod(d));
    }

    #region Helpers

    private static DiagnosisResult HealthyResult(IReadOnlyList<Finding> findings)
        => new(
            "Scan concluído: sem gargalo dominante identificado — throughput dentro da capacidade esperada.",
            Bottleneck.None,
            Severity.Ok,
            95.0,
            findings,
            null,
            RecommendCopyMethod(new ScanData(0,0,0,0,0,"",false,false,false,0,0,0,0,0,0,false,0,0,0)));

    private static double TargetDiskLimit(ScanData d)
        => d.TargetDiskWriteBps > 0
            ? d.TargetDiskWriteBps
            : d.TargetDiskBusyRatio > 0.5
                ? DiskThroughputHddMbs * 1_000_000 * 0.8
                : DiskThroughputSsdMbs * 1_000_000;

    private static double SourceDiskLimit(ScanData d)
        => d.SourceDiskReadBps > 0
            ? d.SourceDiskReadBps
            : d.SourceDiskBusyRatio > 0.5
                ? DiskThroughputHddMbs * 1_000_000 * 0.8
                : DiskThroughputSsdMbs * 1_000_000;

    private static double EstimateSmallFileThroughput(ScanData d)
    {
        // Aproximação: overhead por arquivo ~500 µs (TCP+disk seek+SMB negotiation)
        double overheadPerFile = 500e-6;
        double effectiveBytePerSec = (1.0 / overheadPerFile) * d.AverageFileBytes * 0.5;
        return Math.Min(effectiveBytePerSec, DiskThroughputHddMbs * 1_000_000);
    }

    private static CopyMethodProfile RecommendCopyMethod(ScanData d)
    {
        bool largeFiles = d.AverageFileBytes >= MinFileForSmallWorkloadBytes;
        int count = d.FileCount;
        if (largeFiles || count < 100)
            return new CopyMethodProfile("robocopy /J /ZB",
                $"cópias não-buffered (robocopy /J /ZB) para {count} arquivo(s) de tamanho médio/grande");
        if (count >= SmallWorkloadFileCount)
            return new CopyMethodProfile("robocopy paralelo (/MT)",
                $"carga de {count} arquivos pequenos — paralelismo maximiza seek");
        return new CopyMethodProfile("robocopy /ZB /MT:8",
            "mistura buffered/unbuffered com 8 threads para workload misto");
    }

    private static string BuildSummary(Bottleneck dominant, ScanData d)
        => dominant switch
        {
            Bottleneck.SmbSigning =>
                $"Rede é {FmtLink(d.LinkSpeedBps)}, mas cópia SMB cai para {FmtThroughput(d.ObservedCopyThroughputBps)}. " +
                $"O gargalo dominante é a assinatura SMB.",
            Bottleneck.SmbEncryption =>
                $"Rede é {FmtLink(d.LinkSpeedBps)}, mas cópia SMB cai para {FmtThroughput(d.ObservedCopyThroughputBps)}. " +
                $"O gargalo dominante é a criptografia SMB ativa.",
            Bottleneck.Network =>
                d.PacketLossRatio >= 0.01
                    ? $"Perda de pacote ({d.PacketLossRatio:P1}) em {FmtLink(d.LinkSpeedBps)} impactando TCP/SMB."
                    : $"Capacidade de {FmtLink(d.LinkSpeedBps)} subutilizada durante a cópia observada.",
            Bottleneck.DiskTarget =>
                $"Disco destino saturado limitando cópia para {FmtThroughput(d.TargetDiskWriteBps)}.",
            Bottleneck.DiskSource =>
                $"Disco origem saturado limitando leitura em {FmtThroughput(d.SourceDiskReadBps)}.",
            Bottleneck.Workload =>
                $"Throughput baixo esperado: {d.FileCount} arquivos pequenos de {BytesToString((long)d.AverageFileBytes)}.",
            _ => "Gargalo indeterminado."
        };

    private static string FmtThroughput(double bps)
        => bps >= 1_000_000
            ? $"{bps / 1_000_000:F0} MB/s"
            : $"{bps / 1_000:F0} kB/s";

    private static string FmtLink(double bps)
        => bps >= 1_000_000_000
            ? $"{bps / 1_000_000_000:F1} Gb/s"
            : $"{bps / 1_000_000:F0} Mb/s";

    private static string BytesToString(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / (1024 * 1024)} MiB"
            : bytes >= 1024
                ? $"{bytes / 1024} KiB"
                : $"{bytes} B";

    #endregion
}

/// <summary>
/// Mapper de exit-code para integração com RMM (0 = ok, 2 = gargalo, 1 = erro).
/// </summary>
public static class ExitCodeMapper
{
    public static int For(DiagnosisResult r) => r.Severity switch
    {
        Severity.Ok => 0,
        Severity.Critical => 2,
        _ => 1
    };
}

public static class Remediations
{
    public static Remediation Signing() => new(
        Id: "SMB_SIGNING_DISABLE_CLIENT",
        Title: "Desabilitar assinatura SMB no cliente",
        Description: "Remove o overhead de hash HMAC-SHA-256 em cada pacote SMB. Recomenda-se em redes confiáveis (segmento dedicado, VLAN isolada).",
        RollbackDescription: "Reativa a assinatura com a política padrão do domínio.",
        Commands:
        [
            "Set-SmbClientConfiguration -RequireSecuritySignature $false",
            "Restart-Service lanmanworkstation"
        ]);

    public static Remediation Encryption() => new(
        Id: "SMB_ENCRYPTION_DISABLE_CLIENT",
        Title: "Desabilitar criptografia obrigatória SMB no cliente",
        Description: "Criptografia SMB tem custo adicional de CPU por pacote. Em redes confiáveis, a assinatura já protege integridade.",
        RollbackDescription: "Reativa a exigência de criptografia via política.",
        Commands:
        [
            "Set-SmbClientConfiguration -RequireEncryption $false",
            "Restart-Service lanmanworkstation"
        ]);

    public static Remediation Disk(double busyRatio, double bps) => new(
        Id: "DISK_CONTENTION",
        Title: "Alívio de contenção em disco",
        Description: $"Disco com {busyRatio:P0} de uso e throughput medido de {Fmt(bps)}. Verificar: fragmentação, RAID rebuild, other IO consumer.",
        RollbackDescription: "Nenhuma alteração de configuração aplicada — apenas recomendações de planejamento.",
        Commands: []);

    public static Remediation Network(double loss, int mtu) => new(
        Id: "NETWORK_OPTIMIZE",
        Title: "Otimização de rede",
        Description: $"Perda de {loss:P1} detectada. Testar MTU {mtu} vs 9000 (jumbo frames) em segmento dedicado.",
        RollbackDescription: "Sem alteração de configuração de rede.",
        Commands:
        [
            "netsh interface ipv4 show subinterfaces",
            "ping -f -l 1472 <gateway>"
        ]);

    public static Remediation Workload() => new(
        Id: "COPY_METHOD_TUNE",
        Title: "Ajustar método de cópia para a carga",
        Description: "Carga de arquivos pequenos deve usar robocopy com paralelismo.",
        RollbackDescription: "Nenhuma alteração de configuração.",
        Commands: ["robocopy <origem> <destino> /MT:16 /ZB /J"]);

    private static string Fmt(double bps)
        => bps >= 1_000_000 ? $"{bps / 1_000_000:F0} MB/s" : $"{bps / 1_000:F0} kB/s";
}
