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

    /// <summary>
    /// A conexão WMI pertence ao servidor alvo?
    ///
    /// Sem este filtro, os coletores devolviam dados da PRIMEIRA conexão SMB da
    /// máquina, qualquer que fosse ela — o relatório dizia "dialeto 3.1.1" do
    /// alvo quando o número vinha de outro servidor. Alvo indefinido
    /// (loopback/caminho local) não casa com nada: melhor reportar "sem conexão
    /// ativa" do que emprestar o dado de outra conexão.
    /// </summary>
    private static bool ConexaoDoAlvo(ManagementBaseObject o, string server)
    {
        if (string.IsNullOrWhiteSpace(server) || server is "loopback" or "127.0.0.1")
            return false;
        var nome = o.GetPropertyValue("ServerName")?.ToString();
        return string.Equals(nome, server, StringComparison.OrdinalIgnoreCase);
    }

    public string GetNegotiatedDialect(string server)
    {
        try
        {
            foreach (var o in SmbScope("MSFT_SmbConnection").Get())
            {
                if (!ConexaoDoAlvo(o, server)) continue;
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
                if (!ConexaoDoAlvo(o, server)) continue;
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
            // Só as conexões DO ALVO. Antes contava todas as conexões SMB da
            // máquina: com dois servidores montados, o alvo aparecia com 2
            // canais e o multichannel era avaliado sobre um número inventado.
            int count = 0;
            foreach (var o in SmbScope("MSFT_SmbConnection").Get())
                if (ConexaoDoAlvo(o, server)) count++;
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

/// <summary>
/// Amostragem de workload (tamanho médio e contagem de arquivos) com limites.
///
/// A versão anterior fazia DUAS varreduras recursivas completas do alvo — uma
/// em <c>GetAverageFileSize</c> (com um <c>new FileInfo(f).Length</c> por
/// arquivo, ou seja, uma ida à rede a mais por arquivo) e outra em
/// <c>GetFileCount</c>. Num share SMB real isso é inviável: medido em
/// <b>161 arquivos/s</b> contra <b>43.831/s</b> em disco local (272× mais
/// lento). O scan ficava mais de 10 minutos sem produzir nada, travado na
/// enumeração ANTES de medir qualquer coisa — a ferramenta nunca foi usável no
/// caso de uso a que se destina.
///
/// Correções: uma única passagem (resultado em cache para as duas métricas),
/// <see cref="DirectoryInfo.EnumerateFiles(string, SearchOption)"/> em vez de
/// caminhos + <c>FileInfo</c> (o tamanho já vem do dado de diretório, sem
/// chamada extra), e teto de arquivos + orçamento de tempo.
/// </summary>
public sealed class WindowsWorkloadAnalyzer : IWorkloadAnalyzer
{
    /// <summary>Teto de arquivos amostrados.</summary>
    public const int MaxFilesSampled = 25_000;

    /// <summary>Tempo máximo gasto amostrando, mesmo sem atingir o teto.</summary>
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

    /// <summary>Uma passagem só; as duas métricas saem do mesmo resultado.</summary>
    private void Sample(string path)
    {
        if (_cachedPath == path) return;   // já amostrado nesta coleta
        _cachedPath = path;
        _cachedAvg = 0;
        _cachedCount = 0;

        if (string.IsNullOrWhiteSpace(path)) return;

        var relogio = System.Diagnostics.Stopwatch.StartNew();
        long soma = 0;
        int n = 0;
        bool truncado = false;

        try
        {
            // EnumerateFiles do DirectoryInfo devolve FileInfo com Length já
            // preenchido pelo dado da enumeração — não custa uma ida à rede por
            // arquivo, ao contrário de Directory.EnumerateFiles + new FileInfo.
            foreach (var fi in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { soma += fi.Length; }
                catch { /* arquivo sumiu ou sem acesso: ignora este, segue */ }
                n++;

                if (n >= MaxFilesSampled || relogio.Elapsed > SampleTimeBudget)
                {
                    truncado = true;
                    break;
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Errors.Add($"workload (acesso negado parcial): {ex.Message}");
        }
        catch (Exception ex)
        {
            Errors.Add($"workload: {ex.Message}");
        }

        _cachedCount = n;
        _cachedAvg = n > 0 ? soma / n : 0;

        if (truncado)
        {
            // Honestidade: a contagem passa a ser PISO, não total. Quem lê o
            // relatório precisa saber que o número foi cortado.
            Errors.Add(
                $"workload: amostragem interrompida em {n} arquivos após " +
                $"{relogio.Elapsed.TotalSeconds:F1}s (teto {MaxFilesSampled} / " +
                $"{SampleTimeBudget.TotalSeconds:F0}s). Tamanho médio vem da amostra; " +
                "a contagem é um piso, não o total do compartilhamento.");
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

    /// <summary>
    /// O IP do alvo está numa sub-rede diretamente conectada a alguma interface
    /// física? Se não, o caminho é ROTEADO (VPN/túnel/WAN) e a velocidade do
    /// enlace local não limita o percurso.
    ///
    /// Devolve null quando não dá para afirmar (nome não resolvido, sem IP).
    /// </summary>
    public static bool? AlvoNaSubredeLocal(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || target is "loopback") return null;
        if (!System.Net.IPAddress.TryParse(target, out var ip)) return null;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;

        var alvo = ip.GetAddressBytes();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                                        or NetworkInterfaceType.Tunnel) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                var mascara = ua.IPv4Mask;
                if (mascara is null) continue;

                var loc = ua.Address.GetAddressBytes();
                var msk = mascara.GetAddressBytes();
                bool mesma = true;
                for (int i = 0; i < 4 && mesma; i++)
                    mesma = (loc[i] & msk[i]) == (alvo[i] & msk[i]);
                if (mesma) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Registra que a velocidade do enlace local NÃO limita o caminho quando o
    /// alvo é roteado. As regras de eficiência dividem throughput pela
    /// LinkSpeed; num alvo atrás de VPN isso compara a taxa do túnel com a
    /// velocidade da NIC física — denominador errado, que pode tanto esconder
    /// um gargalo quanto inventar um.
    /// </summary>
    private void AvisarSeAlvoForaDoEnlaceLocal(string target, long linkBps)
    {
        if (linkBps <= 0) return;
        var local = AlvoNaSubredeLocal(target);
        if (local == false)
        {
            CollectionErrors.Add(
                $"alvo {target} não está numa sub-rede diretamente conectada: o caminho é " +
                $"roteado (VPN/túnel/WAN). A velocidade do enlace local " +
                $"({linkBps / 1_000_000.0:F0} Mb/s) NÃO limita esse percurso — leia a " +
                "eficiência relativa ao enlace com essa ressalva.");
        }
    }

    /// <summary>
    /// Extrai o servidor de um caminho UNC: <c>\\servidor\share</c> → <c>servidor</c>.
    /// Devolve null para caminho local ou vazio.
    /// </summary>
    public static string? ExtractServerFromUnc(string? sharePath)
    {
        if (string.IsNullOrWhiteSpace(sharePath)) return null;
        var p = sharePath.Trim();
        if (!p.StartsWith(@"\\") && !p.StartsWith("//")) return null;

        var resto = p.Substring(2);
        int corte = resto.IndexOfAny(new[] { '\\', '/' });
        var servidor = corte > 0 ? resto.Substring(0, corte) : resto;
        return string.IsNullOrWhiteSpace(servidor) ? null : servidor;
    }

    // Parâmetros anotados como nuláveis porque a CLI legitimamente passa null
    // quando --path é omitido. A normalização abaixo é a fronteira: a partir
    // daqui _target e _path nunca são nulos.
    public WindowsScanner(string? targetServer = "loopback", string? sharePath = "", bool noCopy = false)
    {
        // Item 1 (Solução B): normalização na fronteira — null vira neutro aqui,
        // então TODAS as referências internas a _path/_target ficam seguras.
        _path = sharePath ?? string.Empty;

        // O ALVO SAI DO PRÓPRIO SHARE quando não foi informado explicitamente.
        //
        // A CLI só passa sharePath; targetServer ficava no default "loopback" e
        // TODA a camada de rede/SMB media contra 127.0.0.1: latência dava 0 e o
        // dialeto/multichannel vinham de outra conexão qualquer da máquina. Ou
        // seja, apontar para \\servidor\share não fazia o scanner olhar para
        // esse servidor.
        var explicito = !string.IsNullOrWhiteSpace(targetServer) && targetServer != "loopback";
        _target = explicito
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

        AvisarSeAlvoForaDoEnlaceLocal(_target, linkBps);

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
