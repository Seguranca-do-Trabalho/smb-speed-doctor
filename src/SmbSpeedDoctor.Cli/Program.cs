// SMB Speed Doctor CLI — subsistema console
// Uso: smbdoctor-cli.exe scan [--json] [--quiet] [--path <share>]
// Exit codes: 0 = ok, 1 = erro geral / warning, 2 = gargalo crítico (RMM)

using System.Text.Json;
using SmbSpeedDoctor.Core;
using SmbSpeedDoctor.Core.Windows;

namespace SmbSpeedDoctor.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Flags conhecidas; qualquer outra é rejeitada com exit 1 e mensagem de uso.
        string[] knownFlags = { "--json", "--quiet", "--path", "--no-copy", "--save", "--compare", "--export", "--help", "-h" };
        var unknown = args
            .Where(a => a.StartsWith("-") && !knownFlags.Contains(a.Split('=')[0]))
            .ToList();
        if (unknown.Count > 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(
                "Uso: smbdoctor-cli scan [--json] [--quiet] [--path <share>] [--no-copy] [--save <arq>] [--compare <arq>] | fix --export <arquivo.ps1>" + Environment.NewLine +
                "  --json             saída em JSON (integração RMM)" + Environment.NewLine +
                "  --quiet            suprime saída de texto (só exit code)" + Environment.NewLine +
                "  --path <share>     caminho UNC ou local alvo, ex.: \\\\servidor\\compartilhamento" + Environment.NewLine +
                "  --no-copy          desativa cópia de teste real (scan rápido)" + Environment.NewLine +
                "  --save <arquivo>   grava baseline do scan (para comparar depois)" + Environment.NewLine +
                "  --compare <arq>    compara com baseline gravado (MELHOROU/PIOROU/ESTÁVEL)" + Environment.NewLine +
                "  fix --export <ps1> gera script de correção (NÃO executa nada)" + Environment.NewLine +
                "Exit codes: 0 = ok | 1 = warning/erro | 2 = gargalo crítico");
            return unknown.Count > 0 ? 1 : 0;
        }

        var json = args.Contains("--json");
        var quiet = args.Contains("--quiet");
        var noCopy = args.Contains("--no-copy");
        var path = ParsePath(args);
        var savePath = ParseFlagValue(args, "--save");
        var comparePath = ParseFlagValue(args, "--compare");
        var exportPath = ParseFlagValue(args, "--export");

        try
        {
            // Item 1 — fix --export roda o diagnóstico e escreve um .ps1 de correção.
            if (exportPath is not null)
            {
                var scanFix = new WindowsScanner(sharePath: path, noCopy: noCopy).Collect();
                var resultFix = new DiagnosisEngine().Diagnose(scanFix);
                File.WriteAllText(exportPath, FixScriptBuilder.Build(resultFix));
                Console.WriteLine($"Script de correção gerado (NÃO EXECUTADO): {exportPath}");
                Console.WriteLine("Revise o conteúdo; as ações exigem elevação e rodada manual.");
                return 0;
            }

            var scan = new WindowsScanner(sharePath: path, noCopy: noCopy).Collect();
            var result = new DiagnosisEngine().Diagnose(scan);
            var code = ExitCodeMapper.For(result);

            // Item 2 — baseline comparativo
            if (savePath is not null)
            {
                BaselineStore.Save(scan, savePath);
                Console.WriteLine($"Baseline salvo: {savePath}");
            }
            if (comparePath is not null)
            {
                if (!File.Exists(comparePath))
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                        { error = $"Baseline não encontrado: {comparePath}", code = 1 }));
                    return 1;
                }
                var baseline = BaselineStore.Load(comparePath);
                foreach (var line in BaselineStore.Compare(baseline.Scan, scan))
                    Console.WriteLine($"{line.Metric,-28} {line.Before}  ->  {line.After}   [{line.Verdict}]");
            }

            if (json)
            {
                Console.WriteLine(Serialize(result, code, scan));
            }
            else if (!quiet)
            {
                Console.WriteLine(result.OneLineSummary);
                foreach (var f in result.Findings)
                    Console.WriteLine($"  [{f.Severity}] {f.Layer}: {f.Metric} = {f.Value}");
            }

            return code;
        }
        catch (Exception ex)
        {
            // Sem stack trace no JSON: logs RMM podem vazar; detalhe fica no stderr.
            Console.Error.WriteLine(ex.StackTrace);
            var error = new { error = ex.Message, code = 1 };
            Console.WriteLine(JsonSerializer.Serialize(error));
            return 1;
        }
    }

    /// <summary>
    /// Aceita as duas formas do contrato: "--path=\\servidor\share" e "--path \\servidor\share".
    /// </summary>
    private static string? ParsePath(string[] args)
    {
        var pathArg = args.FirstOrDefault(a => a.StartsWith("--path"));
        if (pathArg == null)
            return null;

        // Forma "--path=valor": valor embutido no próprio argumento.
        if (pathArg.Contains('='))
            return pathArg.Split('=', 2)[1];

        // Forma "--path valor": valor é o argumento seguinte.
        return args.SkipWhile(a => a != "--path").Skip(1).FirstOrDefault();
    }

    /// <summary>
    /// Extrai valor de flags com argumento (--save=arquivo ou --save arquivo).
    /// </summary>
    private static string? ParseFlagValue(string[] args, string flag)
    {
        var inline = args.FirstOrDefault(a => a.StartsWith(flag + "="));
        if (inline is not null)
            return inline.Split('=', 2)[1];
        if (!args.Contains(flag))
            return null;
        return args.SkipWhile(a => a != flag).Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
    }

    private static string Serialize(DiagnosisResult r, int code, ScanData scan)
        => JsonSerializer.Serialize(new
        {
            exitCode = code,
            dominant = r.Dominant.ToString(),
            severity = r.Severity.ToString(),
            confidence = r.ConfidencePct,
            summary = r.OneLineSummary,
            findings = r.Findings.Select(f => new
            {
                f.Layer, f.Metric, f.Value, f.Interpretation,
                f.Severity,
                f.WeightContribution
            }),
            remediation = r.RecommendedRemediation is { } rem ? new
            {
                rem.Id, rem.Title, rem.Description, rem.RollbackDescription,
                rem.Commands
            } : null,
            copyMethod = RobocopyBuilder.Build(scan, r)
        }, new JsonSerializerOptions { WriteIndented = true });
}
