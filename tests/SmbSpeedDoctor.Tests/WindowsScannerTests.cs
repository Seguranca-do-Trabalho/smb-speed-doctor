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
}
