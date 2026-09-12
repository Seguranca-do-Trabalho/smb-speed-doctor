// Created by forg3
// License: MIT
//
// Tests for profiled robocopy generator.

using Xunit;
using SmbSpeedDoctor.Core;
using SmbSpeedDoctor.Cli;

namespace SmbSpeedDoctor.Tests;

public class RobocopyBuilderTests
{
    private static ScanData Scan(
        int fileCount = 10,
        double avgBytes = 512L * 1024 * 1024,
        double loss = 0.0,
        double latency = 5,
        bool signing = true,
        long observed = 38_000_000)
        => new(
            LatencyMs: latency,
            RawThroughputBps: 2_400_000_000,
            MtuBytes: 1500,
            PacketLossRatio: loss,
            LinkSpeedBps: 2_500_000_000,
            NegotiatedDialect: "3.1.1",
            SigningEnabled: signing,
            EncryptionEnabled: false,
            Multichannel: false,
            ActiveChannels: 1,
            CpuUtilization: 0.15,
            SourceDiskBusyRatio: 0.2,
            TargetDiskBusyRatio: 0.25,
            SourceDiskReadBps: 2_000_000_000,
            TargetDiskWriteBps: 2_000_000_000,
            AvFilterOnSharePath: false,
            ObservedCopyThroughputBps: observed,
            AverageFileBytes: avgBytes,
            FileCount: fileCount);

    private static DiagnosisResult Diag(ScanData d)
        => new("s", Bottleneck.SmbSigning, Severity.Warning, 60, Array.Empty<Finding>(), null,
               new CopyMethodProfile("x", "y"));

    [Fact]
    public void Large_files_few_uses_J_without_MT()
    {
        var plan = RobocopyBuilder.Build(Scan(fileCount: 10), Diag(null!));

        Assert.Contains("/J", plan.MethodName);
        Assert.DoesNotContain("/MT:", plan.MethodName);
        Assert.Contains("unbuffered", plan.Rationale);
    }

    [Fact]
    public void Many_small_files_uses_parallel_MT()
    {
        var plan = RobocopyBuilder.Build(
            Scan(fileCount: 50_000, avgBytes: 48 * 1024), Diag(null!));

        Assert.Contains("/MT:", plan.MethodName);
        Assert.Contains("parallelizes seeks", plan.Rationale);
        Assert.True(plan.EstimatedThroughputMBps > 0); // populated estimate
    }

    [Fact]
    public void Unstable_link_adds_ZB()
    {
        var loss = RobocopyBuilder.Build(Scan(loss: 0.02), Diag(null!));
        Assert.Contains("/ZB", loss.MethodName);
        Assert.Contains("/ZB restartable", loss.Rationale);

        var lat = RobocopyBuilder.Build(Scan(latency: 80), Diag(null!));
        Assert.Contains("/ZB", lat.MethodName);
    }

    [Fact]
    public void Signing_and_encryption_appear_in_rationale()
    {
        var plan = RobocopyBuilder.Build(Scan(signing: true), Diag(null!));
        Assert.Contains("SMB signing active", plan.Rationale);
    }

    [Fact]
    public void Estimate_respects_observed_copy_ceiling()
    {
        // Observed copy of ~38 MB/s must limit estimate even with 2.5G link
        var plan = RobocopyBuilder.Build(Scan(observed: 38_000_000), Diag(null!));
        Assert.InRange(plan.EstimatedThroughputMBps, 30.0, 40.0);
    }
}
