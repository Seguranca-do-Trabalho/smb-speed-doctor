// Criado por André Santo (forg3) | junkyardgoodies.app
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmbSpeedDoctor.Core;

/// <summary>Veredito comparativo entre dois scans.</summary>
public enum ComparisonVerdict
{
    Melhorou,
    Piorou,
    Estavel,
}

/// <summary>
/// Arquivo de baseline gravado por <c>scan --save</c>: metadados + ScanData completo.
/// </summary>
public sealed record BaselineFile(
    string AppVersion,
    DateTime SavedAtUtc,
    ScanData Scan);

/// <summary>Uma linha da tabela antes→depois do <c>scan --compare</c>.</summary>
public sealed record MetricComparison(
    string Metric,
    object? Before,
    object? After,
    double? Delta,
    ComparisonVerdict Verdict)
{
    public string VerdictLabel => Verdict switch
    {
        ComparisonVerdict.Melhorou => "MELHOROU",
        ComparisonVerdict.Piorou => "PIOROU",
        _ => "ESTÁVEL",
    };
}

/// <summary>
/// Persistência e comparação de baselines. Grava o ScanData completo com
/// timestamp ISO-8601 UTC e versão do app; compara dois scans com thresholds
/// fixos: throughput ±10%, latência ±15%, perda de pacote ±0,5 ponto percentual.
/// </summary>
public static class BaselineStore
{
    public const double ThroughputRelativeThreshold = 0.10;   // ±10%
    public const double LatencyRelativeThreshold = 0.15;      // ±15%
    public const double PacketLossAbsoluteThreshold = 0.005;  // ±0,5 pp (ratio absoluto)

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializa o scan para o formato de baseline (com versão e timestamp UTC).</summary>
    public static string Serialize(ScanData scan, DateTime? savedAtUtc = null, string? appVersion = null)
        => JsonSerializer.Serialize(new BaselineFile(
            AppVersion: appVersion ?? AppVersion(),
            SavedAtUtc: (savedAtUtc ?? DateTime.UtcNow).ToUniversalTime(),
            Scan: scan), FileOptions);

    /// <summary>Grava o baseline em disco.</summary>
    public static void Save(ScanData scan, string path, DateTime? savedAtUtc = null, string? appVersion = null)
        => File.WriteAllText(path, Serialize(scan, savedAtUtc, appVersion));

    /// <summary>Carrega um baseline gravado por <see cref="Save"/>.</summary>
    public static BaselineFile Load(string path)
    {
        var raw = JsonSerializer.Deserialize<BaselineFile>(File.ReadAllText(path), FileOptions)
            ?? throw new InvalidDataException($"Baseline inválido ou vazio: {path}");
        return raw;
    }

    /// <summary>Versão curta do app (AssemblyVersion), ex.: "1.0.0.0".</summary>
    public static string AppVersion()
        => typeof(BaselineStore).Assembly.GetName().Version?.ToString() ?? "unknown";

    // ------------------------------------------------------------------
    // Comparação
    // ------------------------------------------------------------------

    /// <summary>
    /// Compara baseline → scan atual. Métricas-chave: latência, throughput bruto,
    /// throughput observado, perda de pacote, velocidade de link, gargalo dominante e severidade.
    /// O diagnóstico é executado internamente via <see cref="DiagnosisEngine"/> para obter
    /// dominant/severity consistentes com o motor de correlação.
    /// </summary>
    public static IReadOnlyList<MetricComparison> Compare(ScanData before, ScanData after)
    {
        var engine = new DiagnosisEngine();
        var diagBefore = engine.Diagnose(before);
        var diagAfter = engine.Diagnose(after);

        return new List<MetricComparison>
        {
            Relative("LatencyMs", before.LatencyMs, after.LatencyMs, threshold: LatencyRelativeThreshold, betterLower: true),
            HigherIsBetter("RawThroughputBps", before.RawThroughputBps, after.RawThroughputBps),
            HigherIsBetter("ObservedCopyThroughputBps", before.ObservedCopyThroughputBps, after.ObservedCopyThroughputBps),
            PacketLoss(before.PacketLossRatio, after.PacketLossRatio),
            HigherIsBetter("LinkSpeedBps", before.LinkSpeedBps, after.LinkSpeedBps),
            Categorical("dominant", diagBefore.Dominant.ToString(), diagAfter.Dominant.ToString()),
            Categorical("severity", diagBefore.Severity.ToString(), diagAfter.Severity.ToString()),
        };
    }

    /// <summary>Métrica relativa: antes/depois com limiar e direção de melhoria.</summary>
    public static MetricComparison Relative(string metric, double before, double after, double threshold, bool betterLower)
    {
        double delta = after - before;
        var verdict = JudgeRelative(before, delta, betterLower, threshold);
        return new MetricComparison(metric, Round(before), Round(after), Math.Round(delta, 6), verdict);
    }

    /// <summary>Métrica relativa onde valores MAIORES são melhores (throughput, link).</summary>
    public static MetricComparison HigherIsBetter(string metric, double before, double after)
        => Relative(metric, before, after, ThroughputRelativeThreshold, betterLower: false);

