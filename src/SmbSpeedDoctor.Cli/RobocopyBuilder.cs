// Criado por André Santo (forg3) | junkyardgoodies.app
// Licença: MIT
//
// Item 9 — gerador de comando robocopy profiled pelo diagnóstico.
// Traduz o perfil medido (nº de arquivos, tamanho médio, latência, perda)
// numa linha robocopy completa e justificada — fecha o ciclo diagnóstico→ação.

using System.Globalization;
using SmbSpeedDoctor.Core;

namespace SmbSpeedDoctor.Cli;

/// <summary>
/// Constrói a linha robocopy ótima para o cenário diagnosticado.
/// Regras derivadas de campo:
///   - Arquivos grandes / poucos: /J (unbuffered) evita cóstio de cache manager
///   - Muitos arquivos pequenos: /MT:N paraleliza seeks (latência domina)
///   - Link instável (perda): /ZB retoma transferências interrompidas
///   - Latência alta: /MT maior compensa janelas TCP em aberto
/// </summary>
public static class RobocopyBuilder
{
    public static RobocopyPlan Build(ScanData d, DiagnosisResult result)
    {
        bool manyFiles = d.FileCount >= 1000;
        bool smallFiles = d.AverageFileBytes > 0 && d.AverageFileBytes < 1024 * 1024; // < 1 MB
        bool bigFiles = d.AverageFileBytes >= 512 * 1024 * 1024;                      // >= 512 MB
        bool unstableLink = d.PacketLossRatio >= 0.005 || d.LatencyMs >= 20;

        int threads = unstableLink ? 16 : manyFiles ? 12 : 4;

        var flags = new List<string> { "/R:2", "/W:2", "/NP", "/NDL" };

        if (bigFiles || (!manyFiles && !smallFiles))
            flags.Add("/J");                       // unbuffered p/ arquivos grandes
        else if (manyFiles && smallFiles)
            flags.Add($"/MT:{threads}");           // paralelismo de seek
        else
        {
            flags.Add($"/MT:{threads}");
            flags.Add("/J");
        }

        if (unstableLink)
            flags.Insert(1, "/ZB");                // restartable + backup mode

        string rationale = BuildRationale(d, manyFiles, smallFiles, bigFiles, unstableLink, threads);

        return new RobocopyPlan(
            MethodName: "robocopy " + string.Join(' ', flags),
            Rationale: rationale,
            EstimatedThroughputMBps: Estimate(d),
            SourceHint: "<origem>",
            TargetHint: "<destino> \\\\servidor\\share");
    }

    private static string BuildRationale(ScanData d, bool many, bool small, bool big, bool unstable, int threads)
    {
        var parts = new List<string>();
        parts.Add(big ? "arquivos grandes → /J unbuffered"
              : many && small ? $"{d.FileCount} arquivos pequenos → /MT:{threads} paraleliza seeks"
              : $"workload misto → /MT:{threads} + /J");

        if (unstable)
            parts.Add($"link com perda {d.PacketLossRatio:P1}/latência {d.LatencyMs:F0}ms → /ZB retomável");

        if (d.SigningEnabled)
            parts.Add("assinatura SMB ativa — throughput esperado limitado (ver remediação)");

        if (d.EncryptionEnabled)
            parts.Add("criptografia SMB ativa — custo adicional de CPU por I/O");

        return string.Join("; ", parts);
    }

    private static double Estimate(ScanData d)
    {
        // Teto prático = menor entre link teórico (×0.9), cópia observada e disco
        double linkCeiling = d.LinkSpeedBps > 0 ? d.LinkSpeedBps / 8.0 * 0.9 : double.MaxValue;
        double observed = d.ObservedCopyThroughputBps > 0
            ? d.ObservedCopyThroughputBps
            : double.MaxValue;
        double disk = Math.Min(
            d.SourceDiskReadBps > 0 ? d.SourceDiskReadBps : double.MaxValue,
            d.TargetDiskWriteBps > 0 ? d.TargetDiskWriteBps : double.MaxValue);
        return Math.Min(linkCeiling, Math.Min(observed, disk)) / (1024.0 * 1024.0);
    }
}

/// <summary>Plano completo pronto para exibição no JSON e na GUI.</summary>
public sealed record RobocopyPlan(
    string MethodName,
    string Rationale,
    double EstimatedThroughputMBps,
    string SourceHint,
    string TargetHint)
{
    public string FullCommand(CultureInfo? culture = null)
        => $"{MethodName} \"{SourceHint}\" \"{TargetHint}\"";
}
