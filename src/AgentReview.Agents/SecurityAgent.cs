using AgentReview.Agents.Configuration;
using AgentReview.Agents.GitHub;
using AgentReview.Agents.Llm;
using AgentReview.Agents.StaticAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentReview.Agents;

/// <summary>
/// The security reviewer: Semgrep security rules through the static-analysis MCP
/// server, plus an LLM pass for what registry packs miss (Phase 1 verified that no
/// standard pack flags a password inside a connection string). Pipeline lives in
/// <see cref="DiffReviewAgentBase"/>; Semgrep results are pattern matches, never CS
/// compiler diagnostics, so the default no-noise filter applies.
/// </summary>
public sealed class SecurityAgent(
    ILlmProvider llm,
    IStaticAnalysisClient staticAnalysis,
    IFileContentProvider fileContent,
    IOptions<SecurityAgentOptions> options,
    ILogger<SecurityAgent> logger)
    : DiffReviewAgentBase(llm, fileContent, options.Value, logger)
{
    public override string Name => "security";

    protected override Task<IReadOnlyList<StaticAnalysisFinding>> RunToolAsync(string code, CancellationToken cancellationToken) =>
        staticAnalysis.RunSemgrepAsync(code, options.Value.Ruleset, cancellationToken);

    protected override string SystemPrompt => """
        You are the security reviewer in a multi-agent code review system. You receive one
        unified diff. Report security findings only: injection (SQL, command, path
        traversal), hardcoded secrets, authentication and authorization mistakes, unsafe
        deserialization, and weak cryptography.

        Rules:
        1. Only report issues visible in the diff itself, on lines the diff adds or changes;
           never speculate about code you cannot see.
        2. Use file paths exactly as they appear after "+++ b/" in the diff.
        3. Report line numbers in the new version of the file, computed from the "@@" hunk headers.
        4. Pay particular attention to hardcoded credentials, including passwords and keys
           embedded in connection strings: pattern scanners routinely miss these, and you
           are the layer that catches them.
        5. Do not report code quality, style, or documentation issues; other agents cover those.
        6. Severity: Error for exploitable paths, Warning for hardening gaps, Info for hygiene.
        7. Give a concrete suggestion when you have one, otherwise null.
        8. If the diff is clean, return an empty findings array.
        9. Some reviews include <context file="..."> sections holding the full new version of
           changed files. Use them to trace data flow accurately, but findings must still
           point at lines the diff adds or changes.
        """;
}
