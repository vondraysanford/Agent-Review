using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentReview.McpServers.StaticAnalysis;

/// <summary>
/// run_semgrep: writes the supplied code to a temp file, shells out to the
/// Semgrep CLI, and maps its JSON results to <see cref="AnalysisFinding"/>.
/// </summary>
public static partial class SemgrepRunner
{
    private static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(3);

    // Registry pack names and local rule paths only; blocks anything that
    // could smuggle extra CLI flags into the argument list.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex RulesetPattern();

    public static IReadOnlyList<AnalysisFinding> Run(string code, string ruleset)
    {
        if (!RulesetPattern().IsMatch(ruleset))
        {
            throw new ArgumentException($"Invalid ruleset '{ruleset}'. Expected a registry pack like p/default.");
        }

        var cli = LocateCli()
            ?? throw new InvalidOperationException(
                "Semgrep CLI not found. Install the PyPI package 'semgrep' or set SEMGREP_PATH to the binary.");

        var workDir = Directory.CreateTempSubdirectory("agentreview-semgrep-");
        try
        {
            File.WriteAllText(Path.Combine(workDir.FullName, "Snippet.cs"), code);

            var startInfo = new ProcessStartInfo(cli)
            {
                ArgumentList = { "scan", "--json", "--quiet", "--config", ruleset, "Snippet.cs" },
                WorkingDirectory = workDir.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // The semgrep entry point re-executes its Python half (pysemgrep)
            // via PATH, so the CLI's own directory has to be on it.
            startInfo.Environment["PATH"] =
                Path.GetDirectoryName(cli) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the Semgrep process.");

            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(CliTimeout))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException($"Semgrep timed out after {CliTimeout.TotalSeconds:F0}s.");
            }

            // Semgrep exits 0 with or without findings; nonzero means the scan
            // itself failed (bad config, no network for a registry pack, ...).
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Semgrep failed (exit {process.ExitCode}): {stderrTask.GetAwaiter().GetResult().Trim()}");
            }

            return ParseResults(stdout);
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }

    public static IReadOnlyList<AnalysisFinding> ParseResults(string json)
    {
        using var doc = JsonDocument.Parse(json);

        return doc.RootElement.GetProperty("results").EnumerateArray()
            .Select(result =>
            {
                var start = result.GetProperty("start");
                var extra = result.GetProperty("extra");
                return new AnalysisFinding(
                    result.GetProperty("check_id").GetString() ?? "unknown",
                    extra.GetProperty("message").GetString() ?? "",
                    start.GetProperty("line").GetInt32(),
                    start.GetProperty("col").GetInt32(),
                    MapSeverity(extra.GetProperty("severity").GetString()),
                    Source: "semgrep");
            })
            .OrderBy(f => f.Line).ThenBy(f => f.Column).ThenBy(f => f.RuleId)
            .ToList();
    }

    private static string MapSeverity(string? semgrepSeverity) => semgrepSeverity switch
    {
        "ERROR" => "Error",
        "WARNING" => "Warning",
        "INFO" => "Info",
        _ => semgrepSeverity ?? "Unknown",
    };

    private static string? LocateCli()
    {
        var configured = Environment.GetEnvironmentVariable("SEMGREP_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured)
                ? configured
                : throw new InvalidOperationException($"SEMGREP_PATH is set but no file exists at '{configured}'.");
        }

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // After PATH: the conventional pip --user locations (macOS, then Linux),
        // newest Python first, so a plain user-site install works unconfigured.
        var userSiteCandidates = Directory.Exists(Path.Combine(home, "Library", "Python"))
            ? Directory.EnumerateDirectories(Path.Combine(home, "Library", "Python"))
                .OrderByDescending(d => d)
                .Select(d => Path.Combine(d, "bin"))
            : [];

        return pathDirs
            .Concat(userSiteCandidates)
            .Append(Path.Combine(home, ".local", "bin"))
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(dir => Path.Combine(dir, "semgrep"))
            .FirstOrDefault(File.Exists);
    }
}
