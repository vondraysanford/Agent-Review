namespace AgentReview.Agents.StaticAnalysis;

/// <summary>
/// Client-side copy of the static-analysis MCP server's wire contract. It deliberately
/// does not reference the server project: the agent consumes the tool over MCP as JSON,
/// exactly as any other MCP client would, so the contract is the wire shape and nothing else.
/// </summary>
public sealed record StaticAnalysisFinding(
    string RuleId,
    string Message,
    int Line,
    int Column,
    string Severity,
    string Source);

public interface IStaticAnalysisClient
{
    /// <summary>
    /// Runs the analyze_csharp tool against a code snippet. Line numbers in the result
    /// are 1-based positions within the snippet, not within any real file.
    /// </summary>
    Task<IReadOnlyList<StaticAnalysisFinding>> AnalyzeCSharpAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the run_semgrep tool against a code snippet with the given registry pack.
    /// The server analyzes the snippet as C#. Same snippet-relative line semantics.
    /// </summary>
    Task<IReadOnlyList<StaticAnalysisFinding>> RunSemgrepAsync(string code, string ruleset, CancellationToken cancellationToken = default);
}
