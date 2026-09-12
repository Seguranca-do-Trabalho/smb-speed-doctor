// Author: forg3 | junkyardgoodies.app
using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using SmbSpeedDoctor.Core;

namespace SmbSpeedDoctor.Core.Windows;

/// <summary>
/// Operational decision of the scanner on how to obtain ObservedCopyThroughputBps.
/// </summary>
public enum CopyProbeDecision
{
    /// <summary>Real copy is viable and should be executed.</summary>
    RunRealCopy,
    /// <summary>Disabled by flag or unwriteable path; uses NIC traffic approximation.</summary>
    FallbackToApproximation,
}

/// <summary>
/// Result of a test copy attempt (probe).
/// </summary>
public sealed class CopyProbeResult
{
    public bool Success { get; }
    public long WriteBps { get; }
    public long ReadBps { get; }
    public long EffectiveBps { get; }
    public string? Error { get; }

    public CopyProbeResult(bool success, long writeBps, long readBps, long effectiveBps, string? error)
    {
        Success = success;
        WriteBps = writeBps;
        ReadBps = readBps;
        EffectiveBps = effectiveBps;
        Error = error;
    }

    public static CopyProbeResult Fail(string error)
        => new(false, 0, 0, 0, error);

    public static CopyProbeResult Ok(long writeBps, long readBps)
        => new(true, writeBps, readBps, Math.Min(writeBps, readBps), null);
}

/// <summary>
/// Creates, writes, and reads a fixed-size temporary file on the target
/// to measure real write and read rate (MB/s). The file is deleted
/// in finally, even on failure.
/// </summary>
public sealed class RealCopyProbe
{
    /// <summary>Total probe size — 256 MB. Overridden in tests to reduce runtime.</summary>
    public long TotalBytes { get; init; } = 256L * 1024 * 1024;

    private const int BufferSize = 1024 * 1024;   // 1 MB per chunk
    private const string ProbeFileName = "smb-speed-doctor-copy-probe.bin";
    private const int Seed = unchecked((int)0xDEAD_BEEF);         // reusable, deterministic

    /// <summary>
    /// Decides if real copy should run. Silently falls back to approximation when
    /// no path is defined or --no-copy flag is set. UNC paths ARE executed:
    /// the probe validates writeability in practice and degrades gracefully with logged error on failure.
    /// </summary>
    // targetPath is nullable: CLI omits --path and method handles it via IsNullOrWhiteSpace.
    public static CopyProbeDecision Decide(string? targetPath, bool realCopyDisabled)
    {
        if (realCopyDisabled) return CopyProbeDecision.FallbackToApproximation;
        if (string.IsNullOrWhiteSpace(targetPath)) return CopyProbeDecision.FallbackToApproximation;
        return CopyProbeDecision.RunRealCopy;
    }

    /// <summary>
    /// Credibility floor for NIC approximation (1 MB/s).
    ///
    /// Below this, what is being read is idle network background traffic,
    /// not a copy. Treating this as measurement caused the engine to conclude
    /// "SMB signing critical" and recommend disabling signing without evidence.
    /// </summary>
    public const double MinCredibleApproximationBps = 1_000_000;

    /// <summary>
    /// Origin of the value passed to <see cref="ScanData.ObservedCopyThroughputBps"/>.
    /// Only a successful real copy qualifies as <see cref="MeasurementQuality.Measured"/>.
    /// </summary>
    public static MeasurementQuality ResolveQuality(
        CopyProbeDecision decision, CopyProbeResult? probeResult, double approximationBps)
    {
        if (decision == CopyProbeDecision.RunRealCopy && probeResult is { Success: true })
            return MeasurementQuality.Measured;

        return approximationBps >= MinCredibleApproximationBps
            ? MeasurementQuality.Approximated
            : MeasurementQuality.Unavailable;
    }

    /// <summary>Resolves observed throughput, populating CollectionErrors when fallback occurs due to failure.</summary>
    public static double ResolveObservedCopyBps(
        CopyProbeDecision decision,
        CopyProbeResult? probeResult,
        double approximationBps,
        List<string> collectionErrors)
    {
        switch (decision)
        {
            case CopyProbeDecision.RunRealCopy when probeResult is { Success: true }:
                return probeResult.EffectiveBps;
            case CopyProbeDecision.RunRealCopy when probeResult is not null:
            {
                var msg = $"test copy failed ({probeResult.Error}): using NIC traffic approximation";
                collectionErrors.Add(msg);
                return approximationBps;
            }
            default:
                return approximationBps;
        }
    }

