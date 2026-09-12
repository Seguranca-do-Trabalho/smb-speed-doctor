// Created by forg3
// License: MIT
using SmbSpeedDoctor.Core;
using SmbSpeedDoctor.Core.Windows;
using Xunit;

namespace SmbSpeedDoctor.Tests;

/// <summary>
/// Real test copy. The decision (copy vs approximation), observed throughput
/// resolution, results formatting, and the copy itself (executed in local tmpdir — File I/O is cross-platform).
/// </summary>
public class RealCopyProbeTests : IDisposable
{
    private readonly string _tmpDir;

    public RealCopyProbeTests()
        => _tmpDir = Path.Combine(Path.GetTempPath(), $"smbdoctor-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    // ---------- Decision logic ----------

    [Fact]
    public void Without_defined_path_falls_back_to_approximation()
    {
        Assert.Equal(CopyProbeDecision.FallbackToApproximation,
            RealCopyProbe.Decide(targetPath: "", realCopyDisabled: false));
    }

    [Fact]
    public void No_copy_flag_disables_real_copy()
    {
        Assert.Equal(CopyProbeDecision.FallbackToApproximation,
            RealCopyProbe.Decide(targetPath: @"\\server\share", realCopyDisabled: true));
    }

    [Fact]
    public void Valid_target_path_executes_real_copy()
    {
        Assert.Equal(CopyProbeDecision.RunRealCopy,
            RealCopyProbe.Decide(targetPath: @"\\server\share", realCopyDisabled: false));
    }

    // ---------- Observed throughput resolution ----------

    [Fact]
    public void Successful_copy_populates_minimum_between_write_and_read()
    {
        var result = new CopyProbeResult(true, 200_000_000, 120_000_000, 120_000_000, null);
        var errors = new List<string>();

        double observed = RealCopyProbe.ResolveObservedCopyBps(
            CopyProbeDecision.RunRealCopy, result, approximationBps: 300_000_000, errors);

        Assert.Equal(120_000_000, observed); // practical ceiling = min(write, read)
        Assert.Empty(errors);
    }

    [Fact]
    public void Failed_copy_logs_reason_and_falls_back_to_approximation()
    {
        var result = CopyProbeResult.Fail("access denied when creating test file");
        var errors = new List<string>();

        double observed = RealCopyProbe.ResolveObservedCopyBps(
            CopyProbeDecision.RunRealCopy, result, approximationBps: 250_000_000, errors);

        Assert.Equal(250_000_000, observed);
        string reason = Assert.Single(errors);
        Assert.Contains("access denied", reason);
        Assert.Contains("approximation", reason);
    }

    [Fact]
    public void Fallback_by_decision_does_not_log_collection_error()
    {
        var errors = new List<string>();

        double observed = RealCopyProbe.ResolveObservedCopyBps(
            CopyProbeDecision.FallbackToApproximation, probeResult: null,
            approximationBps: 100_000_000, errors);

        Assert.Equal(100_000_000, observed);
        Assert.Empty(errors); // intentional disable is not a collection failure
    }

    // ---------- The copy itself (Linux local tmpdir) ----------

    [Fact]
    public void Copy_in_tmpdir_measures_write_and_read_and_deletes_file()
    {
        Directory.CreateDirectory(_tmpDir);

        var probe = new RealCopyProbe { TotalBytes = 8L * 1024 * 1024 }; // 8 MB for quick test
        var result = probe.Probe(_tmpDir);

        Assert.True(result.Success, $"expected success, got: {result.Error}");
        Assert.True(result.WriteBps > 0, "write MB/s must be > 0");
        Assert.True(result.ReadBps > 0, "read MB/s must be > 0");
        Assert.Equal(Math.Min(result.WriteBps, result.ReadBps), result.EffectiveBps);
        Assert.Null(result.Error);
        Assert.Empty(Directory.GetFiles(_tmpDir)); // test file deleted in finally
    }

    [Fact]
    public void Copy_in_nonexistent_path_fails_without_throwing_exception()
    {
        var nonexistent = Path.Combine(_tmpDir, "does-not-exist", "sub");

        var result = new RealCopyProbe { TotalBytes = 1024 * 1024 }.Probe(nonexistent);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(0, result.WriteBps);
        Assert.Equal(0, result.ReadBps);
    }

    [Fact]
    public void Written_file_has_requested_total_size()
    {
        Directory.CreateDirectory(_tmpDir);

        long total = 3L * 1024 * 1024;
        string dest = Path.Combine(_tmpDir, "manual-target");

        new RealCopyProbe().WriteTestFile(dest, total);

        Assert.Equal(total, new FileInfo(dest).Length);
    }

    // ---------- Buffer and formatting ----------

    [Fact]
    public void Pseudorandom_buffer_is_deterministic_and_matches_requested_size()
    {
        var b1 = RealCopyProbe.CreatePseudoRandomBuffer(64 * 1024);
        var b2 = RealCopyProbe.CreatePseudoRandomBuffer(64 * 1024);

        Assert.Equal(64 * 1024, b1.Length);
        Assert.Equal(b1, b2);                       // same seed => same content
        Assert.NotEqual(new byte[b1.Length], b1);   // not zeroed buffer
    }

    [Fact]
    public void FormatMBps_uses_invariant_culture()
    {
        // DECIMAL MB (10^6), not MiB: network throughput is expressed this way, and
        // JSON uses same base. Previously both bases coexisted in same report, both labeled "MB/s".
        Assert.Equal("10.00", RealCopyProbe.FormatMBps(10_000_000));
        Assert.Equal("0.50", RealCopyProbe.FormatMBps(500_000));

        // Unit is added by Describe, not numeric formatter.
        string described = RealCopyProbe.Describe(
            new CopyProbeResult(true, 10_000_000, 0, 0, null));
        Assert.Contains("10.00 MB/s", described);
    }

    [Fact]
    public void Describe_shows_write_read_and_effective_ceiling()
    {
        var result = new CopyProbeResult(true, 200_000_000, 100_000_000, 100_000_000, null);

        string s = RealCopyProbe.Describe(result);

        Assert.Contains("write", s);
        Assert.Contains("read", s);
        Assert.Contains("100.00 MB/s", s); // effective ceiling formatted
    }

    // ---------------------------------------------------------------------
    // Throughput provenance: separates "measured and found low" from "did not measure anything".
    // Without this the engine concluded bottleneck from idle NIC.
    // ---------------------------------------------------------------------

    [Fact]
    public void Successful_real_copy_is_Measured()
    {
        var ok = CopyProbeResult.Ok(120L * 1024 * 1024, 110L * 1024 * 1024);

        var q = RealCopyProbe.ResolveQuality(
            CopyProbeDecision.RunRealCopy, ok, approximationBps: 0);

        Assert.Equal(MeasurementQuality.Measured, q);
    }

    [Fact]
    public void Without_copy_and_with_idle_network_is_Unavailable()
    {
        // Exact bug scenario: `scan` without --path on idle network. NIC yields
        // ~886 B/s background traffic — this is NOT a copy measurement.
        var q = RealCopyProbe.ResolveQuality(
            CopyProbeDecision.FallbackToApproximation, probeResult: null, approximationBps: 886);

        Assert.Equal(MeasurementQuality.Unavailable, q);
    }

    [Fact]
    public void Without_copy_but_with_real_traffic_is_Approximated()
    {
        var q = RealCopyProbe.ResolveQuality(
            CopyProbeDecision.FallbackToApproximation, probeResult: null,
            approximationBps: 40L * 1024 * 1024);

        Assert.Equal(MeasurementQuality.Approximated, q);
    }

    [Fact]
    public void Failed_real_copy_does_not_become_Measured()
    {
        var failed = CopyProbeResult.Fail("access denied");

        var q = RealCopyProbe.ResolveQuality(
            CopyProbeDecision.RunRealCopy, failed, approximationBps: 886);

        Assert.NotEqual(MeasurementQuality.Measured, q);
        Assert.Equal(MeasurementQuality.Unavailable, q);
    }
}
