using System.Text.Json;
using AgentReview.Agents.Configuration;
using AgentReview.Agents.Diff;
using AgentReview.Agents.Llm;
using AgentReview.Agents.StaticAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentReview.Agents;

/// <summary>
/// The quality reviewer: parses a unified diff, runs changed C# code through the
/// static-analysis MCP server, adds one LLM pass for complexity, naming, and
/// duplication, and merges both into one list of schema-valid findings.
/// Fails closed: if a tool or the LLM fails, the review fails; degraded partial
/// reviews would break the honesty contract, and tolerating a failed agent is the
/// orchestrator's job in Phase 4.
/// </summary>
public sealed class QualityAgent(
    ILlmProvider llm,
    IStaticAnalysisClient staticAnalysis,
    IOptions<QualityAgentOptions> options,
    ILogger<QualityAgent> logger) : IReviewAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "quality";

    public async Task<IReadOnlyList<Finding>> ReviewAsync(string diff, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        if (diff.Length > opts.MaxDiffChars)
        {
            throw new ArgumentException(
                $"Diff is {diff.Length} chars; this agent caps input at {opts.MaxDiffChars} (QualityAgent:MaxDiffChars).",
                nameof(diff));
        }

        var files = UnifiedDiffParser.Parse(diff);
        if (files.Count == 0)
        {
            throw new ArgumentException("Input is not a unified diff.", nameof(diff));
        }

        var findings = new List<Finding>();
        foreach (var file in files.Where(f => f.IsCSharp && f.NewLines.Count > 0))
        {
            findings.AddRange(await AnalyzeFileAsync(file, cancellationToken));
        }

        findings.AddRange(await GetLlmFindingsAsync(diff, files, findings, cancellationToken));

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Source, StringComparer.Ordinal)
            .ThenBy(f => f.Issue, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Runs one file's hunk text through analyze_csharp and maps the results back to
    /// real line numbers. Two filters keep fragment noise out: findings on context
    /// lines are outside the change under review, and CS compiler errors are almost
    /// always resolution failures caused by analyzing a fragment (the snippet cannot
    /// see usings or types outside the hunks). Known v1 limitation: a real compile
    /// error introduced by the diff is filtered with the noise; full-file context via
    /// the GitHub MCP server is the planned fix.
    /// </summary>
    private async Task<List<Finding>> AnalyzeFileAsync(DiffFile file, CancellationToken cancellationToken)
    {
        var snippet = string.Join('\n', file.NewLines.Select(l => l.Content));
        var raw = await staticAnalysis.AnalyzeCSharpAsync(snippet, cancellationToken);

        var kept = new List<Finding>();
        int droppedErrors = 0, droppedContext = 0, droppedOutOfRange = 0;
        foreach (var f in raw)
        {
            if (f.Line < 1 || f.Line > file.NewLines.Count)
            {
                droppedOutOfRange++;
                continue;
            }

            var line = file.NewLines[f.Line - 1];
            if (!line.IsAdded)
            {
                droppedContext++;
                continue;
            }

            if (f.RuleId.StartsWith("CS", StringComparison.Ordinal)
                && string.Equals(f.Severity, "Error", StringComparison.OrdinalIgnoreCase))
            {
                droppedErrors++;
                continue;
            }

            kept.Add(new Finding(
                Issue: $"{f.RuleId}: {f.Message}",
                File: file.Path,
                Line: line.NewLineNumber,
                Severity: Finding.ParseSeverity(f.Severity),
                Suggestion: null,
                Source: f.Source));
        }

        logger.LogInformation(
            "Analyzer pass {File}: {Kept} kept, {DroppedErrors} fragment compiler errors dropped, {DroppedContext} context-line findings dropped, {DroppedOutOfRange} out of range",
            file.Path,
            kept.Count,
            droppedErrors,
            droppedContext,
            droppedOutOfRange);

        return kept;
    }

    private async Task<List<Finding>> GetLlmFindingsAsync(
        string diff,
        IReadOnlyList<DiffFile> files,
        IReadOnlyList<Finding> analyzerFindings,
        CancellationToken cancellationToken)
    {
        var response = await llm.CompleteAsync(
            new LlmRequest(SystemPrompt, diff, ResponseSchema, options.Value.MaxOutputTokens),
            cancellationToken);

        if (response.StopReason == "refusal")
        {
            throw new InvalidOperationException("The LLM refused the review request.");
        }

        if (response.StopReason == "max_tokens")
        {
            throw new InvalidOperationException(
                "The LLM response hit the output token cap and is likely truncated; raise QualityAgent:MaxOutputTokens.");
        }

        var parsed = JsonSerializer.Deserialize<LlmReviewResponse>(response.Text, JsonOptions)
            ?? throw new InvalidOperationException("The LLM returned no parsable findings payload.");

        // Grounding whitelist: LLM findings must name a file and new-side line that the
        // diff actually contains, or they are dropped. The agent never trusts the model
        // with provenance either; Source is always stamped here.
        var validLines = files.ToDictionary(
            f => f.Path,
            f => f.NewLines.Select(l => l.NewLineNumber).ToHashSet(),
            StringComparer.Ordinal);
        var analyzerLines = analyzerFindings.Select(f => (f.File, f.Line)).ToHashSet();

        var kept = new List<Finding>();
        foreach (var item in parsed.Findings)
        {
            if (!validLines.TryGetValue(item.File, out var lines) || !lines.Contains(item.Line))
            {
                logger.LogWarning("LLM finding dropped, not grounded in the diff: {File}:{Line}", item.File, item.Line);
                continue;
            }

            if (analyzerLines.Contains((item.File, item.Line)))
            {
                // Same line as a deterministic analyzer finding: almost always the same
                // observation restated, and the analyzer version is the one to trust.
                logger.LogInformation("LLM finding dropped, duplicates an analyzer finding at {File}:{Line}", item.File, item.Line);
                continue;
            }

            kept.Add(new Finding(item.Issue, item.File, item.Line, Finding.ParseSeverity(item.Severity), item.Suggestion, "llm"));
        }

        return kept;
    }

    private sealed record LlmReviewResponse(List<LlmFindingDto> Findings);

    private sealed record LlmFindingDto(string Issue, string File, int Line, string Severity, string? Suggestion);

    private const string SystemPrompt = """
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
        """;

    private const string ResponseSchema = """
        {
          "type": "object",
          "properties": {
            "findings": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "issue": { "type": "string", "description": "What is wrong, in one sentence." },
                  "file": { "type": "string", "description": "File path exactly as it appears after '+++ b/' in the diff." },
                  "line": { "type": "integer", "description": "Line number in the new version of the file." },
                  "severity": { "type": "string", "enum": ["Info", "Warning", "Error"] },
                  "suggestion": { "type": ["string", "null"], "description": "Concrete fix, or null." }
                },
                "required": ["issue", "file", "line", "severity", "suggestion"],
                "additionalProperties": false
              }
            }
          },
          "required": ["findings"],
          "additionalProperties": false
        }
        """;
}
