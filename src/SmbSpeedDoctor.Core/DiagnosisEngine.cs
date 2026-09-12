// Author: forg3 | junkyardgoodies.app
using SmbSpeedDoctor.Core;

namespace SmbSpeedDoctor.Core;

/// <summary>
/// Correlation engine: cross-references data from all layers and identifies the
/// dominant bottleneck. Rules derived from field experience (24H2/25H2,
/// SMB signing, local HDDs, small file workloads).
/// </summary>
public sealed class DiagnosisEngine
{
    private const double DiskThroughputHddMbs = 90;   // ~90 MB/s
    private const double DiskThroughputSsdMbs = 500;  // typical SSD
    private const long MinFileForSmallWorkloadBytes = 64 * 1024;
    private const long SmallWorkloadFileCount = 10_000;

    public DiagnosisResult Diagnose(ScanData d)
    {
        var findings = new List<Finding>();
        var scores = new Dictionary<Bottleneck, double>();

        // Every rule concluding from throughput requires a valid measurement.
        // Without it, "did not measure" and "measured and got zero" would be indistinguishable —
        // which is how an idle network was previously flagged as "SMB signing critical".
        bool usableThroughput = d.HasUsableThroughput;
        if (!usableThroughput)
        {
            findings.Add(new Finding("Scan", "ThroughputQuality", "unavailable",
                "test copy not executed — without throughput measurement, diagnosis " +
                "cannot conclude on signing, multichannel, disk, CPU, or link utilization " +
                "(use --path <share> to measure)",
                Severity.Ok, 0.0));
        }

        // --- Local disk (checked FIRST for prioritization) ---
        double targetLimitBps = TargetDiskLimit(d);
        double sourceLimitBps = SourceDiskLimit(d);
        bool diskIssue = false;
        bool workloadIssue = false;

        if (usableThroughput && d.TargetDiskBusyRatio >= 0.9 && d.ObservedCopyThroughputBps < targetLimitBps * 0.95)
        {
            findings.Add(new Finding("DiskTarget", "BusyRatio", $"{d.TargetDiskBusyRatio:P0}",
                $"target disk saturated (estimated limit {targetLimitBps / 1_000_000:F0} MB/s)",
                Severity.Critical, 8.0));
            scores[Bottleneck.DiskTarget] = 8.0;
            diskIssue = true;
        }
        if (usableThroughput && d.SourceDiskBusyRatio >= 0.9 && d.ObservedCopyThroughputBps < sourceLimitBps * 0.95)
        {
            findings.Add(new Finding("DiskSource", "BusyRatio", $"{d.SourceDiskBusyRatio:P0}",
                "source disk saturated", Severity.Critical, 6.0));
            scores[Bottleneck.DiskSource] = 6.0;
            diskIssue = true;
        }

        // --- Workload ---
        if (usableThroughput && d.AverageFileBytes < MinFileForSmallWorkloadBytes && d.FileCount >= SmallWorkloadFileCount)
        {
            double expectedThroughput = EstimateSmallFileThroughput(d);
            if (d.ObservedCopyThroughputBps > expectedThroughput * 0.8)
            {
                findings.Add(new Finding("Workload", "AvgFileSize", $"{BytesToString((long)d.AverageFileBytes)}",
                    $"workload of {d.FileCount} small files — throughput within expectations",
                    Severity.Warning, 0.0));
                scores[Bottleneck.Workload] = 5.0;
                workloadIssue = true;
            }
        }

        // --- Network (packet loss always outranks SMB when critical) ---
        if (d.PacketLossRatio >= 0.01)
        {
            double penalty = d.PacketLossRatio >= 0.03 ? 12.0 : 6.0;
            findings.Add(new Finding("Network", "PacketLossRatio",
                $"{d.PacketLossRatio:P0}", "Packet loss degrading TCP/SMB",
                d.PacketLossRatio >= 0.03 ? Severity.Critical : Severity.Warning,
                penalty));
            scores[Bottleneck.Network] = Math.Max(scores.GetValueOrDefault(Bottleneck.Network), penalty);
        }
        // Negotiated low link (<= 100 Mbit) on modern network: old Cat5/Cat5e cable,
        // 10/100 switch port, or bad duplex negotiation. The link IS the bottleneck.
        else if (d.LinkSpeedBps > 0 && d.LinkSpeedBps <= 100_000_000)
        {
            findings.Add(new Finding("Network", "NegotiatedLinkSpeed",
                FmtLink(d.LinkSpeedBps),
                "negotiated link speed well below modern standards — check cable (Cat6+) and switch port",
                Severity.Warning, 8.0));
            scores[Bottleneck.Network] = Math.Max(scores.GetValueOrDefault(Bottleneck.Network), 8.0);
        }
        // LinkUtilization: only scores with credible link measurement (>= 10 Mbit/s and <= 400 Gbit/s)
        // AND with observed real traffic (idle network is not a bottleneck).
        // Virtual links report 100+ Gb/s nominal; without reliable physical NIC, no weight.
        bool linkCredible = d.LinkSpeedBps >= 10_000_000 && d.LinkSpeedBps <= 400_000_000_000;
        bool hasTraffic = usableThroughput
            && (d.RawThroughputBps > 1_000_000 || d.ObservedCopyThroughputBps > 1_000_000);
        if (!linkCredible && d.LinkSpeedBps > 0)
        {
            findings.Add(new Finding("Network", "LinkSpeed",
                FmtLink(d.LinkSpeedBps),
                "link speed outside credible range — likely a virtual adapter",
                Severity.Warning, 0));
        }
        if (linkCredible && hasTraffic && d.RawThroughputBps / d.LinkSpeedBps < 0.3
            && d.ObservedCopyThroughputBps * 8.0 < d.LinkSpeedBps * 0.15)
        {
            findings.Add(new Finding("Network", "LinkUtilization",
                $"{d.ObservedCopyThroughputBps / Math.Max(1, d.LinkSpeedBps):P1}",
                "observed throughput well below link capacity",
                Severity.Warning, 3.0));
            scores[Bottleneck.Network] = Math.Max(scores.GetValueOrDefault(Bottleneck.Network), 3.0);
        }

        // --- SMB (only evaluated if disk/workload are not compromised) ---
        bool signed = d.SigningEnabled;
        bool encrypted = d.EncryptionEnabled;
        string dialect = d.NegotiatedDialect;

        // Undetermined dialect is NOT a good dialect: previously fell through `_ =>`
        // and was reported as "modern dialect", turning missing data into a positive verdict (fail-open).
        bool dialectKnown = !string.IsNullOrWhiteSpace(dialect) && char.IsDigit(dialect[0]);

        findings.Add(new Finding("SMB", "NegotiatedDialect",
            dialectKnown ? dialect : "undetermined",
            !dialectKnown
                ? "undetermined dialect — insufficient basis to classify protocol"
                : dialect switch
                {
                    "1.0" or "2.0" => "legacy dialect, high overhead",
                    "2.1" => "legacy dialect, moderate overhead",
                    _ => "modern dialect"
                },
            !dialectKnown ? Severity.Ok
                : dialect.StartsWith("1.") || dialect.StartsWith("2.0") ? Severity.Critical
                : dialect.StartsWith("2.1") ? Severity.Warning
                : Severity.Ok,
            !dialectKnown ? 0
                : dialect.StartsWith("1.") ? 10.0
                : dialect.StartsWith("2.0") ? 6.0
                : 0));

        // Legacy protocol is concluded from the dialect itself — independent of measurement.
        if (dialectKnown && (dialect.StartsWith("1.") || dialect.StartsWith("2.0")))
            scores[Bottleneck.Protocol] = 10.0;

        if (encrypted)
        {
            findings.Add(new Finding("SMB", "EncryptionEnabled", "true",
                "SMB encryption active — CPU overhead on every I/O", Severity.Critical, 9.0));
            scores[Bottleneck.SmbEncryption] = 9.0;
        }
        else if (usableThroughput && signed && !diskIssue && !workloadIssue)
        {
            // Only evaluate signing if there was a MEASUREMENT and neither disk nor
            // workload is bottlenecked. Recommending disabling signing is a
            // security downgrade: requires measured evidence, never an idle network estimate.
            double efficiency = d.LinkSpeedBps > 0
                ? (d.ObservedCopyThroughputBps * 8.0) / d.LinkSpeedBps
                : 1.0;
            // Only flag SMB as bottleneck if efficiency is low (<50%)
            double smbScore = efficiency < 0.15 ? 9.0 : efficiency < 0.5 ? 6.0 : 0.0;
            // Zero score is not inserted into dictionary: avoids unwarranted dominance in healthy profiles.
            if (smbScore > 0)
            {
                findings.Add(new Finding("SMB", "SigningEnabled", "true",
                    "SMB signing active — hash overhead on every packet",
                    smbScore >= 7.0 ? Severity.Critical : smbScore >= 4.0 ? Severity.Warning : Severity.Ok,
                    smbScore));
                scores[Bottleneck.SmbSigning] = Math.Max(scores.GetValueOrDefault(Bottleneck.SmbSigning), smbScore);
            }
        }

        if (usableThroughput && !d.Multichannel && d.LinkSpeedBps >= 1_000_000_000 && d.ActiveChannels == 1)
        {
            // Only penalize multichannel if overall efficiency is already low
            double efficiency = d.LinkSpeedBps > 0
                ? (d.ObservedCopyThroughputBps * 8.0) / d.LinkSpeedBps
                : 1.0;
            if (efficiency < 0.5 && !diskIssue && !workloadIssue)
            {
                findings.Add(new Finding("SMB", "Multichannel", "disabled",
                    "multichannel disabled on link >= 1 Gb/s", Severity.Warning, 3.0));
                // OWN bucket: previously summed into SmbSigning score, so reports
                // listed multichannel while blaming signing — remediating by disabling signing for an unrelated issue.
                scores[Bottleneck.SmbMultichannel] =
                    Math.Max(scores.GetValueOrDefault(Bottleneck.SmbMultichannel), 3.0);
            }
        }

        // --- CPU ---
        if (usableThroughput && d.CpuUtilization > 0.85 && d.ObservedCopyThroughputBps * 8.0 < d.LinkSpeedBps * 0.1)
        {
            findings.Add(new Finding("CPU", "Utilization", $"{d.CpuUtilization:P0}",
                "high CPU during SMB copy", Severity.Warning, 4.0));
            scores[Bottleneck.Cpu] = 4.0;
        }

        // Antivirus
        if (d.AvFilterOnSharePath)
        {
            findings.Add(new Finding("Antivirus", "FilterOnSharePath", "true",
                "antivirus filter on SMB share path", Severity.Warning, 5.0));
            scores[Bottleneck.Antivirus] = 5.0;
        }

        // --- Decision ---
        if (scores.Count == 0)
            return HealthyResult(findings, usableThroughput);

        var dominant = scores.OrderByDescending(kv => kv.Value).First().Key;
        double confidence = Math.Clamp(scores.Values.Max() / (scores.Values.Max() + 2.0) * 100, 40, 99);
        // Without throughput measurement, remaining signals are direct;
        // nonetheless the picture is partial and confidence cannot claim full weight.
        if (!usableThroughput) confidence = Math.Min(confidence, 70);

        Severity severity = dominant switch
        {
            Bottleneck.SmbSigning or Bottleneck.SmbEncryption or Bottleneck.Network
                or Bottleneck.DiskTarget or Bottleneck.DiskSource
                or Bottleneck.Protocol => Severity.Critical,
            _ => Severity.Warning
        };

        var remediation = dominant switch
        {
            Bottleneck.SmbSigning => Remediations.Signing(),
            Bottleneck.SmbEncryption => Remediations.Encryption(),
            Bottleneck.SmbMultichannel => Remediations.Multichannel(),
            Bottleneck.Protocol => Remediations.Protocol(),
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

    private static DiagnosisResult HealthyResult(IReadOnlyList<Finding> findings, bool usableThroughput)
        => new(
            usableThroughput
                ? "Scan completed: no dominant bottleneck identified — throughput within expected capacity."
                : "Partial scan: no issues detected in direct signals, but test copy was "
                  + "not executed — nothing was verified about throughput. Use --path <share>.",
            Bottleneck.None,
            Severity.Ok,
            // Absence of evidence is not evidence of absence.
            usableThroughput ? 95.0 : 55.0,
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
        // Approximation: overhead per file ~500 µs (TCP+disk seek+SMB negotiation)
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
                $"unbuffered copy (robocopy /J /ZB) for {count} medium/large file(s)");
        if (count >= SmallWorkloadFileCount)
            return new CopyMethodProfile("parallel robocopy (/MT)",
                $"workload of {count} small files — parallelism maximizes seek");
        return new CopyMethodProfile("robocopy /ZB /MT:8",
            "mix of buffered/unbuffered with 8 threads for mixed workload");
    }

    private static string BuildSummary(Bottleneck dominant, ScanData d)
        => dominant switch
        {
            Bottleneck.SmbSigning =>
                $"Network is {FmtLink(d.LinkSpeedBps)}, but SMB copy drops to {FmtThroughput(d.ObservedCopyThroughputBps)}. " +
                $"The dominant bottleneck is SMB signing.",
            Bottleneck.SmbEncryption =>
                $"Network is {FmtLink(d.LinkSpeedBps)}, but SMB copy drops to {FmtThroughput(d.ObservedCopyThroughputBps)}. " +
                $"The dominant bottleneck is active SMB encryption.",
            Bottleneck.Network =>
                d.PacketLossRatio >= 0.01
                    ? $"Packet loss ({d.PacketLossRatio:P1}) on {FmtLink(d.LinkSpeedBps)} impacting TCP/SMB."
                    : $"Capacity of {FmtLink(d.LinkSpeedBps)} underutilized during observed copy.",
            Bottleneck.DiskTarget =>
                $"Target disk saturated, limiting copy to {FmtThroughput(d.TargetDiskWriteBps)}.",
            Bottleneck.DiskSource =>
                $"Source disk saturated, limiting read to {FmtThroughput(d.SourceDiskReadBps)}.",
            Bottleneck.SmbMultichannel =>
                $"Link of {FmtLink(d.LinkSpeedBps)} with multichannel disabled and single channel — "
                + $"idle capacity in observed copy ({FmtThroughput(d.ObservedCopyThroughputBps)}).",
            Bottleneck.Protocol =>
                $"Connection negotiated legacy SMB dialect ({d.NegotiatedDialect}) — high overhead and, "
                + $"in the case of SMB1, security risk.",
            Bottleneck.Workload =>
                $"Low throughput expected: {d.FileCount} small files averaging {BytesToString((long)d.AverageFileBytes)}.",
            _ => "Undetermined bottleneck."
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
/// Exit-code mapper for RMM integration (0 = ok, 2 = bottleneck, 1 = error).
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
        Title: "Disable SMB signing on client",
        Description: "Removes HMAC-SHA-256 hash overhead on each SMB packet. Recommended on trusted networks (dedicated segment, isolated VLAN).",
        RollbackDescription: "Re-enables signing according to domain default policy.",
        Commands:
        [
            "Set-SmbClientConfiguration -RequireSecuritySignature $false",
            "Restart-Service lanmanworkstation"
        ]);

    public static Remediation Encryption() => new(
        Id: "SMB_ENCRYPTION_DISABLE_CLIENT",
        Title: "Disable mandatory SMB encryption on client",
        Description: "SMB encryption adds CPU overhead per packet. On trusted networks, signing already protects integrity.",
        RollbackDescription: "Re-enables encryption requirement via policy.",
        Commands:
        [
            "Set-SmbClientConfiguration -RequireEncryption $false",
            "Restart-Service lanmanworkstation"
        ]);

    public static Remediation Multichannel() => new(
        Id: "SMB_MULTICHANNEL_ENABLE",
        Title: "Enable SMB Multichannel on client",
        Description: "Disabled multichannel on >= 1 Gb/s links underutilizes RSS queues and "
                   + "additional NICs. Unlike signing, enabling multichannel does NOT lower "
                   + "security posture. See scripts/remediation/Enable-SmbMultichannel.ps1, "
                   + "which verifies feasibility (NICs/RSS) before applying and includes rollback.",
        RollbackDescription: "Restores EnableMultiChannel to the value saved before modification.",
        Commands:
        [
            "Set-SmbClientConfiguration -EnableMultiChannel $true",
            "Get-SmbClientNetworkInterface"
        ]);

    public static Remediation Protocol() => new(
        Id: "SMB_LEGACY_DIALECT",
        Title: "Legacy SMB dialect negotiated",
        Description: "The connection negotiated SMB 1.x/2.0. In addition to overhead, SMB1 is obsolete and "
                   + "insecure. Remediation is to enable modern dialect on BOTH ends — do not "
                   + "change signing, which is not the cause here.",
        RollbackDescription: "No configuration changes applied automatically — manual action required on both ends.",
        Commands:
        [
            "Get-SmbConnection | Select-Object ServerName,Dialect",
            "Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol"
        ]);

    public static Remediation Disk(double busyRatio, double bps) => new(
        Id: "DISK_CONTENTION",
        Title: "Relieve disk contention",
        Description: $"Disk at {busyRatio:P0} utilization and measured throughput of {Fmt(bps)}. Check: fragmentation, RAID rebuild, other IO consumer.",
        RollbackDescription: "No configuration changes applied — planning recommendations only.",
        Commands: []);

    public static Remediation Network(double loss, int mtu) => new(
        Id: "NETWORK_OPTIMIZE",
        Title: "Network optimization",
        Description: $"Packet loss of {loss:P1} detected. Test MTU {mtu} vs 9000 (jumbo frames) on dedicated segment.",
        RollbackDescription: "No network configuration changed.",
        Commands:
        [
            "netsh interface ipv4 show subinterfaces",
            "ping -f -l 1472 <gateway>"
        ]);

    public static Remediation Workload() => new(
        Id: "COPY_METHOD_TUNE",
        Title: "Tune copy method for workload",
        Description: "Small file workloads should use robocopy with parallelism.",
        RollbackDescription: "No configuration changes.",
        Commands: ["robocopy <source> <destination> /MT:16 /ZB /J"]);

    private static string Fmt(double bps)
        => bps >= 1_000_000 ? $"{bps / 1_000_000:F0} MB/s" : $"{bps / 1_000:F0} kB/s";
}
