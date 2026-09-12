// Created by forg3
// License: MIT
//
// Generates robocopy command profiled by diagnosis.
// Translates measured profile (file count, average size, latency, loss)
// into a complete and justified robocopy command line — closes diagnosis→action loop.

using System.Globalization;
using SmbSpeedDoctor.Core;

namespace SmbSpeedDoctor.Cli;

/// <summary>
/// Builds optimal robocopy command for diagnosed scenario.
/// Field-derived rules:
///   - Large / few files: /J (unbuffered) avoids cache manager overhead
///   - Many small files: /MT:N parallelizes seeks (latency dominates)
///   - Unstable link (loss): /ZB resumes interrupted transfers
///   - High latency: higher /MT compensates for open TCP windows
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
            flags.Add("/J");                       // unbuffered for large files
        else if (manyFiles && smallFiles)
            flags.Add($"/MT:{threads}");           // seek parallelism
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
            SourceHint: "<source>",
            TargetHint: @"<destination> \\server\share");
    }

    private static string BuildRationale(ScanData d, bool many, bool small, bool big, bool unstable, int threads)
    {
        var parts = new List<string>();
        parts.Add(big ? "large files → /J unbuffered"
              : many && small ? $"{d.FileCount} small files → /MT:{threads} parallelizes seeks"
              : $"mixed workload → /MT:{threads} + /J");

        if (unstable)
            parts.Add($"link with loss {d.PacketLossRatio:P1}/latency {d.LatencyMs:F0}ms → /ZB restartable");

        if (d.SigningEnabled)
            parts.Add("SMB signing active — expected throughput limited (see remediation)");

        if (d.EncryptionEnabled)
            parts.Add("SMB encryption active — additional CPU cost per I/O");

        return string.Join("; ", parts);
    }

    private static double Estimate(ScanData d)
    {
        // Practical ceiling = lowest among theoretical link (x0.9), observed copy, and disk
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

/// <summary>Complete plan ready for display in JSON and GUI.</summary>
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
