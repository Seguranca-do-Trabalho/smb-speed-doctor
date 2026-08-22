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
        var json = args.Contains("--json");
        var quiet = args.Contains("--quiet");
        var path = ParsePath(args);

        try
        {
            var scan = new WindowsScanner().Collect();
            var result = new DiagnosisEngine().Diagnose(scan);
            var code = ExitCodeMapper.For(result);

            if (json)
            {
                Console.WriteLine(Serialize(result, code));
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

    private static string Serialize(DiagnosisResult r, int code)
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
            copyMethod = new
            {
                r.RecommendedMethod.MethodName,
                r.RecommendedMethod.Rationale
            }
        }, new JsonSerializerOptions { WriteIndented = true });
}
