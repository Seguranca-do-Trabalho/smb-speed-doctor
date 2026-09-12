// Created by forg3
// License: MIT
using Xunit;
using SmbSpeedDoctor.Core;

namespace SmbSpeedDoctor.Tests;

/// <summary>
/// Tests for correlation engine. Each scenario reproduces a real field profile:
/// SMB signing on 24H2, slow disk, fragmented MTU, many small files workload, etc.
/// </summary>
public class DiagnosisEngineTests
{
    private static ScanData Baseline => new(
        LatencyMs: 0.4,
        RawThroughputBps: 2_400_000_000,   // 2.5 GbE ~ saturated
        MtuBytes: 1500,
        PacketLossRatio: 0.0,
        LinkSpeedBps: 2_500_000_000,
        NegotiatedDialect: "3.1.1",
        SigningEnabled: true,
        EncryptionEnabled: false,
        Multichannel: false,
        ActiveChannels: 1,
        CpuUtilization: 0.15,
        SourceDiskBusyRatio: 0.20,
        TargetDiskBusyRatio: 0.25,
        SourceDiskReadBps: 2_000_000_000,
        TargetDiskWriteBps: 2_000_000_000,
        AvFilterOnSharePath: false,
        ObservedCopyThroughputBps: 38_000_000, // ~38 MB/s (BYTES/s) — SMB copy collapses on 24H2 with signing
        AverageFileBytes: 512L * 1024 * 1024,
        FileCount: 10,
        // These scenarios describe ACTUALLY measured copies; without this the engine
        // (correctly) refuses to conclude anything about throughput.
        ThroughputQuality: MeasurementQuality.Measured);

    [Fact]
    public void Healthy_profile_indicates_no_bottleneck()
    {
        var healthy = Baseline with { ObservedCopyThroughputBps = 290_000_000 };
        var result = new DiagnosisEngine().Diagnose(healthy);

        Assert.Equal(Bottleneck.None, result.Dominant);
        Assert.Equal(Severity.Ok, result.Severity);
        Assert.Contains("no dominant bottleneck", result.OneLineSummary);
        Assert.Equal(0, ExitCodeMapper.For(result));
    }

    [Fact]
    public void Smb_signing_with_saturated_network_is_dominant_bottleneck()
    {
        // 24H2 scenario: network delivers ~2 Gb/s but SMB copy drops to 38 MB/s.
        var result = new DiagnosisEngine().Diagnose(Baseline);

        Assert.Equal(Bottleneck.SmbSigning, result.Dominant);
        Assert.Equal(Severity.Critical, result.Severity);
        Assert.Equal(2, ExitCodeMapper.For(result)); // exit code for bottleneck in RMM
        Assert.Contains("signing", result.OneLineSummary.ToLowerInvariant());
        Assert.NotNull(result.RecommendedRemediation);
        // Every remediation carries an explicit rollback (product requirement).
        Assert.False(string.IsNullOrWhiteSpace(result.RecommendedRemediation!.RollbackDescription));
    }

    [Fact]
    public void Smb_encryption_supersedes_signing_when_active()
    {
        var data = Baseline with { EncryptionEnabled = true };
        var result = new DiagnosisEngine().Diagnose(data);

        Assert.Equal(Bottleneck.SmbEncryption, result.Dominant);
    }

    [Fact]
    public void Slow_target_disk_outweighs_smb_cause()
    {
        var data = Baseline with
        {
            TargetDiskWriteBps = 90_000_000,          // ~90 MB/s (HDD)
            TargetDiskBusyRatio = 0.98,
            ObservedCopyThroughputBps = 85_000_000,
        };
        var result = new DiagnosisEngine().Diagnose(data);

        Assert.Equal(Bottleneck.DiskTarget, result.Dominant);
    }

    [Fact]
    public void Many_small_files_explains_low_throughput()
    {
        var data = Baseline with
        {
            AverageFileBytes = 48 * 1024,
            FileCount = 50_000,
            ObservedCopyThroughputBps = 60_000_000,
        };
        var result = new DiagnosisEngine().Diagnose(data);

        Assert.Equal(Bottleneck.Workload, result.Dominant);
        Assert.Contains("small files", result.OneLineSummary);
    }

    [Fact]
    public void Packet_loss_is_reported_as_network_bottleneck()
    {
        var data = Baseline with { PacketLossRatio = 0.03, ObservedCopyThroughputBps = 120_000_000 };
        var result = new DiagnosisEngine().Diagnose(data);

        Assert.Equal(Bottleneck.Network, result.Dominant);
    }

