using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AgentReview.McpServers.StaticAnalysis;

[McpServerToolType]
public static class StaticAnalysisTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "analyze_csharp")]
    [Description("Run Roslyn compiler diagnostics against C# source code. Returns a JSON array of findings: ruleId, message, line, column, severity, source.")]
    public static string AnalyzeCSharp(
        [Description("The C# source code to analyze.")] string code)
    {
        var findings = CSharpAnalyzer.Analyze(code);
        return JsonSerializer.Serialize(findings, JsonOptions);
    }

    [McpServerTool(Name = "run_semgrep")]
    [Description("Run Semgrep rules against source code (security patterns, secrets, injection). Returns a JSON array of findings: ruleId, message, line, column, severity, source.")]
    public static string RunSemgrep(
        [Description("The source code to analyze.")] string code,
        [Description("Semgrep registry ruleset to apply, e.g. p/default or p/csharp.")] string ruleset = "p/default")
    {
        var findings = SemgrepRunner.Run(code, ruleset);
        return JsonSerializer.Serialize(findings, JsonOptions);
    }
}
