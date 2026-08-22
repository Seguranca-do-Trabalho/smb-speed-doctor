namespace SmbSpeedDoctor.Core;

/// <summary>Camada onde foi identificado o gargalo dominante.</summary>
public enum Bottleneck
{
    None,
    Network,
    SmbSigning,
    SmbEncryption,
    DiskSource,
    DiskTarget,
    Cpu,
    Antivirus,
    Workload,
    Protocol,
}

/// <summary>Severidade do gargalo detectado.</summary>
public enum Severity
{
    Ok,
    Warning,
    Critical,
}

/// <summary>
/// Coleta bruta de todas as camadas medidas durante um scan.
/// Valores normalizados (bytes/s, ms, 0-1).
/// </summary>
public sealed record ScanData(
    // Rede
    double LatencyMs,
    double RawThroughputBps,     // throughput bruto da rede (ex.: iperf-like)
    int MtuBytes,
    double PacketLossRatio,
    double LinkSpeedBps,         // velocidade do adaptador (negociada)
    // SMB
    string NegotiatedDialect,    // ex.: "3.1.1"
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
    // Carga observada
    double ObservedCopyThroughputBps,
    double AverageFileBytes,
    int FileCount)
{
    /// <summary>Duração média estimada por arquivo na cópia observada.</summary>
    public double AverageFileTransferSeconds =>
        FileCount > 0 && ObservedCopyThroughputBps > 0
            ? AverageFileBytes * FileCount / ObservedCopyThroughputBps / Math.Max(1, FileCount)
            : 0;
}

/// <summary>Ajuste recomendado (ou aplicável) com rollback.</summary>
public sealed record Remediation(
    string Id,
    string Title,
    string Description,
    string RollbackDescription,
    IReadOnlyList<string> Commands);

/// <summary>Veredicto final do diagnóstico.</summary>
public sealed record DiagnosisResult(
    string OneLineSummary,
    Bottleneck Dominant,
    Severity Severity,
    double ConfidencePct,
    IReadOnlyList<Finding> Findings,
    Remediation? RecommendedRemediation,
    CopyMethodProfile RecommendedMethod);

/// <summary>Evidência individual de uma camada.</summary>
public sealed record Finding(
    string Layer,
    string Metric,
    string Value,
    string Interpretation,
    Severity Severity,
    double WeightContribution);

/// <summary>Método de cópia ótimo para o perfil medido.</summary>
public sealed record CopyMethodProfile(string MethodName, string Rationale);
