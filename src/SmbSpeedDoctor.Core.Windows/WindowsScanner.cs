// Criado por André Santo (forg3) | junkyardgoodies.app
//
// Coletores Windows reais do SMB Speed Doctor.
// Fontes de dados: WMI (System.Management), System.Net.NetworkInformation.
// Degradação graciosa: falha de coleta nunca derruba o scan — registra em
// CollectionErrors e devolve valor neutro.
//
// Limitações conhecidas (documentadas, não escondidas):
// - Busy ratio de disco usa a instância agregada "_Total" do PhysicalDisk;
//   mapear letra (C:) -> instância exata exige correlação com PerfDisk_LogicalDisk.
// - Throughput bruto de rede é amostrado dos contadores Bytes Sent/Received
//   da interface num intervalo fixo; medição ativa (iperf-like) exige um par.
// - MTU: sem leitura nativa simples no .NET; usa 1500 como neutro.

using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Management;
using SmbSpeedDoctor.Core;

namespace SmbSpeedDoctor.Core.Windows;

/// <summary>Base comum: captura exceção, registra e devolve fallback.</summary>
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
            Errors.Add($"latência ({host}): {ex.Message}");
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
            Errors.Add($"perda ({host}): {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Amostra bytes enviados+recebidos da interface ativa por ~1s e converte
    /// para bits/s. É o throughput bruto observado — não é capacidade máxima.
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
            // NetworkInterface.Speed JÁ É bits/s (contrato .NET).
            // Saturação (ex.: -1 em int32 => 4294967295) e valores não-físicos são rejeitados:
            // devolvemos 0 (desconhecido) e o motor trata link ausente com segurança.
            var nic = FastestActiveInterface();
            if (nic == null) return 0;
            long bps = (long)nic.Speed;
            if (bps <= 0 || bps > 400_000_000_000L)
            {
                Errors.Add($"link speed implausível ({bps} bps) na interface {nic.Name} — tratado como desconhecido");
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
        // .NET não expõe MTU da interface de forma confiável multi-plataforma;
        // neutro 1500 (Ethernet padrão). Jumbo frames ficam para versão futura.
        return 1500;
    }

    private static string ResolveHost(string target)
        => target is "loopback" or "" ? "127.0.0.1" : target;

    private static NetworkInterface? FastestActiveInterface()
    {
        // Filtra loopback, túneis (Tailscale/VPN) e adaptadores virtuais comuns.
        // Sem esse filtro, o coletor pode reportar 100 Gb/s de um vSwitch.
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

        // Preferência: interface que tem gateway (rota default) — é a física de verdade.
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
                // RequireSecuritySignature: assinatura obrigatória no cliente.
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

    public string GetNegotiatedDialect(string server)
    {
        try
        {
            foreach (var o in SmbScope("MSFT_SmbConnection").Get())
            {
                var dialectObj = o.GetPropertyValue("Dialect");
                if (dialectObj != null)
                    return dialectObj.ToString() ?? "desconhecido";
            }
            return "sem conexão ativa";
        }
        catch (Exception ex)
        {
            Errors.Add($"dialeto: {ex.Message}");
            return "desconhecido";
        }
    }

    public bool IsMultichannelEnabled(string server)
    {
        try
        {
            foreach (var o in SmbScope("MSFT_SmbConnection").Get())
            {
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
            int count = 0;
            foreach (var _ in SmbScope("MSFT_SmbConnection").Get()) count++;
            return count;
        }
        catch (Exception ex)
        {
            Errors.Add($"canais: {ex.Message}");
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
            Errors.Add($"busy ratio disco: {ex.Message}");
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
            Errors.Add($"leitura disco: {ex.Message}");
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
            Errors.Add($"escrita disco: {ex.Message}");
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

public sealed class WindowsWorkloadAnalyzer : IWorkloadAnalyzer
{
    public List<string> Errors { get; } = new();

    public long GetAverageFileSize(string path)
    {
        try
        {
            var sizes = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f).Length);
            long sum = 0; int n = 0;
            foreach (var s in sizes) { sum += s; n++; }
            return n > 0 ? sum / n : 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            Errors.Add($"workload (acesso negado parcial): {ex.Message}");
            return 0;
        }
        catch (Exception ex)
        {
            Errors.Add($"workload: {ex.Message}");
            return 0;
        }
    }

    public int GetFileCount(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();
        }
        catch (Exception ex)
        {
            Errors.Add($"contagem arquivos: {ex.Message}");
            return 0;
        }
    }
}

/// <summary>
/// Scanner que orquestra todos os coletores reais. Nenhuma coleta pode
/// estourar exceção — cada uma degrada para valor neutro e registra erro.
/// </summary>
public sealed class WindowsScanner : IScanner
{
    /// <summary>Falhas parciais de coleta da última execução (para o JSON).</summary>
    public List<string> CollectionErrors { get; } = new();

    private readonly string _target;
    private readonly string _path;
    private readonly bool _noCopy;

    // Parâmetros anotados como nuláveis porque a CLI legitimamente passa null
    // quando --path é omitido. A normalização abaixo é a fronteira: a partir
    // daqui _target e _path nunca são nulos.
    public WindowsScanner(string? targetServer = "loopback", string? sharePath = "", bool noCopy = false)
    {
        // Item 1 (Solução B): normalização na fronteira — null vira neutro aqui,
        // então TODAS as referências internas a _path/_target ficam seguras.
        _target = targetServer ?? "loopback";
        _path = sharePath ?? string.Empty;
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

        // Disco: amostra durante a cópia observada; aqui faz duas leituras
        // espaçadas para ter valores formatados correntes.
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

        // Sem alvo não há workload a medir. Antes só GetFileCount tinha guarda:
        // GetAverageFileSize recebia null, estourava lá dentro e o catch amplo
        // registrava um erro espúrio ("Value cannot be null") no relatório.
        bool hasPath = _path.Length > 0;
        long avgFile = hasPath ? workload.GetAverageFileSize(_path) : 0;
        int fileCount = hasPath ? workload.GetFileCount(_path) : 0;

        CollectionErrors.AddRange(network.Errors);
        CollectionErrors.AddRange(smb.Errors);
        CollectionErrors.AddRange(disk.Errors);
        CollectionErrors.AddRange(cpu.Errors);
        CollectionErrors.AddRange(workload.Errors);

        // Item 3: cópia de teste real vs aproximação de NIC.
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
                "sem medição de throughput: cópia de teste não executada e tráfego de rede "
                + "abaixo do piso de credibilidade — use --path <compartilhamento> para medir");

        if (probeResult is not null && probeResult.Success)
            CollectionErrors.Add($"cópia de teste: {RealCopyProbe.Describe(probeResult)}");

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

    /// <summary>Duas amostras com intervalo para contadores formatados.</summary>
    private static double ReadDiskSteady(Func<double> probe)
    {
        _ = probe();
        Thread.Sleep(500);
        return probe();
    }

    /// <summary>
    /// Filtro de antivírus: presença de serviços conhecidos (Defender mínimo).
    /// Heurística leve — versão paga faria enumeração de minifiltros (fltmc).
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
    /// Cópia observada: aproximação pelo tráfego SMB medido na interface
    /// durante o scan. Versão completa mediria uma cópia real de arquivo
    /// teste no caminho alvo (requisito de escrita no destino).
    /// </summary>
    private static double EstimateObservedCopy(double rawNicBps)
        => rawNicBps / 8.0; // bits/s -> bytes/s como aproximação inicial
}