    /// <summary>Perda de pacote usa limiar ABSOLUTO (±0,5 pp), não relativo.</summary>
    public static MetricComparison PacketLoss(double before, double after)
    {
        double delta = after - before;
        var verdict = Math.Abs(delta) <= PacketLossAbsoluteThreshold
            ? ComparisonVerdict.Estavel
            : delta < 0 ? ComparisonVerdict.Melhorou : ComparisonVerdict.Piorou;
        return new MetricComparison("PacketLossRatio", Round(before), Round(after), Math.Round(delta, 6), verdict);
    }

    /// <summary>Métrica categórica: igual = ESTÁVEL; senão decide pela hierarquia de impacto.</summary>
    public static MetricComparison Categorical(string metric, string before, string after)
        => new(metric, before, after, null,
            before == after ? ComparisonVerdict.Estavel : RankDelta(before, after));

    /// <summary>
    /// Veredito para métricas com limiar RELATIVO sobre o valor anterior.
    /// Dentro do limiar = ESTÁVEL; fora, o sinal do delta decide.
    /// Valores nulos/zero anteriores: qualquer ganho conta como melhora.
    /// </summary>
    public static ComparisonVerdict JudgeRelative(double before, double delta, bool betterLower, double threshold)
    {
        if (Math.Abs(delta) <= 0)
            return ComparisonVerdict.Estavel;

        double relative = before > 0 ? Math.Abs(delta) / before : double.PositiveInfinity;
        if (relative <= threshold)
            return ComparisonVerdict.Estavel;

        bool improved = betterLower ? delta < 0 : delta > 0;
        return improved ? ComparisonVerdict.Melhorou : ComparisonVerdict.Piorou;
    }

    // Hierarquia de impacto dos gargalos (menor = melhor): None é saudável;
    // críticos de protocolo/rede/disco pesam mais que warnings locais.
    private static readonly Dictionary<string, int> BottleneckRank = new()
    {
        ["None"] = 0,
        ["Cpu"] = 1,
        ["Workload"] = 2,
        ["Antivirus"] = 3,
        ["Protocol"] = 4,
        ["DiskSource"] = 5,
        ["DiskTarget"] = 6,
        ["SmbSigning"] = 7,
        ["SmbEncryption"] = 8,
        ["Network"] = 9,
    };

    private static ComparisonVerdict RankDelta(string before, string after)
    {
        int b = BottleneckRank.GetValueOrDefault(before, 4);
        int a = BottleneckRank.GetValueOrDefault(after, 4);
        return a < b ? ComparisonVerdict.Melhorou : a > b ? ComparisonVerdict.Piorou : ComparisonVerdict.Estavel;
    }

    private static double Round(double v) => Math.Round(v, 6);

    // ------------------------------------------------------------------
    // DTO explícito: isola o JSON de baseline das propriedades calculadas
    // do record ScanData (AverageFileTransferSeconds) e garante round-trip
    // estável mesmo se o record ganhar membros computados no futuro.
    // ------------------------------------------------------------------

    private sealed record ScanDto(
        double LatencyMs,
        double RawThroughputBps,
        int MtuBytes,
        double PacketLossRatio,
        double LinkSpeedBps,
        string NegotiatedDialect,
        bool SigningEnabled,
        bool EncryptionEnabled,
        bool Multichannel,
        int ActiveChannels,
        double CpuUtilization,
        double SourceDiskBusyRatio,
        double TargetDiskBusyRatio,
        double SourceDiskReadBps,
        double TargetDiskWriteBps,
        bool AvFilterOnSharePath,
        double ObservedCopyThroughputBps,
        double AverageFileBytes,
        int FileCount);

    private static ScanDto ToDto(ScanData d) => new(
        d.LatencyMs, d.RawThroughputBps, d.MtuBytes, d.PacketLossRatio, d.LinkSpeedBps,
        d.NegotiatedDialect, d.SigningEnabled, d.EncryptionEnabled, d.Multichannel, d.ActiveChannels,
        d.CpuUtilization, d.SourceDiskBusyRatio, d.TargetDiskBusyRatio, d.SourceDiskReadBps, d.TargetDiskWriteBps,
        d.AvFilterOnSharePath, d.ObservedCopyThroughputBps, d.AverageFileBytes, d.FileCount);

    private static ScanData FromDto(ScanDto dto) => new(
        dto.LatencyMs, dto.RawThroughputBps, dto.MtuBytes, dto.PacketLossRatio, dto.LinkSpeedBps,
        dto.NegotiatedDialect, dto.SigningEnabled, dto.EncryptionEnabled, dto.Multichannel, dto.ActiveChannels,
        dto.CpuUtilization, dto.SourceDiskBusyRatio, dto.TargetDiskBusyRatio, dto.SourceDiskReadBps, dto.TargetDiskWriteBps,
        dto.AvFilterOnSharePath, dto.ObservedCopyThroughputBps, dto.AverageFileBytes, dto.FileCount);
}
