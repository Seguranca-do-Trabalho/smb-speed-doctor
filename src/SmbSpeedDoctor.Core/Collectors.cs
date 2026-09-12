namespace SmbSpeedDoctor.Core;

/// <summary>
/// Abstract collector interface — allows full mocking in unit tests
/// and real adapters on Windows.
/// </summary>
public interface IScanner
{
    ScanData Collect();
}

/// <summary>
/// Contracts for collection layers.
/// </summary>
public interface INetworkCollector
{
    double GetLatencyMs(string target = "loopback");
    double GetThroughputBps(string target = "loopback");
    int GetMtu(string interfaceName);
    double GetPacketLossPct(string target);
    long GetLinkSpeedBps(string interfaceName);
}

public interface ISmbCollector
{
    string GetNegotiatedDialect(string server);
    bool IsSigningEnabled();
    bool IsEncryptionRequired();
    bool IsMultichannelEnabled(string server);
    int GetChannelCount(string server);
}

public interface IDiskCollector
{
    double GetBusyRatio(string driveLetter);
    long GetReadThroughputBps(string driveLetter);
    long GetWriteThroughputBps(string driveLetter);
    bool IsOnSharePath(string path);
}

public interface ICpuCollector
{
    double GetUtilization();
}

public interface IWorkloadAnalyzer
{
    long GetAverageFileSize(string path);
    int GetFileCount(string path);
}

/// <summary>
/// Collector mocks for unit tests.
/// </summary>
public sealed class MockNetworkCollector : INetworkCollector
{
    public double LatencyMs { get; set; }
    public double ThroughputBps { get; set; }
    public int MtuBytes { get; set; }
    public double PacketLossPct { get; set; }
    public long LinkSpeedBps { get; set; }

    public double GetLatencyMs(string target = "loopback") => LatencyMs;
    public double GetThroughputBps(string target = "loopback") => ThroughputBps;
    public int GetMtu(string interfaceName) => MtuBytes;
    public double GetPacketLossPct(string target) => PacketLossPct;
    public long GetLinkSpeedBps(string interfaceName) => LinkSpeedBps;
}

public sealed class MockSmbCollector : ISmbCollector
{
    public string Dialect { get; set; } = "3.1.1";
    public bool Signing { get; set; }
    public bool Encryption { get; set; }
    public bool Multichannel { get; set; }
    public int Channels { get; set; }

    public string GetNegotiatedDialect(string server) => Dialect;
    public bool IsSigningEnabled() => Signing;
    public bool IsEncryptionRequired() => Encryption;
    public bool IsMultichannelEnabled(string server) => Multichannel;
    public int GetChannelCount(string server) => Channels;
}

public sealed class MockDiskCollector : IDiskCollector
{
    public double BusyRatio { get; set; }
    public long ReadBps { get; set; }
    public long WriteBps { get; set; }
    public bool OnSharePath { get; set; }

    public double GetBusyRatio(string driveLetter) => BusyRatio;
    public long GetReadThroughputBps(string driveLetter) => ReadBps;
    public long GetWriteThroughputBps(string driveLetter) => WriteBps;
    public bool IsOnSharePath(string path) => OnSharePath;
}

public sealed class MockCpuCollector : ICpuCollector
{
    public double Utilization { get; set; }
    public double GetUtilization() => Utilization;
}

public sealed class MockWorkloadAnalyzer : IWorkloadAnalyzer
{
    public long AvgSize { get; set; }
    public int Count { get; set; }
    public long GetAverageFileSize(string path) => AvgSize;
    public int GetFileCount(string path) => Count;
}
