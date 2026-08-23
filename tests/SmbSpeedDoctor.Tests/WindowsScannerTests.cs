// Criado por André Santo (forg3) | junkyardgoodies.app
// Licença: MIT
//
// Item 1 — reproduz a NRE do CLI sem --path e garante degradação graciosa.

using Xunit;
using SmbSpeedDoctor.Core;
using SmbSpeedDoctor.Core.Windows;

namespace SmbSpeedDoctor.Tests;

public class WindowsScannerTests
{
    /// <summary>
    /// Reproduz exatamente a cadeia do bug: 'fix --export' sem '--path' →
    /// ParsePath retorna null → construtor recebia null → Collect() estourava NRE na linha 465.
    /// Após o fix (normalização no construtor + null-safety pontual), a coleta completa.
    /// </summary>
    [Fact]
    public void Collect_com_sharePath_null_nao_estoura_NRE()
    {
        var scanner = new WindowsScanner(sharePath: null, noCopy: true);
        var data = scanner.Collect();

        Assert.Equal(0L, data.FileCount);
        Assert.Equal(0L, data.AverageFileBytes);
    }

    [Fact]
    public void Collect_com_targetServer_null_nao_estoura_NRE()
    {
        var scanner = new WindowsScanner(targetServer: null!, sharePath: null!, noCopy: true);
        var data = scanner.Collect();

        Assert.Equal(0L, data.FileCount);   // degradação graciosa mantida
    }

    [Fact]
    public void Collect_com_sharePath_vazio_degrada_suavemente()
    {
        var scanner = new WindowsScanner(sharePath: "", noCopy: true);
        var data = scanner.Collect();

        Assert.Equal(0L, data.FileCount);
        Assert.Equal(0L, data.AverageFileBytes);
    }

    [Fact]
    public void Collect_com_caminho_existente_popula_workload_sem_excecao()
    {
        var tmp = Path.GetTempPath();
        var scanner = new WindowsScanner(sharePath: tmp, noCopy: true);

        var data = scanner.Collect();   // não deve lançar

        Assert.True(data.FileCount >= 0);
        Assert.True(data.AverageFileBytes >= 0);
    }

    // ---------------------------------------------------------------------
    // O alvo das medições de rede/SMB tem de sair do share informado.
    //
    // A CLI só passa sharePath; targetServer ficava no default "loopback" e
    // toda a camada media contra 127.0.0.1 — latência 0 e dialeto/multichannel
    // vindos de outra conexão qualquer da máquina. Apontar para
    // \\servidor\share não fazia o scanner olhar para esse servidor.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(@"\\servidor\share", "servidor")]
    [InlineData(@"\\100.70.183.123\projetos", "100.70.183.123")]
    [InlineData(@"\\servidor\share\sub\pasta", "servidor")]
    [InlineData(@"\\servidor", "servidor")]
    [InlineData("//servidor/share", "servidor")]
    public void Servidor_e_extraido_do_caminho_UNC(string unc, string esperado)
    {
        Assert.Equal(esperado, WindowsScanner.ExtractServerFromUnc(unc));
    }

    [Theory]
    [InlineData(@"C:\SMBTest")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(@"\\")]
    public void Caminho_local_ou_vazio_nao_produz_servidor(string? caminho)
    {
        Assert.Null(WindowsScanner.ExtractServerFromUnc(caminho));
    }
}