    [Fact]
    public void Smb1_dialect_is_blocking()
    {
        var data = Baseline with { NegotiatedDialect = "1.0" };
        var result = new DiagnosisEngine().Diagnose(data);

        var f = Assert.Single(result.Findings, x => x.Metric == "NegotiatedDialect");
        Assert.Equal(Severity.Critical, f.Severity);
    }

    [Fact]
    public void Copy_method_for_large_files_is_unbuffered()
    {
        var result = new DiagnosisEngine().Diagnose(Baseline);
        Assert.Contains("/J", result.RecommendedMethod.Rationale);
    }

    [Fact]
    public void Copy_method_for_many_small_files_is_parallel()
    {
        var data = Baseline with
        {
            AverageFileBytes = 32 * 1024,
            FileCount = 80_000,
            ObservedCopyThroughputBps = 40_000_000,
        };
        var result = new DiagnosisEngine().Diagnose(data);
        Assert.Equal("parallel robocopy (/MT)", result.RecommendedMethod.MethodName);
    }

    // ---------------------------------------------------------------------
    // Field regression: `scan` WITHOUT --path does not execute test copy.
    // Throughput came from idle NIC (~886 B/s) and engine concluded "critical SMB signing"
    // (exit 2), recommending DISABLING signing — a security downgrade backed by no measurement.
    // ---------------------------------------------------------------------

    private static ScanData NoMeasurement => Baseline with
    {
        ObservedCopyThroughputBps = 886,   // actual value observed in bug
        ThroughputQuality = MeasurementQuality.Unavailable,
    };

    [Fact]
    public void Without_real_measurement_does_not_accuse_signing_as_bottleneck()
    {
        var result = new DiagnosisEngine().Diagnose(NoMeasurement);

        Assert.NotEqual(Bottleneck.SmbSigning, result.Dominant);
        Assert.NotEqual(Severity.Critical, result.Severity);
        Assert.NotEqual(2, ExitCodeMapper.For(result));
    }

    [Fact]
    public void Without_real_measurement_does_not_recommend_disabling_signing()
    {
        var result = new DiagnosisEngine().Diagnose(NoMeasurement);

        // No security downgrade recommendation without evidence.
        Assert.Null(result.RecommendedRemediation);
    }

    [Fact]
    public void Without_real_measurement_declares_limitation_and_lowers_confidence()
    {
        var result = new DiagnosisEngine().Diagnose(NoMeasurement);

        Assert.Contains(result.Findings, f => f.Metric == "ThroughputQuality");
        // Absence of evidence is not evidence of absence: cannot claim 95%.
        Assert.True(result.ConfidencePct < 95,
            $"confidence should drop without measurement, got {result.ConfidencePct}");
    }

    [Fact]
    public void Without_real_measurement_still_reports_findings_independent_of_throughput()
    {
        // Packet loss is directly measured — does not depend on copy. Quick scan
        // remains useful; it just does not conclude what requires throughput.
        var data = NoMeasurement with { PacketLossRatio = 0.03 };
        var result = new DiagnosisEngine().Diagnose(data);

        Assert.Equal(Bottleneck.Network, result.Dominant);
    }

    [Fact]
    public void Unknown_dialect_is_not_interpreted_as_modern()
    {
        // Fail-open: missing data became positive verdict ("modern dialect").
        var data = Baseline with { NegotiatedDialect = "unknown" };
        var result = new DiagnosisEngine().Diagnose(data);

        var f = Assert.Single(result.Findings, x => x.Metric == "NegotiatedDialect");
        Assert.DoesNotContain("modern", f.Interpretation);
    }

    [Fact]
    public void Disabled_multichannel_is_not_counted_as_signing()
    {
        // Multichannel finding previously added to SmbSigning score: report
        // listed one problem and blamed another.
        var data = Baseline with
        {
            SigningEnabled = false,        // isolates multichannel effect
            Multichannel = false,
            ActiveChannels = 1,
            ObservedCopyThroughputBps = 38_000_000,
        };
        var result = new DiagnosisEngine().Diagnose(data);

        Assert.Equal(Bottleneck.SmbMultichannel, result.Dominant);
        Assert.NotEqual(Bottleneck.SmbSigning, result.Dominant);
    }
}
