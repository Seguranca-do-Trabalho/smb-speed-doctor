// SMB Speed Doctor CLI — console subsystem
// Usage: smbdoctor-cli.exe scan [--json] [--quiet] [--path <share>]
// Exit codes: 0 = ok, 1 = general error / warning, 2 = critical bottleneck (RMM)

using System.Text.Json;
using SmbSpeedDoctor.Core;
using SmbSpeedDoctor.Core.Windows;

namespace SmbSpeedDoctor.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Known flags; any other flag is rejected with exit 1 and usage message.
        string[] knownFlags = { "--json", "--quiet", "--path", "--no-copy", "--save", "--compare", "--export", "--help", "-h" };
        var unknown = args
            .Where(a => a.StartsWith("-") && !knownFlags.Contains(a.Split('=')[0]))
            .ToList();
        if (unknown.Count > 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(
                "Usage: smbdoctor-cli scan [--json] [--quiet] [--path <share>] [--no-copy] [--save <file>] [--compare <file>] | fix --export <file.ps1>" + Environment.NewLine +
                "  --json             JSON output (RMM integration)" + Environment.NewLine +
                "  --quiet            suppress text output (exit code only)" + Environment.NewLine +
                "  --path <share>     target UNC or local path, e.g.: \\\\server\\share" + Environment.NewLine +
                "  --no-copy          disable real test copy (quick scan)" + Environment.NewLine +
                "  --save <file>      save scan baseline (for later comparison)" + Environment.NewLine +
                "  --compare <file>   compare with saved baseline (IMPROVED/REGRESSED/STABLE)" + Environment.NewLine +
                "  fix --export <ps1> generate remediation script (does NOT execute anything)" + Environment.NewLine +
                "Exit codes: 0 = ok | 1 = warning/error | 2 = critical bottleneck");
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
            // fix --export runs diagnosis and writes a remediation .ps1 script.
            if (exportPath is not null)
            {
                var scanFix = new WindowsScanner(sharePath: path, noCopy: noCopy).Collect();
                var resultFix = new DiagnosisEngine().Diagnose(scanFix);
                File.WriteAllText(exportPath, FixScriptBuilder.Build(resultFix));
                Console.WriteLine($"Remediation script generated (NOT EXECUTED): {exportPath}");
                Console.WriteLine("Review contents; actions require elevation and manual execution.");
                return 0;
            }

            var scanner = new WindowsScanner(sharePath: path, noCopy: noCopy);
            var scan = scanner.Collect();
            var result = new DiagnosisEngine().Diagnose(scan);
            var code = ExitCodeMapper.For(result);

            // Comparative baseline
            if (savePath is not null)
            {
                BaselineStore.Save(scan, savePath);
                Console.WriteLine($"Baseline saved: {savePath}");
            }
            if (comparePath is not null)
            {
                if (!File.Exists(comparePath))
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                        { error = $"Baseline not found: {comparePath}", code = 1 }));
                    return 1;
                }
                var baseline = BaselineStore.Load(comparePath);
                foreach (var line in BaselineStore.Compare(baseline.Scan, scan))
                    Console.WriteLine($"{line.Metric,-28} {line.Before}  ->  {line.After}   [{line.Verdict}]");
            }

            if (json)
            {
                Console.WriteLine(Serialize(result, code, scan, scanner.CollectionErrors));
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
            // No stack trace in JSON: RMM logs could leak; detail goes to stderr.
            Console.Error.WriteLine(ex.StackTrace);
            var error = new { error = ex.Message, code = 1 };
            Console.WriteLine(JsonSerializer.Serialize(error));
            return 1;
        }
    }

    /// <summary>
    /// Accepts both contract forms: "--path=\\server\share" and "--path \\server\share".
    /// </summary>
    private static string? ParsePath(string[] args)
    {
        var pathArg = args.FirstOrDefault(a => a.StartsWith("--path"));
        if (pathArg == null)
            return null;

        // Form "--path=value": value embedded in argument.
        if (pathArg.Contains('='))
            return pathArg.Split('=', 2)[1];

        // Form "--path value": value is next argument.
        return args.SkipWhile(a => a != "--path").Skip(1).FirstOrDefault();
    }

    /// <summary>
    /// Extracts flag value with argument (--save=file or --save file).
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

    private static string Serialize(
        DiagnosisResult r, int code, ScanData scan, IReadOnlyList<string>? notes = null)
        => JsonSerializer.Serialize(new
        {
            exitCode = code,
            dominant = r.Dominant.ToString(),
            severity = r.Severity.ToString(),
            confidence = r.ConfidencePct,
            summary = r.OneLineSummary,

            // The MEASUREMENT itself — the number the tool exists to produce.
            // Previously not serialized: `copyMethod.EstimatedThroughputMBps` is the
            // robocopy ESTIMATE (bounded by ceilings), not the measured rate, and
            // was the only speed number in JSON. RMM integrators could not see
            // the test copy result.
            measuredThroughputMBps = Math.Round(scan.ObservedCopyThroughputBps / 1_000_000.0, 2),
            throughputQuality = scan.ThroughputQuality.ToString(),
            linkSpeedMbps = Math.Round(scan.LinkSpeedBps / 1_000_000.0, 0),
            latencyMs = Math.Round(scan.LatencyMs, 2),
            fileCountSampled = scan.FileCount,
            averageFileBytes = (long)scan.AverageFileBytes,

            // Collection notes: copy probe result, fallbacks to
            // approximation, workload sampling truncation. Previously
            // calculated and discarded.
            collectionNotes = notes ?? Array.Empty<string>(),

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
