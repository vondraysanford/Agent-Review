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
}
