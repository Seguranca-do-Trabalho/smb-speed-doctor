// Criado por André Santo (forg3) | junkyardgoodies.app
using SmbSpeedDoctor.Core;
using SmbSpeedDoctor.Core.Windows;
using Xunit;

namespace SmbSpeedDoctor.Tests;

/// <summary>
/// Item 3 — cópia de teste real. A decisão (cópia vs aproximação), a resolução
/// do throughput observado, a formatação dos resultados e a cópia em si
/// (executada num tmpdir local — File I/O é cross-platform).
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

    // ---------- Lógica de decisão ----------

    [Fact]
    public void Sem_path_definido_cai_na_aproximacao()
    {
        Assert.Equal(CopyProbeDecision.FallbackToApproximation,
            RealCopyProbe.Decide(targetPath: "", realCopyDisabled: false));
    }

    [Fact]
    public void Flag_no_copy_desativa_a_copia_real()
    {
        Assert.Equal(CopyProbeDecision.FallbackToApproximation,
            RealCopyProbe.Decide(targetPath: @"\\servidor\share", realCopyDisabled: true));
    }

    [Fact]
    public void Caminho_alvo_valido_executa_copia_real()
    {
        Assert.Equal(CopyProbeDecision.RunRealCopy,
            RealCopyProbe.Decide(targetPath: @"\\servidor\share", realCopyDisabled: false));
    }

    // ---------- Resolução do throughput observado ----------

    [Fact]
    public void Copia_com_sucesso_popula_o_menor_entre_write_e_read()
    {
        var result = new CopyProbeResult(true, 200_000_000, 120_000_000, 120_000_000, null);
        var errors = new List<string>();

        double observed = RealCopyProbe.ResolveObservedCopyBps(
            CopyProbeDecision.RunRealCopy, result, approximationBps: 300_000_000, errors);

        Assert.Equal(120_000_000, observed); // teto prático = min(write, read)
        Assert.Empty(errors);
    }

    [Fact]
    public void Copia_que_falha_registra_motivo_e_volta_para_aproximacao()
    {
        var result = CopyProbeResult.Fail("acesso negado ao criar arquivo de teste");
        var errors = new List<string>();

        double observed = RealCopyProbe.ResolveObservedCopyBps(
            CopyProbeDecision.RunRealCopy, result, approximationBps: 250_000_000, errors);

        Assert.Equal(250_000_000, observed);
        string motivo = Assert.Single(errors);
        Assert.Contains("acesso negado", motivo);
        Assert.Contains("aproximação", motivo);
    }

    [Fact]
    public void Fallback_por_decisao_nao_registra_erro_de_colecao()
    {
        var errors = new List<string>();

        double observed = RealCopyProbe.ResolveObservedCopyBps(
            CopyProbeDecision.FallbackToApproximation, probeResult: null,
            approximationBps: 100_000_000, errors);

        Assert.Equal(100_000_000, observed);
        Assert.Empty(errors); // desativado de propósito não é falha de coleta
    }

    // ---------- A cópia em si (tmpdir local do Linux) ----------

    [Fact]
    public void Copia_em_tmpdir_medindo_write_e_read_e_apagando_arquivo()
    {
        Directory.CreateDirectory(_tmpDir);

        var probe = new RealCopyProbe { TotalBytes = 8L * 1024 * 1024 }; // 8 MB p/ teste rápido
        var result = probe.Probe(_tmpDir);

        Assert.True(result.Success, $"esperava sucesso, veio: {result.Error}");
        Assert.True(result.WriteBps > 0, "write MB/s deve ser > 0");
        Assert.True(result.ReadBps > 0, "read MB/s deve ser > 0");
        Assert.Equal(Math.Min(result.WriteBps, result.ReadBps), result.EffectiveBps);
        Assert.Null(result.Error);
        Assert.Empty(Directory.GetFiles(_tmpDir)); // arquivo de teste apagado no finally
    }

    [Fact]
    public void Copia_em_caminho_inexistente_falha_sem_estourar_excecao()
    {
        var inexistente = Path.Combine(_tmpDir, "nao-existe", "sub");

        var result = new RealCopyProbe { TotalBytes = 1024 * 1024 }.Probe(inexistente);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(0, result.WriteBps);
        Assert.Equal(0, result.ReadBps);
    }

    [Fact]
    public void Arquivo_escrito_tem_o_tamanho_total_solicitado()
    {
        Directory.CreateDirectory(_tmpDir);

        long total = 3L * 1024 * 1024;
        string destino = Path.Combine(_tmpDir, "alvo-manual");

        new RealCopyProbe().WriteTestFile(destino, total);

        Assert.Equal(total, new FileInfo(destino).Length);
    }

    // ---------- Buffer e formatação ----------

    [Fact]
    public void Buffer_pseudoaleatorio_e_deterministico_e_do_tamanho_pedido()
    {
        var b1 = RealCopyProbe.CreatePseudoRandomBuffer(64 * 1024);
        var b2 = RealCopyProbe.CreatePseudoRandomBuffer(64 * 1024);

        Assert.Equal(64 * 1024, b1.Length);
        Assert.Equal(b1, b2);                       // mesma seed => mesmo conteúdo
        Assert.NotEqual(new byte[b1.Length], b1);   // não é buffer zerado
    }

    [Fact]
    public void FormatMBps_usa_cultura_invariante()
    {
        // MB DECIMAL (10^6), nao MiB: throughput de rede se expressa assim, e
        // o JSON usa a mesma base. Antes as duas bases conviviam no mesmo
        // relatorio, ambas rotuladas "MB/s".
        Assert.Equal("10.00", RealCopyProbe.FormatMBps(10_000_000));
        Assert.Equal("0.50", RealCopyProbe.FormatMBps(500_000));

        // A unidade é adicionada por Describe, não pelo formatador numérico.
        string described = RealCopyProbe.Describe(
            new CopyProbeResult(true, 10_000_000, 0, 0, null));
        Assert.Contains("10.00 MB/s", described);
    }

    [Fact]
    public void Describe_mostra_write_read_e_teto_efetivo()
    {
        var result = new CopyProbeResult(true, 200_000_000, 100_000_000, 100_000_000, null);

        string s = RealCopyProbe.Describe(result);

        Assert.Contains("write", s);
        Assert.Contains("read", s);
        Assert.Contains("100.00 MB/s", s); // teto efetivo aparece formatado
    }

    // ---------------------------------------------------------------------
    // Procedência do throughput: é o que separa "medi e deu baixo" de
    // "não medi nada". Sem isso o motor concluía gargalo a partir de NIC ociosa.
    // ---------------------------------------------------------------------

    [Fact]
    public void Copia_real_bem_sucedida_e_medicao_Measured()
    {
        var ok = CopyProbeResult.Ok(120L * 1024 * 1024, 110L * 1024 * 1024);

        var q = RealCopyProbe.ResolveQuality(
            CopyProbeDecision.RunRealCopy, ok, approximationBps: 0);

        Assert.Equal(MeasurementQuality.Measured, q);
    }

    [Fact]
    public void Sem_copia_e_com_rede_ociosa_e_Unavailable()
    {
        // Cenário exato do bug: `scan` sem --path numa rede parada. A NIC rende
        // ~886 B/s de tráfego de fundo — isso NÃO é medição de cópia.
        var q = RealCopyProbe.ResolveQuality(
            CopyProbeDecision.FallbackToApproximation, probeResult: null, approximationBps: 886);

        Assert.Equal(MeasurementQuality.Unavailable, q);
    }

    [Fact]
    public void Sem_copia_mas_com_trafego_real_e_Approximated()
    {
        var q = RealCopyProbe.ResolveQuality(
            CopyProbeDecision.FallbackToApproximation, probeResult: null,
            approximationBps: 40L * 1024 * 1024);

        Assert.Equal(MeasurementQuality.Approximated, q);
    }

    [Fact]
    public void Copia_real_que_falhou_nao_vira_Measured()
    {
        var falhou = CopyProbeResult.Fail("acesso negado");

        var q = RealCopyProbe.ResolveQuality(
            CopyProbeDecision.RunRealCopy, falhou, approximationBps: 886);

        Assert.NotEqual(MeasurementQuality.Measured, q);
        Assert.Equal(MeasurementQuality.Unavailable, q);
    }
}
