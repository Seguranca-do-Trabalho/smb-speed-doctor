namespace SmbSpeedDoctor.Core;

/// <summary>Camada onde foi identificado o gargalo dominante.</summary>
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
/// Procedência do throughput em <see cref="ScanData.ObservedCopyThroughputBps"/>.
///
/// Existe porque "não medi nada" e "medi e deu quase zero" produzem o MESMO
/// número, e o motor tratava os dois como evidência de gargalo — concluindo
/// "assinatura SMB crítica" numa rede ociosa e recomendando desligar a
/// assinatura sem nenhuma medição. Regra: conclusão derivada de throughput
/// exige <see cref="Measured"/> ou <see cref="Approximated"/>.
/// </summary>
public enum MeasurementQuality
{
    /// <summary>Sem medição válida. Não sustenta conclusão sobre throughput.</summary>
    Unavailable,

    /// <summary>
    /// Estimado a partir de tráfego observado na NIC, com volume acima do piso
    /// de credibilidade. Serve para conclusão, com confiança menor.
    /// </summary>
    Approximated,

    /// <summary>Cópia de teste real executada e cronometrada.</summary>
    Measured,
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
    int FileCount,
    // Procedência do throughput acima. Default é o valor SEGURO: quem não
    // declara explicitamente que mediu não recebe conclusão de throughput.
    MeasurementQuality ThroughputQuality = MeasurementQuality.Unavailable)
{
    /// <summary>Throughput observado sustenta conclusão de gargalo?</summary>
    public bool HasUsableThroughput => ThroughputQuality != MeasurementQuality.Unavailable;

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
