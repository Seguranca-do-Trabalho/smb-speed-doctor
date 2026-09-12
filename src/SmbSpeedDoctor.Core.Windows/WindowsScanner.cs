// Created by forg3
//
// Real Windows collectors for SMB Speed Doctor.
// Data sources: WMI (System.Management), System.Net.NetworkInformation.
// Graceful degradation: collection failure never crashes the scan — logs to
// CollectionErrors and returns neutral fallback value.
//
// Known limitations (documented, not hidden):
// - Disk busy ratio uses the aggregate instance "_Total" of PhysicalDisk;
//   mapping drive letter (C:) -> exact instance requires correlation with PerfDisk_LogicalDisk.
// - Raw network throughput is sampled from Bytes Sent/Received counters
//   of the interface in a fixed interval; active measurement (iperf-like) requires a peer.
// - MTU: no simple native reading in .NET; uses 1500 as neutral.

using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Management;
using SmbSpeedDoctor.Core;

namespace SmbSpeedDoctor.Core.Windows;

/// <summary>Common base: catches exception, logs and returns fallback.</summary>
internal abstract class CollectorBase
{
    protected List<string> Errors { get; } = new();

    protected T Safe<T>(string what, T fallback, Func<T> probe)
    {
        try { return probe(); }
        catch (Exception ex)
        {
            Errors.Add($"{what}: {ex.Message}");
            return fallback;
        }
    }
}

public sealed class WindowsNetworkCollector : INetworkCollector
{
    public List<string> Errors { get; } = new();

    private double PingTarget(string target, out double lossPct)
    {
        lossPct = 0;
        var latencies = new List<double>();
        int total = 20, lost = 0;

        for (int i = 0; i < total; i++)
        {
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(target, 1000);
                if (reply.Status == IPStatus.Success)
                    latencies.Add(reply.RoundtripTime);
                else
                    lost++;
            }
            catch (PingException) { lost++; }
        }

