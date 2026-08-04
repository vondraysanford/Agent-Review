using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace AgentReview.McpServers.StaticAnalysis;

[McpServerToolType]
public class StaticAnalysisTools(ILogger<StaticAnalysisTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "analyze_csharp")]
    [Description("Run Roslyn compiler diagnostics against C# source code. Returns a JSON array of findings: ruleId, message, line, column, severity, source.")]
    public string AnalyzeCSharp(
        [Description("The C# source code to analyze.")] string code)
    {
        // Code length only, never the code: analyzed snippets can hold secrets.
        var stopwatch = Stopwatch.StartNew();
        var findings = CSharpAnalyzer.Analyze(code);
        logger.LogInformation(
            "analyze_csharp: {CodeLength} chars in, {FindingCount} findings out, {ElapsedMs} ms",
            code.Length, findings.Count, stopwatch.ElapsedMilliseconds);
        return JsonSerializer.Serialize(findings, JsonOptions);
    }

    [McpServerTool(Name = "run_semgrep")]
    [Description("Run Semgrep rules against source code (security patterns, secrets, injection). Returns a JSON array of findings: ruleId, message, line, column, severity, source.")]
    public string RunSemgrep(
        [Description("The source code to analyze.")] string code,
        [Description("Semgrep registry ruleset to apply, e.g. p/default or p/csharp.")] string ruleset = "p/default")
    {
        var stopwatch = Stopwatch.StartNew();
        var findings = SemgrepRunner.Run(code, ruleset);
        logger.LogInformation(
            "run_semgrep: {CodeLength} chars in, ruleset {Ruleset}, {FindingCount} findings out, {ElapsedMs} ms",
            code.Length, ruleset, findings.Count, stopwatch.ElapsedMilliseconds);
        return JsonSerializer.Serialize(findings, JsonOptions);
    }
}
