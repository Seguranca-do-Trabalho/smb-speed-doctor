// Author: forg3 | junkyardgoodies.app
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmbSpeedDoctor.Core;

/// <summary>Comparative verdict between two scans.</summary>
public enum ComparisonVerdict
{
    Improved,
    Regressed,
    Stable,
}

/// <summary>
/// Baseline file saved by <c>scan --save</c>: metadata + full ScanData.
/// </summary>
public sealed record BaselineFile(
    string AppVersion,
    DateTime SavedAtUtc,
    ScanData Scan);

/// <summary>A row in the before->after table of <c>scan --compare</c>.</summary>
public sealed record MetricComparison(
    string Metric,
    object? Before,
    object? After,
    double? Delta,
    ComparisonVerdict Verdict)
{
    public string VerdictLabel => Verdict switch
    {
        ComparisonVerdict.Improved => "IMPROVED",
        ComparisonVerdict.Regressed => "REGRESSED",
        _ => "STABLE",
    };
}

/// <summary>
/// Persistence and comparison of baselines. Saves complete ScanData with
/// ISO-8601 UTC timestamp and app version; compares two scans with fixed
/// thresholds: throughput ±10%, latency ±15%, packet loss ±0.5 percentage points.
/// </summary>
public static class BaselineStore
{
    public const double ThroughputRelativeThreshold = 0.10;   // ±10%
    public const double LatencyRelativeThreshold = 0.15;      // ±15%
    public const double PacketLossAbsoluteThreshold = 0.005;  // ±0.5 pp (absolute ratio)

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes the scan to baseline format (with version and UTC timestamp).</summary>
    public static string Serialize(ScanData scan, DateTime? savedAtUtc = null, string? appVersion = null)
        => JsonSerializer.Serialize(new BaselineFile(
            AppVersion: appVersion ?? AppVersion(),
            SavedAtUtc: (savedAtUtc ?? DateTime.UtcNow).ToUniversalTime(),
            Scan: scan), FileOptions);

    /// <summary>Saves baseline to disk.</summary>
    public static void Save(ScanData scan, string path, DateTime? savedAtUtc = null, string? appVersion = null)
        => File.WriteAllText(path, Serialize(scan, savedAtUtc, appVersion));

    /// <summary>Loads a baseline saved by <see cref="Save"/>.</summary>
    public static BaselineFile Load(string path)
    {
        var raw = JsonSerializer.Deserialize<BaselineFile>(File.ReadAllText(path), FileOptions)
            ?? throw new InvalidDataException($"Invalid or empty baseline: {path}");
        return raw;
    }

    /// <summary>Short version of the app (AssemblyVersion), e.g. "1.0.0.0".</summary>
    public static string AppVersion()
        => typeof(BaselineStore).Assembly.GetName().Version?.ToString() ?? "unknown";

    // ------------------------------------------------------------------
    // Comparison
    // ------------------------------------------------------------------

    /// <summary>
    /// Compares baseline -> current scan. Key metrics: latency, raw throughput,
    /// observed throughput, packet loss, link speed, dominant bottleneck, and severity.
    /// Diagnosis is executed internally via <see cref="DiagnosisEngine"/> to obtain
    /// dominant/severity consistent with the correlation engine.
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

    /// <summary>Relative metric: before/after with threshold and improvement direction.</summary>
    public static MetricComparison Relative(string metric, double before, double after, double threshold, bool betterLower)
    {
        double delta = after - before;
        var verdict = JudgeRelative(before, delta, betterLower, threshold);
        return new MetricComparison(metric, Round(before), Round(after), Math.Round(delta, 6), verdict);
    }

    /// <summary>Relative metric where HIGHER values are better (throughput, link speed).</summary>
    public static MetricComparison HigherIsBetter(string metric, double before, double after)
        => Relative(metric, before, after, ThroughputRelativeThreshold, betterLower: false);

    /// <summary>Packet loss uses ABSOLUTE threshold (±0.5 pp), not relative.</summary>
    public static MetricComparison PacketLoss(double before, double after)
    {
        double delta = after - before;
        var verdict = Math.Abs(delta) <= PacketLossAbsoluteThreshold
            ? ComparisonVerdict.Stable
            : delta < 0 ? ComparisonVerdict.Improved : ComparisonVerdict.Regressed;
        return new MetricComparison("PacketLossRatio", Round(before), Round(after), Math.Round(delta, 6), verdict);
    }

    /// <summary>Categorical metric: equal = STABLE; otherwise decides by impact hierarchy.</summary>
    public static MetricComparison Categorical(string metric, string before, string after)
        => new(metric, before, after, null,
            before == after ? ComparisonVerdict.Stable : RankDelta(before, after));

    /// <summary>
    /// Verdict for metrics with RELATIVE threshold over previous value.
    /// Within threshold = STABLE; outside, delta sign decides.
    /// Previous null/zero values: any gain counts as improvement.
    /// </summary>
    public static ComparisonVerdict JudgeRelative(double before, double delta, bool betterLower, double threshold)
    {
        if (Math.Abs(delta) <= 0)
            return ComparisonVerdict.Stable;

        double relative = before > 0 ? Math.Abs(delta) / before : double.PositiveInfinity;
        if (relative <= threshold)
            return ComparisonVerdict.Stable;

        bool improved = betterLower ? delta < 0 : delta > 0;
        return improved ? ComparisonVerdict.Improved : ComparisonVerdict.Regressed;
    }

    // Impact hierarchy of bottlenecks (lower = better): None is healthy;
    // critical protocol/network/disk bottlenecks outrank local warnings.
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
        return a < b ? ComparisonVerdict.Improved : a > b ? ComparisonVerdict.Regressed : ComparisonVerdict.Stable;
    }

    private static double Round(double v) => Math.Round(v, 6);

    // ------------------------------------------------------------------
    // Explicit DTO: isolates baseline JSON from computed properties
    // of ScanData record (AverageFileTransferSeconds) and ensures stable
    // round-trip even if the record gains computed members in the future.
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