    /// <summary>
    /// Executes test copy: writes TotalBytes in 1 MB chunks (WriteThrough)
    /// and reads back, measuring MB/s for each. The file is deleted in finally.
    /// </summary>
    public CopyProbeResult Probe(string targetPath)
    {
        string probePath = Path.Combine(targetPath, ProbeFileName);
        long written = 0, read = 0;
        long writeBytes = 0, readBytes = 0;

        try
        {
            // ----- WRITE (with FlushToDisk/WriteThrough) -----
            using (var fs = new FileStream(probePath, FileMode.Create, FileAccess.Write, FileShare.None,
                       bufferSize: BufferSize,
                       FileOptions.WriteThrough))
            {
                var buffer = CreatePseudoRandomBuffer(BufferSize);
                long remaining = TotalBytes;
                while (remaining > 0)
                {
                    int toWrite = (int)Math.Min(BufferSize, remaining);
                    fs.Write(buffer, 0, toWrite);
                    written += toWrite;
                    remaining -= toWrite;
                }
                fs.Flush(true); // true = FlushToDisk (WriteThrough without extra FileOptions)
                writeBytes = written;
            }

            // ----- READ -----
            using (var fs = new FileStream(probePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                       bufferSize: BufferSize,
                       FileOptions.RandomAccess))
            {
                var buffer = new byte[BufferSize];
                long totalRead = 0;
                int n;
                while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    totalRead += n;
                    read += n;
                }
                readBytes = totalRead;
            }

            return CopyProbeResult.Ok(
                writeBps: writeBytes > 0 ? _bps(writeBytes) : 0,
                readBps: readBytes > 0 ? _bps(readBytes) : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or PathTooLongException or NotSupportedException or ArgumentException)
        {
            return CopyProbeResult.Fail(ex.Message);
        }
        finally
        {
            try { if (File.Exists(probePath)) File.Delete(probePath); }
            catch { /* already in error path */ }
        }
    }

    /// <summary>Test method exposed to verify written file size.</summary>
    public void WriteTestFile(string path, long totalBytes)
    {
        var buffer = CreatePseudoRandomBuffer(BufferSize);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                   bufferSize: BufferSize,
                   FileOptions.WriteThrough);
        long remaining = totalBytes;
        while (remaining > 0)
        {
            int toWrite = (int)Math.Min(BufferSize, remaining);
            fs.Write(buffer, 0, toWrite);
            remaining -= toWrite;
        }
        fs.Flush(true);
    }

    /// <summary>Pseudo-random, deterministic, reusable buffer — same seed, same sequence.</summary>
    public static byte[] CreatePseudoRandomBuffer(int length)
    {
        var buf = new byte[length];
        var span = buf.AsSpan();
        // Fills with derivative repeating patterns (deterministic, not uniform)
        var rng = new Random(Seed);
        long written = 0;
        while (written < length)
        {
            int toWrite = (int)Math.Min(length - written, sizeof(int));
            uint v = (uint)rng.Next();
            MemoryMarshal.Write(span[(int)written..], in v);
            written += sizeof(int);
        }
        return buf;
    }

    /// <summary>
    /// Formats bytes/s as DECIMAL MB/s (10^6), convention for network
    /// and storage throughput.
    /// </summary>
    public static string FormatMBps(double bps)
        => (bps / 1_000_000.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Human-readable description of the result (used in logs/errors).</summary>
    public static string Describe(CopyProbeResult r)
        => $"write={FormatMBps(r.WriteBps)} MB/s, read={FormatMBps(r.ReadBps)} MB/s, effective={FormatMBps(r.EffectiveBps)} MB/s";

    private static long _bps(long bytes)
        => (long)(bytes * 1.0 / (ProbeDurationMs / 1000.0));

    /// <summary>Measurement window duration (ms). Increase for higher precision on slow disks.</summary>
    internal static double ProbeDurationMs => 3000.0; // 3-second sample window
}
