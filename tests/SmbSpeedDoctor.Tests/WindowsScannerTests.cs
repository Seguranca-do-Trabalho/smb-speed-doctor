// Created by forg3
// License: MIT
//
// Reproduces NRE in CLI without --path and ensures graceful degradation.

using Xunit;
using SmbSpeedDoctor.Core;
using SmbSpeedDoctor.Core.Windows;

namespace SmbSpeedDoctor.Tests;

public class WindowsScannerTests
{
    /// <summary>
    /// Reproduces bug chain: 'fix --export' without '--path' →
    /// ParsePath returns null → constructor received null → Collect() threw NRE.
    /// After fix (normalization in constructor + targeted null-safety), collection succeeds.
    /// </summary>
    [Fact]
    public void Collect_with_null_sharePath_does_not_throw_NRE()
    {
        var scanner = new WindowsScanner(sharePath: null, noCopy: true);
        var data = scanner.Collect();

        Assert.Equal(0L, data.FileCount);
        Assert.Equal(0L, data.AverageFileBytes);
    }

    [Fact]
    public void Collect_with_null_targetServer_does_not_throw_NRE()
    {
        var scanner = new WindowsScanner(targetServer: null!, sharePath: null!, noCopy: true);
        var data = scanner.Collect();

        Assert.Equal(0L, data.FileCount);   // graceful degradation maintained
    }

    [Fact]
    public void Collect_with_empty_sharePath_degrades_gracefully()
    {
        var scanner = new WindowsScanner(sharePath: "", noCopy: true);
        var data = scanner.Collect();

        Assert.Equal(0L, data.FileCount);
        Assert.Equal(0L, data.AverageFileBytes);
    }

    [Fact]
    public void Collect_with_existing_path_populates_workload_without_exception()
    {
        var tmp = Path.GetTempPath();
        var scanner = new WindowsScanner(sharePath: tmp, noCopy: true);

        var data = scanner.Collect();   // should not throw

        Assert.True(data.FileCount >= 0);
        Assert.True(data.AverageFileBytes >= 0);
    }

    // ---------------------------------------------------------------------
    // Network/SMB measurement target must come from provided share.
    //
    // CLI only passes sharePath; targetServer defaulted to "loopback" and
    // the entire layer measured against 127.0.0.1 — 0 latency and dialect/multichannel
    // coming from any other connection on the machine. Pointing to
    // \\server\share did not make the scanner look at that server.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(@"\\server\share", "server")]
    [InlineData(@"\\100.70.183.123\projects", "100.70.183.123")]
    [InlineData(@"\\server\share\sub\folder", "server")]
    [InlineData(@"\\server", "server")]
    [InlineData("//server/share", "server")]
    public void Server_is_extracted_from_UNC_path(string unc, string expected)
    {
        Assert.Equal(expected, WindowsScanner.ExtractServerFromUnc(unc));
    }

    [Theory]
    [InlineData(@"C:\SMBTest")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(@"\\")]
    public void Local_or_empty_path_does_not_produce_server(string? path)
    {
        Assert.Null(WindowsScanner.ExtractServerFromUnc(path));
    }
}
