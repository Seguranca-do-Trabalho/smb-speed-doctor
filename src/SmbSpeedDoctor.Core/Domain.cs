namespace SmbSpeedDoctor.Core;

/// <summary>Layer where the dominant bottleneck was identified.</summary>
public enum Bottleneck
{
    None,
    Network,
    SmbSigning,
    SmbEncryption,
    SmbMultichannel,
    DiskSource,
    DiskTarget,
    Cpu,
    Antivirus,
    Workload,
    Protocol,
}

/// <summary>
/// Origin of throughput in <see cref="ScanData.ObservedCopyThroughputBps"/>.
///
/// Exists because "did not measure" and "measured and got near zero" yield the SAME
/// number, and the engine previously treated both as bottleneck evidence — concluding
/// "SMB signing critical" on an idle network and recommending disabling SMB signing
/// without any actual measurement. Rule: throughput-derived conclusions require
/// <see cref="Measured"/> or <see cref="Approximated"/>.
/// </summary>
public enum MeasurementQuality
{
    /// <summary>No valid measurement. Cannot support conclusions about throughput.</summary>
    Unavailable,

    /// <summary>
    /// Estimated from observed NIC traffic, above the credibility floor.
    /// Supports conclusions, with lower confidence.
    /// </summary>
    Approximated,

    /// <summary>Real test copy executed and benchmarked.</summary>
    Measured,
}

/// <summary>Severity of the detected bottleneck.</summary>
public enum Severity
{
    Ok,
    Warning,
    Critical,
}

/// <summary>
/// Raw collection of all measured layers during a scan.
/// Normalized values (bytes/s, ms, 0-1).
/// </summary>
public sealed record ScanData(
    // Network
    double LatencyMs,
    double RawThroughputBps,     // raw network throughput (e.g. iperf-like)
    int MtuBytes,
    double PacketLossRatio,
    double LinkSpeedBps,         // negotiated adapter speed
    // SMB
    string NegotiatedDialect,    // e.g. "3.1.1"
    bool SigningEnabled,
    bool EncryptionEnabled,
    bool Multichannel,
    int ActiveChannels,
    // Local
    double CpuUtilization,       // 0-1
    double SourceDiskBusyRatio,  // 0-1
    double TargetDiskBusyRatio,  // 0-1
    double SourceDiskReadBps,
    double TargetDiskWriteBps,
    bool AvFilterOnSharePath,
    // Observed workload
    double ObservedCopyThroughputBps,
    double AverageFileBytes,
    int FileCount,
    // Origin of the throughput above. Default is the SAFE value: scans that
    // do not explicitly declare a measurement do not receive throughput conclusions.
    MeasurementQuality ThroughputQuality = MeasurementQuality.Unavailable)
{
    /// <summary>Does the observed throughput support bottleneck conclusions?</summary>
    public bool HasUsableThroughput => ThroughputQuality != MeasurementQuality.Unavailable;

    /// <summary>Estimated average transfer duration per file in observed copy.</summary>
    public double AverageFileTransferSeconds =>
        FileCount > 0 && ObservedCopyThroughputBps > 0
            ? AverageFileBytes * FileCount / ObservedCopyThroughputBps / Math.Max(1, FileCount)
            : 0;
}

/// <summary>Recommended (or applicable) remediation with rollback.</summary>
public sealed record Remediation(
    string Id,
    string Title,
    string Description,
    string RollbackDescription,
    IReadOnlyList<string> Commands);

/// <summary>Final diagnostic verdict.</summary>
public sealed record DiagnosisResult(
    string OneLineSummary,
    Bottleneck Dominant,
    Severity Severity,
    double ConfidencePct,
    IReadOnlyList<Finding> Findings,
    Remediation? RecommendedRemediation,
    CopyMethodProfile RecommendedMethod);

/// <summary>Individual evidence from a layer.</summary>
public sealed record Finding(
    string Layer,
    string Metric,
    string Value,
    string Interpretation,
    Severity Severity,
    double WeightContribution);

/// <summary>Optimal copy method for the measured workload profile.</summary>
public sealed record CopyMethodProfile(string MethodName, string Rationale);
