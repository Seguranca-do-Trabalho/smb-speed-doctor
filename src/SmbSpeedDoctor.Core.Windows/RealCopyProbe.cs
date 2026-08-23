// Criado por André Santo (forg3) | junkyardgoodies.app
using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using SmbSpeedDoctor.Core;

namespace SmbSpeedDoctor.Core.Windows;

/// <summary>
/// Decisão operacional do scanner sobre como obter ObservedCopyThroughputBps.
/// </summary>
public enum CopyProbeDecision
{
    /// <summary>A cópia real está viável e deve ser executada.</summary>
    RunRealCopy,
    /// <summary>Desativada por flag ou caminho não gravável; usa aproximação de NIC.</summary>
    FallbackToApproximation,
}

/// <summary>
/// Resultado de uma tentativa de cópia de teste (probe).
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
/// Cria, escreve e lê um arquivo temporário de tamanho fixo no destino
/// para medir a taxa real de escrita e leitura (MB/s). O arquivo é
/// eliminado no finally, mesmo em caso de falha.
/// </summary>
public sealed class RealCopyProbe
{
    /// <summary>Tamanho total do probe — 256 MB. Alterado nos testes p/ reduzir tempo.</summary>
    public long TotalBytes { get; init; } = 256L * 1024 * 1024;

    private const int BufferSize = 1024 * 1024;   // 1 MB por chunk
    private const string ProbeFileName = "smb-speed-doctor-copy-probe.bin";
    private const int Seed = unchecked((int)0xDEAD_BEEF);         // reutilizável, determinístico

    /// <summary>
    /// Decide se a cópia real deve ser executada. Falha silenciosa para aproximação quando
    /// não há path definido ou a flag --no-copy está ativa. Caminhos UNC SÃO executados:
    /// a probe valida escritabilidade na prática e degrada com erro registrado se falhar.
    /// </summary>
    // targetPath é nulável de fato: a CLI omite --path e o método já trata isso
    // com IsNullOrWhiteSpace. Declarar como não-nulável era a anotação errada.
    public static CopyProbeDecision Decide(string? targetPath, bool realCopyDisabled)
    {
        if (realCopyDisabled) return CopyProbeDecision.FallbackToApproximation;
        if (string.IsNullOrWhiteSpace(targetPath)) return CopyProbeDecision.FallbackToApproximation;
        return CopyProbeDecision.RunRealCopy;
    }

    /// <summary>
    /// Piso de credibilidade da aproximação por NIC (1 MB/s).
    ///
    /// Abaixo disso o que se está lendo é tráfego de fundo de uma rede ociosa,
    /// não uma cópia. Tratar isso como medição fazia o motor concluir
    /// "assinatura SMB crítica" e recomendar desligar a assinatura sem evidência.
    /// </summary>
    public const double MinCredibleApproximationBps = 1_000_000;

    /// <summary>
    /// Procedência do número que vai para <see cref="ScanData.ObservedCopyThroughputBps"/>.
    /// Só a cópia real bem-sucedida vale como <see cref="MeasurementQuality.Measured"/>.
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

    /// <summary>Resolve o throughput observado, populando CollectionErrors quando houver fallback por falha.</summary>
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
                var msg = $"cópia de teste falhou ({probeResult.Error}): usando aproximação de tráfego NIC";
                collectionErrors.Add(msg);
                return approximationBps;
            }
            default:
                return approximationBps;
        }
    }

    /// <summary>
    /// Executa a cópia de teste: escreve TotalBytes em chunks de 1 MB (WriteThrough no write)
    /// e lê de volta, medindo MB/s de cada. O arquivo é apagado no finally.
    /// </summary>
    public CopyProbeResult Probe(string targetPath)
    {
        string probePath = Path.Combine(targetPath, ProbeFileName);
        long written = 0, read = 0;
        long writeBytes = 0, readBytes = 0;

        try
        {
            // ----- WRITE (com FlushToDisk/WriteThrough) -----
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
                fs.Flush(true); // true = FlushToDisk (WriteThrough sem FileOptions extra)
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

    /// <summary>Método de teste exposto para verificar tamanho do arquivo gravado.</summary>
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

    /// <summary>Buffer pseudo-aleatório, determinístico, reutilizável — mesmo seed, mesma sequência.</summary>
    public static byte[] CreatePseudoRandomBuffer(int length)
    {
        var buf = new byte[length];
        var span = buf.AsSpan();
        // Preenche com padrões repetidos derivativos (determinísticos, não todos iguais)
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

    /// <summary>Formatação para MB/s com cultura invariant (dois decimais).</summary>
    /// <summary>
    /// Formata bytes/s como MB/s DECIMAL (10^6), convenção para throughput de
    /// rede e armazenamento.
    ///
    /// Antes dividia por 1024² (MiB) mas rotulava "MB/s", enquanto o JSON
    /// expunha o mesmo dado em MB decimal — o relatório trazia dois números
    /// diferentes para a mesma medição, ambos chamados "MB/s"
    /// (89.48 e 85.33). Unificado em decimal.
    /// </summary>
    public static string FormatMBps(double bps)
        => (bps / 1_000_000.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Descrição legível do resultado (usado em logs/erros).</summary>
    public static string Describe(CopyProbeResult r)
        => $"write={FormatMBps(r.WriteBps)} MB/s, read={FormatMBps(r.ReadBps)} MB/s, effective={FormatMBps(r.EffectiveBps)} MB/s";

    private static long _bps(long bytes)
        => (long)(bytes * 1.0 / (ProbeDurationMs / 1000.0));

    /// <summary>Duração da janela de medição (ms). Aumentar se quiser mais precisão em discos lentos.</summary>
    internal static double ProbeDurationMs => 3000.0; // 3 segundos de amostragem
}