        lossPct = total > 0 ? (double)lost / total : 1.0;
        return latencies.Count > 0 ? latencies.Average() : 0;
    }

    public double GetLatencyMs(string target = "loopback")
    {
        string host = ResolveHost(target);
        try
        {
            double loss;
            double avg = PingTarget(host, out loss);
            Errors.Clear();
            return avg;
        }
        catch (Exception ex)
        {
            Errors.Add($"latency ({host}): {ex.Message}");
            return 0;
        }
    }

    public double GetPacketLossPct(string target)
    {
        string host = ResolveHost(target);
        try
        {
            double loss;
            PingTarget(host, out loss);
            return loss * 100.0;
        }
        catch (Exception ex)
        {
            Errors.Add($"loss ({host}): {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Samples bytes sent+received on the active interface for ~1s and converts
    /// to bits/s. This is the observed raw throughput — not maximum capacity.
    /// </summary>
    public double GetThroughputBps(string target = "loopback")
    {
        try
        {
            var nic = FastestActiveInterface();
            if (nic == null) return 0;

            long b1 = nic.GetIPStatistics().BytesSent + nic.GetIPStatistics().BytesReceived;
            var sw = Stopwatch.StartNew();
            Thread.Sleep(1000);
            sw.Stop();
            long b2 = nic.GetIPStatistics().BytesSent + nic.GetIPStatistics().BytesReceived;

            return Math.Max(0, (b2 - b1) * 8.0 / sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            Errors.Add($"throughput: {ex.Message}");
            return 0;
        }
    }

    public long GetLinkSpeedBps(string interfaceName = "")
    {
        try
        {
            // NetworkInterface.Speed ALREADY IS bits/s (.NET contract).
            // Saturation (e.g. -1 in int32 => 4294967295) and non-physical values are rejected:
            // we return 0 (unknown) and the engine handles missing link safely.
            var nic = FastestActiveInterface();
            if (nic == null) return 0;
            long bps = (long)nic.Speed;
            if (bps <= 0 || bps > 400_000_000_000L)
            {
                Errors.Add($"implausible link speed ({bps} bps) on interface {nic.Name} — treated as unknown");
                return 0;
            }
            return bps;
        }
        catch (Exception ex)
        {
            Errors.Add($"link speed: {ex.Message}");
            return 0;
        }
    }

    public int GetMtu(string interfaceName)
    {
        // .NET does not reliably expose interface MTU cross-platform;
        // neutral 1500 (standard Ethernet). Jumbo frames reserved for future version.
        return 1500;
    }

    private static string ResolveHost(string target)
        => target is "loopback" or "" ? "127.0.0.1" : target;

    private static NetworkInterface? FastestActiveInterface()
    {
        // Filter out loopback, tunnels (Tailscale/VPN), and common virtual adapters.
        // Without this filter, the collector may report 100 Gb/s from a vSwitch.
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                     && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                     && n.NetworkInterfaceType != NetworkInterfaceType.GenericModem)
            .Where(n =>
            {
                var name = n.Name.ToLowerInvariant();
                var desc = n.Description.ToLowerInvariant();
                bool virtualNic =
                    name.Contains("virtual") || name.Contains("vethernet")
                    || name.Contains("vmware") || name.Contains("hyper-v")
                    || name.Contains("wsa") || name.Contains("tailscale")
                    || desc.Contains("virtual") || desc.Contains("hyper-v")
                    || desc.Contains("vmware") || desc.Contains("virtualbox")
                    || desc.Contains("tap-") || desc.Contains("tailscale")
                    || desc.Contains("wireguard") || desc.Contains("openvpn");
                return !virtualNic;
            })
            .OrderByDescending(n => n.Speed)
            .ToList();

        // Preference: interface with a gateway (default route) — the actual physical NIC.
        var withGateway = candidates.FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Count > 0);
        return withGateway ?? candidates.FirstOrDefault();
    }
}

public sealed class WindowsSmbCollector : ISmbCollector
{
    public List<string> Errors { get; } = new();

    private ManagementObjectSearcher SmbScope(string wmiClass)
        => new(
            @"root\Microsoft\Windows\SMB",
            $"SELECT * FROM {wmiClass}");

    public bool IsSigningEnabled()
    {
        try
        {
            foreach (var o in SmbScope("MSFT_SmbClientConfiguration").Get())
            {
                // RequireSecuritySignature: mandatory signature on client.
                return Convert.ToBoolean(o.GetPropertyValue("RequireSecuritySignature"));
            }
            return false;
        }
        catch (Exception ex)
        {
            Errors.Add($"signing: {ex.Message}");
            return false;
        }
    }

    public bool IsEncryptionRequired()
    {
        try
        {
            foreach (var o in SmbScope("MSFT_SmbClientConfiguration").Get())
            {
                return Convert.ToBoolean(o.GetPropertyValue("EncryptData"));
            }
            return false;
        }
        catch (Exception ex)
        {
            Errors.Add($"encryption: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Does the WMI connection belong to the target server?
    ///
    /// Without this filter, collectors returned data from the FIRST SMB connection
    /// on the machine, whichever it was — the report would say "dialect 3.1.1" of
    /// the target when the number came from another server. Undefined target
    /// (loopback/local path) matches nothing: better to report "no active connection"
    /// than borrow data from another connection.
    /// </summary>
    private static bool IsTargetConnection(ManagementBaseObject o, string server)
    {
        if (string.IsNullOrWhiteSpace(server) || server is "loopback" or "127.0.0.1")
            return false;
        var name = o.GetPropertyValue("ServerName")?.ToString();
        return string.Equals(name, server, StringComparison.OrdinalIgnoreCase);
    }

    public string GetNegotiatedDialect(string server)
    {
        try
        {
            foreach (var o in SmbScope("MSFT_SmbConnection").Get())
            {
                if (!IsTargetConnection(o, server)) continue;
                var dialectObj = o.GetPropertyValue("Dialect");
                if (dialectObj != null)
                    return dialectObj.ToString() ?? "unknown";
            }
            return "no active connection";
        }
        catch (Exception ex)
        {
            Errors.Add($"dialect: {ex.Message}");
            return "unknown";
        }
    }

    public bool IsMultichannelEnabled(string server)
    {
        try
        {
            foreach (var o in SmbScope("MSFT_SmbConnection").Get())
            {
                if (!IsTargetConnection(o, server)) continue;
                var mc = o.GetPropertyValue("MultiChannel");
                if (mc != null)
                    return Convert.ToBoolean(mc);
            }
            return false;
        }
        catch (Exception ex)
        {
            Errors.Add($"multichannel: {ex.Message}");
            return false;
        }
    }

    public int GetChannelCount(string server)
    {
        try
        {
            // Only TARGET connections. Previously counted all SMB connections on the
            // machine: with two mounted servers, the target appeared with 2
            // channels and multichannel was evaluated against an invented number.
            int count = 0;
            foreach (var o in SmbScope("MSFT_SmbConnection").Get())
                if (IsTargetConnection(o, server)) count++;
            return count;
        }
        catch (Exception ex)
        {
            Errors.Add($"channels: {ex.Message}");
            return 1;
        }
    }
}

public sealed class WindowsDiskCollector : IDiskCollector
{
    public List<string> Errors { get; } = new();

    private static ManagementObjectCollection DiskPerf()
        => new ManagementObjectSearcher(
            @"root\cimv2",
            "SELECT * FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'")
            .Get();

    public double GetBusyRatio(string driveLetter)
    {
        try
        {
            foreach (var o in DiskPerf())
            {
                ushort pct = Convert.ToUInt16(o.GetPropertyValue("PercentDiskTime"));
                return pct / 100.0;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Errors.Add($"disk busy ratio: {ex.Message}");
            return 0;
        }
    }

    public long GetReadThroughputBps(string driveLetter)
    {
        try
        {
            foreach (var o in DiskPerf())
                return Convert.ToInt64(o.GetPropertyValue("DiskReadBytesPersec"));
            return 0;
        }
        catch (Exception ex)
        {
            Errors.Add($"disk read: {ex.Message}");
            return 0;
        }
    }

    public long GetWriteThroughputBps(string driveLetter)
    {
        try
        {
            foreach (var o in DiskPerf())
                return Convert.ToInt64(o.GetPropertyValue("DiskWriteBytesPersec"));
            return 0;
        }
        catch (Exception ex)
        {
            Errors.Add($"disk write: {ex.Message}");
            return 0;
        }
    }

    public bool IsOnSharePath(string path)
        => path.StartsWith(@"\\") || path.StartsWith("UNC");
}

public sealed class WindowsCpuCollector : ICpuCollector
{
    public List<string> Errors { get; } = new();

    public double GetUtilization()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2",
                "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'");
            foreach (var o in searcher.Get())
                return Convert.ToUInt16(o.GetPropertyValue("PercentProcessorTime")) / 100.0;
            return 0;
        }
        catch (Exception ex)
        {
            Errors.Add($"CPU: {ex.Message}");
            return 0;
        }
    }
}

/// <summary>
/// Workload sampling (average file size and file count) with limits.
///
/// The previous version performed TWO complete recursive scans of the target — one
/// in <c>GetAverageFileSize</c> (with a <c>new FileInfo(f).Length</c> per
/// file, i.e., an extra network roundtrip per file) and another in
/// <c>GetFileCount</c>. On a real SMB share this is unviable: measured at
/// <b>161 files/s</b> vs <b>43,831/s</b> on local disk (272× slower).
/// The scan hung for over 10 minutes without producing anything, stuck in
/// enumeration BEFORE measuring anything — the tool was never usable in
/// its intended use case.
///
/// Fixes: single pass (cached result for both metrics),
/// <see cref="DirectoryInfo.EnumerateFiles(string, SearchOption)"/> instead of
/// paths + <c>FileInfo</c> (size already comes from directory entry, no extra
/// call), and file cap + time budget.
/// </summary>
public sealed class WindowsWorkloadAnalyzer : IWorkloadAnalyzer
{
    /// <summary>Sampled files cap.</summary>
    public const int MaxFilesSampled = 25_000;

    /// <summary>Maximum time spent sampling, even if cap is not reached.</summary>
    public static readonly TimeSpan SampleTimeBudget = TimeSpan.FromSeconds(10);

    public List<string> Errors { get; } = new();

    private string? _cachedPath;
    private long _cachedAvg;
    private int _cachedCount;

    public long GetAverageFileSize(string path)
    {
        Sample(path);
        return _cachedAvg;
    }

    public int GetFileCount(string path)
    {
        Sample(path);
        return _cachedCount;
    }

    /// <summary>Single pass; both metrics come from the same result.</summary>
    private void Sample(string path)
    {
        if (_cachedPath == path) return;   // already sampled in this collection
        _cachedPath = path;
        _cachedAvg = 0;
        _cachedCount = 0;

        if (string.IsNullOrWhiteSpace(path)) return;

        var timer = System.Diagnostics.Stopwatch.StartNew();
        long sum = 0;
        int n = 0;
        bool truncated = false;

        try
        {
            // DirectoryInfo.EnumerateFiles returns FileInfo with Length already
            // populated from enumeration data — does not cost a network roundtrip
            // per file, unlike Directory.EnumerateFiles + new FileInfo.
            foreach (var fi in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { sum += fi.Length; }
                catch { /* file disappeared or no access: ignore this one, continue */ }
                n++;

                if (n >= MaxFilesSampled || timer.Elapsed > SampleTimeBudget)
                {
                    truncated = true;
                    break;
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Errors.Add($"workload (partial access denied): {ex.Message}");
        }
        catch (Exception ex)
        {
            Errors.Add($"workload: {ex.Message}");
        }

        _cachedCount = n;
        _cachedAvg = n > 0 ? sum / n : 0;

        if (truncated)
        {
            // Honesty: the count becomes a FLOOR, not a total. Anyone reading the
            // report needs to know the number was truncated.
            Errors.Add(
                $"workload: sampling stopped at {n} files after " +
                $"{timer.Elapsed.TotalSeconds:F1}s (cap {MaxFilesSampled} / " +
                $"{SampleTimeBudget.TotalSeconds:F0}s). Average size comes from sample; " +
                "count is a floor, not the total share content.");
        }
    }
}

/// <summary>
/// Scanner that orchestrates all real collectors. No collection may
/// throw an exception — each degrades to a neutral value and logs an error.
/// </summary>
public sealed class WindowsScanner : IScanner
{
    /// <summary>Partial collection errors from the last run (for JSON).</summary>
    public List<string> CollectionErrors { get; } = new();

    private readonly string _target;
    private readonly string _path;
    private readonly bool _noCopy;

    /// <summary>
    /// Is target IP on a subnet directly connected to any physical interface?
    /// If not, the path is ROUTED (VPN/tunnel/WAN) and local link speed does not
    /// limit the path.
    ///
    /// Returns null when cannot be determined (unresolved name, no IP).
    /// </summary>
    public static bool? IsTargetOnLocalSubnet(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || target is "loopback") return null;
        if (!System.Net.IPAddress.TryParse(target, out var ip)) return null;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;

        var targetBytes = ip.GetAddressBytes();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                                        or NetworkInterfaceType.Tunnel) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                var mask = ua.IPv4Mask;
                if (mask is null) continue;

                var loc = ua.Address.GetAddressBytes();
                var msk = mask.GetAddressBytes();
                bool same = true;
                for (int i = 0; i < 4 && same; i++)
                    same = (loc[i] & msk[i]) == (targetBytes[i] & msk[i]);
                if (same) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Logs that local link speed does NOT limit the path when target is routed.
    /// Efficiency rules divide throughput by LinkSpeed; for a target behind a VPN
    /// this compares tunnel rate with physical NIC speed — wrong denominator,
    /// which can either hide or invent a bottleneck.
    /// </summary>
    private void WarnIfTargetOutsideLocalLink(string target, long linkBps)
    {
        if (linkBps <= 0) return;
        var local = IsTargetOnLocalSubnet(target);
        if (local == false)
        {
            CollectionErrors.Add(
                $"target {target} is not on a directly connected subnet: the path is " +
                $"routed (VPN/tunnel/WAN). Local link speed " +
                $"({linkBps / 1_000_000.0:F0} Mb/s) does NOT limit this path — read " +
                "link-relative efficiency with this caveat.");
        }
    }

    /// <summary>
    /// Extracts server name from UNC path: <c>\\server\share</c> → <c>server</c>.
    /// Returns null for local path or empty string.
    /// </summary>
    public static string? ExtractServerFromUnc(string? sharePath)
    {
        if (string.IsNullOrWhiteSpace(sharePath)) return null;
        var p = sharePath.Trim();
        if (!p.StartsWith(@"\\") && !p.StartsWith("//")) return null;

        var remainder = p.Substring(2);
        int cut = remainder.IndexOfAny(new[] { '\\', '/' });
        var server = cut > 0 ? remainder.Substring(0, cut) : remainder;
        return string.IsNullOrWhiteSpace(server) ? null : server;
    }

    // Parameters annotated as nullable because CLI legitimately passes null
    // when --path is omitted. Normalization below is the boundary: from here
    // onward _target and _path are never null.
    public WindowsScanner(string? targetServer = "loopback", string? sharePath = "", bool noCopy = false)
    {
        // Normalization at boundary — null becomes neutral here,
        // so ALL internal references to _path/_target are safe.
        _path = sharePath ?? string.Empty;

        // TARGET IS EXTRACTED FROM SHARE ITSELF when not explicitly provided.
        //
        // CLI only passes sharePath; targetServer defaulted to "loopback" and
        // the ENTIRE network/SMB layer measured against 127.0.0.1: latency was 0 and
        // dialect/multichannel came from any other connection on the machine.
        // In other words, pointing to \\server\share did not make the scanner look at that server.
        var isExplicit = !string.IsNullOrWhiteSpace(targetServer) && targetServer != "loopback";
        _target = isExplicit
            ? targetServer!
            : (ExtractServerFromUnc(_path) ?? "loopback");

        _noCopy = noCopy;
    }

    public ScanData Collect()
    {
        CollectionErrors.Clear();

        var network = new WindowsNetworkCollector();
        var smb = new WindowsSmbCollector();
        var disk = new WindowsDiskCollector();
        var cpu = new WindowsCpuCollector();
        var workload = new WindowsWorkloadAnalyzer();

        // Disk: sample during observed copy; here makes two spaced readings
        // to get current formatted values.
        double busy = ReadDiskSteady(() => disk.GetBusyRatio(_path));
        long readBps = (long)ReadDiskSteady(() => disk.GetReadThroughputBps(_path));
        long writeBps = (long)ReadDiskSteady(() => disk.GetWriteThroughputBps(_path));

        double latency = network.GetLatencyMs(_target);
        double rawBps = network.GetThroughputBps(_target);
        double lossPct = network.GetPacketLossPct(_target);
        long linkBps = network.GetLinkSpeedBps();
        int mtu = network.GetMtu("");

        string dialect = smb.GetNegotiatedDialect(_target);
        bool signing = smb.IsSigningEnabled();
        bool encryption = smb.IsEncryptionRequired();
        bool multichannel = smb.IsMultichannelEnabled(_target);
        int channels = Math.Max(1, smb.GetChannelCount(_target));

        double cpuPct = cpu.GetUtilization();

        // Without target there is no workload to measure. Previously only GetFileCount had a guard:
        // GetAverageFileSize received null, threw internally, and the broad catch logged a spurious error ("Value cannot be null").
        bool hasPath = _path.Length > 0;
        long avgFile = hasPath ? workload.GetAverageFileSize(_path) : 0;
        int fileCount = hasPath ? workload.GetFileCount(_path) : 0;

        WarnIfTargetOutsideLocalLink(_target, linkBps);

        CollectionErrors.AddRange(network.Errors);
        CollectionErrors.AddRange(smb.Errors);
        CollectionErrors.AddRange(disk.Errors);
        CollectionErrors.AddRange(cpu.Errors);
        CollectionErrors.AddRange(workload.Errors);

        // Real test copy vs NIC approximation.
        var decision = RealCopyProbe.Decide(_path, _noCopy);
        var probe = new RealCopyProbe();
        var probeResult = decision == CopyProbeDecision.RunRealCopy
            ? probe.Probe(_path)
            : null;
        double approximation = EstimateObservedCopy(rawBps);
        double observedCopy = RealCopyProbe.ResolveObservedCopyBps(
            decision, probeResult, approximationBps: approximation, collectionErrors: CollectionErrors);
        var quality = RealCopyProbe.ResolveQuality(decision, probeResult, approximation);

        if (quality == MeasurementQuality.Unavailable)
            CollectionErrors.Add(
                "no throughput measurement: test copy not executed and network traffic "
                + "below credibility floor — use --path <share> to measure");

        if (probeResult is not null && probeResult.Success)
            CollectionErrors.Add($"test copy: {RealCopyProbe.Describe(probeResult)}");

        return new ScanData(
            LatencyMs: latency,
            RawThroughputBps: rawBps,
            MtuBytes: mtu,
            PacketLossRatio: lossPct / 100.0,
            LinkSpeedBps: linkBps,
            NegotiatedDialect: dialect,
            SigningEnabled: signing,
            EncryptionEnabled: encryption,
            Multichannel: multichannel,
            ActiveChannels: channels,
            CpuUtilization: cpuPct,
            SourceDiskBusyRatio: busy,
            TargetDiskBusyRatio: busy,
            SourceDiskReadBps: readBps,
            TargetDiskWriteBps: writeBps,
            AvFilterOnSharePath: DetectAvFilter(),
            ObservedCopyThroughputBps: observedCopy,
            AverageFileBytes: avgFile,
            FileCount: fileCount,
            ThroughputQuality: quality);
    }

    /// <summary>Two samples with interval for formatted counters.</summary>
    private static double ReadDiskSteady(Func<double> probe)
    {
        _ = probe();
        Thread.Sleep(500);
        return probe();
    }

    /// <summary>
    /// Antivirus filter: presence of known services (Defender minimum).
    /// Lightweight heuristic — full version would enumerate minifilters (fltmc).
    /// </summary>
    private static bool DetectAvFilter()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2",
                "SELECT DisplayName, State FROM Win32_Service WHERE State='Running'");
            foreach (var o in searcher.Get())
            {
                var name = o.GetPropertyValue("DisplayName")?.ToString() ?? "";
                if (name.Contains("Antimalware", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Antivirus", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Observed copy: approximation by SMB traffic measured on interface
    /// during scan. Full version would measure real test file copy
    /// at target path (requires write permission at destination).
    /// </summary>
    private static double EstimateObservedCopy(double rawNicBps)
        => rawNicBps / 8.0; // bits/s -> bytes/s as initial approximation
}
