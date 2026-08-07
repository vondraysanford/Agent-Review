using AgentReview.Agents.Configuration;
using AgentReview.Agents.Diff;
using AgentReview.Agents.GitHub;
using AgentReview.Agents.Llm;
using AgentReview.Agents.StaticAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentReview.Agents;

/// <summary>
/// The documentation reviewer: LLM-only, no static tool (nothing deterministic can
/// tell you the README was not updated). Fetches both C# and Markdown files as GitHub
/// context so it can judge docs against the code they describe. Future enhancement:
/// fetching related files that are not in the diff (a README the change should have
/// touched) once agents can request unchanged files.
/// </summary>
public sealed class DocsAgent(
    ILlmProvider llm,
    IFileContentProvider fileContent,
    IOptions<DocsAgentOptions> options,
    ILogger<DocsAgent> logger)
    : DiffReviewAgentBase(llm, fileContent, options.Value, logger)
{
    public override string Name => "docs";

    /// <summary>No static tool: this agent's entire judgment is the LLM pass.</summary>
    protected override bool RunsToolOn(DiffFile file) => false;

    protected override bool WantsContextFor(DiffFile file) =>
        file.IsCSharp || file.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    /// <summary>Never invoked because <see cref="RunsToolOn"/> is always false.</summary>
    protected override Task<IReadOnlyList<StaticAnalysisFinding>> RunToolAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StaticAnalysisFinding>>([]);

    protected override string SystemPrompt => """
        You are the documentation reviewer in a multi-agent code review system. You receive
        one unified diff. Report documentation findings only: new or changed public APIs
        missing XML doc comments, comments this change makes stale or contradictory,
        documentation files not updated for behavior changes visible in the diff, and names
        or docs that mislead about what the code now does.

        Rules:
        1. Only report issues visible in the diff itself, on lines the diff adds or changes;
           never speculate about code you cannot see.
        2. Use file paths exactly as they appear after "+++ b/" in the diff.
        3. Report line numbers in the new version of the file, computed from the "@@" hunk headers.
        4. Do not report code quality, style, correctness, or security issues; other agents
           cover those.
        5. Severity: Warning for missing or wrong documentation on public surface area,
           Info for internal or minor gaps.
        6. Draft the fix in the suggestion field: a complete XML doc comment for a missing
           one, or the corrected sentence for a stale comment. Use null only when no
           concrete text can be drafted.
        7. If the documentation is fine, return an empty findings array.
        8. Some reviews include <context file="..."> sections holding the full new version of
           changed files. Use them to judge whether documentation matches behavior, but
           findings must still point at lines the diff adds or changes.
        """;
}
