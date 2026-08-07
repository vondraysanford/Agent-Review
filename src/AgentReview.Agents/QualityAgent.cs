using AgentReview.Agents.Configuration;
using AgentReview.Agents.GitHub;
using AgentReview.Agents.Llm;
using AgentReview.Agents.StaticAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentReview.Agents;

/// <summary>
/// The quality reviewer: Roslyn compiler diagnostics and CA rules through the
/// static-analysis MCP server, plus an LLM pass for complexity, naming, and
/// duplication. Pipeline lives in <see cref="DiffReviewAgentBase"/>.
/// </summary>
public sealed class QualityAgent(
    ILlmProvider llm,
    IStaticAnalysisClient staticAnalysis,
    IFileContentProvider fileContent,
    IOptions<QualityAgentOptions> options,
    ILogger<QualityAgent> logger)
    : DiffReviewAgentBase(llm, fileContent, options.Value, logger)
{
    public override string Name => "quality";

    protected override Task<IReadOnlyList<StaticAnalysisFinding>> RunToolAsync(string code, CancellationToken cancellationToken) =>
        staticAnalysis.AnalyzeCSharpAsync(code, cancellationToken);

    /// <summary>
    /// CS compiler errors on partial input are almost always resolution failures (the
    /// analyzed text cannot see other files or package references), so they are noise
    /// rather than review findings. CS warnings and CA rules carry the real signal.
    /// </summary>
    protected override bool IsToolNoise(StaticAnalysisFinding finding) =>
        finding.RuleId.StartsWith("CS", StringComparison.Ordinal)
        && string.Equals(finding.Severity, "Error", StringComparison.OrdinalIgnoreCase);

    protected override string SystemPrompt => """
        You are the quality reviewer in a multi-agent code review system. You receive one
        unified diff. Report code quality findings only: complexity, naming, duplication,
        and maintainability.

        Rules:
        1. Only report issues visible in the diff itself, on lines the diff adds or changes;
           never speculate about code you cannot see.
        2. Use file paths exactly as they appear after "+++ b/" in the diff.
        3. Report line numbers in the new version of the file, computed from the "@@" hunk headers.
        4. Do not report compile errors, missing imports, or security issues; other tools cover those.
        5. Severity: Error for changes likely to cause wrong behavior, Warning for maintainability
           problems worth fixing before merge, Info for minor style points.
        6. Give a concrete suggestion when you have one, otherwise null.
        7. If the diff is clean, return an empty findings array.
        8. Some reviews include <context file="..."> sections holding the full new version of
           changed files. Use them to judge complexity, naming, and duplication accurately,
           but findings must still point at lines the diff adds or changes.
        """;
}
