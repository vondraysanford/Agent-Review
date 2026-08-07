using System.Text;
using System.Text.Json;
using AgentReview.Agents.Configuration;
using AgentReview.Agents.Diff;
using AgentReview.Agents.GitHub;
using AgentReview.Agents.Llm;
using AgentReview.Agents.StaticAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentReview.Agents;

/// <summary>
/// The quality reviewer: parses a unified diff, runs changed C# code through the
/// static-analysis MCP server, adds one LLM pass for complexity, naming, and
/// duplication, and merges both into one list of schema-valid findings.
/// With repo coordinates, full new-revision files are fetched through the GitHub MCP
/// server: the analyzer then sees whole files instead of hunk fragments, and the LLM
/// gets the same files as context. Without coordinates, the diff is all there is.
/// Fails closed on analyzer or LLM errors; missing GitHub context only degrades.
/// </summary>
public sealed class QualityAgent(
    ILlmProvider llm,
    IStaticAnalysisClient staticAnalysis,
    IFileContentProvider fileContent,
    IOptions<QualityAgentOptions> options,
    ILogger<QualityAgent> logger) : IReviewAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "quality";

    public async Task<IReadOnlyList<Finding>> ReviewAsync(ReviewRequest request, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var diff = request.Diff;
        if (diff.Length > opts.MaxDiffChars)
        {
            throw new ArgumentException(
                $"Diff is {diff.Length} chars; this agent caps input at {opts.MaxDiffChars} (QualityAgent:MaxDiffChars).",
                nameof(request));
        }

        var files = UnifiedDiffParser.Parse(diff);
        if (files.Count == 0)
        {
            throw new ArgumentException("Input is not a unified diff.", nameof(request));
        }

        var findings = new List<Finding>();
        var contextFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.Where(f => f.IsCSharp && f.NewLines.Count > 0))
        {
            string? fullContent = null;
            if (request.Repo is not null)
            {
                fullContent = await fileContent.GetFileContentAsync(request.Repo, file.Path, cancellationToken);
            }

            if (fullContent is not null)
            {
                contextFiles[file.Path] = fullContent;
                findings.AddRange(await AnalyzeFullFileAsync(file, fullContent, cancellationToken));
            }
            else
            {
                if (request.Repo is not null)
                {
                    logger.LogWarning("No context for {File}; falling back to fragment analysis", file.Path);
                }

                findings.AddRange(await AnalyzeFragmentAsync(file, cancellationToken));
            }
        }

        findings.AddRange(await GetLlmFindingsAsync(diff, files, contextFiles, findings, cancellationToken));

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Source, StringComparer.Ordinal)
            .ThenBy(f => f.Issue, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Full-file analysis, used when GitHub context is available. Analyzer line numbers
    /// are real file lines, so no positional mapping is needed; findings are kept only
    /// on lines this diff added, keeping the review scoped to the change. The CS-error
    /// filter stays even here: a single file still cannot resolve types from other files
    /// or packages, so resolution errors remain fragment noise, just less of it.
    /// </summary>
    private async Task<List<Finding>> AnalyzeFullFileAsync(DiffFile file, string fullContent, CancellationToken cancellationToken)
    {
        var raw = await staticAnalysis.AnalyzeCSharpAsync(fullContent, cancellationToken);
        var addedLines = file.NewLines.Where(l => l.IsAdded).Select(l => l.NewLineNumber).ToHashSet();

        var kept = new List<Finding>();
        int droppedErrors = 0, droppedUnchanged = 0;
        foreach (var f in raw)
        {
            if (!addedLines.Contains(f.Line))
            {
                droppedUnchanged++;
                continue;
            }

            if (IsFragmentCompilerError(f))
            {
                droppedErrors++;
                continue;
            }

            kept.Add(ToFinding(f, file.Path, f.Line));
        }

        logger.LogInformation(
            "Analyzer pass {File} (full file): {Kept} kept, {DroppedErrors} compiler errors dropped, {DroppedUnchanged} findings on unchanged lines dropped",
            file.Path,
            kept.Count,
            droppedErrors,
            droppedUnchanged);

        return kept;
    }

    /// <summary>
    /// Fragment analysis on hunk text, used when no repo context is available. Analyzer
    /// lines are snippet-relative and map positionally back to real new-file lines.
    /// </summary>
    private async Task<List<Finding>> AnalyzeFragmentAsync(DiffFile file, CancellationToken cancellationToken)
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

            if (IsFragmentCompilerError(f))
            {
                droppedErrors++;
                continue;
            }

            kept.Add(ToFinding(f, file.Path, line.NewLineNumber));
        }

        logger.LogInformation(
            "Analyzer pass {File} (fragment): {Kept} kept, {DroppedErrors} fragment compiler errors dropped, {DroppedContext} context-line findings dropped, {DroppedOutOfRange} out of range",
            file.Path,
            kept.Count,
            droppedErrors,
            droppedContext,
            droppedOutOfRange);

        return kept;
    }

    /// <summary>
    /// CS compiler errors on partial input are almost always resolution failures (the
    /// analyzed text cannot see other files or package references), so they are noise
    /// rather than review findings. CS warnings and CA rules carry the real signal.
    /// </summary>
    private static bool IsFragmentCompilerError(StaticAnalysisFinding f) =>
        f.RuleId.StartsWith("CS", StringComparison.Ordinal)
        && string.Equals(f.Severity, "Error", StringComparison.OrdinalIgnoreCase);

    private static Finding ToFinding(StaticAnalysisFinding f, string path, int realLine) =>
        new(
            Issue: $"{f.RuleId}: {f.Message}",
            File: path,
            Line: realLine,
            Severity: Finding.ParseSeverity(f.Severity),
            Suggestion: null,
            Source: f.Source);

    private async Task<List<Finding>> GetLlmFindingsAsync(
        string diff,
        IReadOnlyList<DiffFile> files,
        IReadOnlyDictionary<string, string> contextFiles,
        IReadOnlyList<Finding> analyzerFindings,
        CancellationToken cancellationToken)
    {
        var userContent = BuildUserContent(diff, contextFiles);
        var response = await llm.CompleteAsync(
            new LlmRequest(SystemPrompt, userContent, ResponseSchema, options.Value.MaxOutputTokens),
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

    /// <summary>
    /// The LLM sees the diff first, then any fetched full files as delimited context
    /// sections, bounded by MaxContextChars so a large PR cannot run up the input bill.
    /// </summary>
    private string BuildUserContent(string diff, IReadOnlyDictionary<string, string> contextFiles)
    {
        if (contextFiles.Count == 0)
        {
            return diff;
        }

        var maxContextChars = options.Value.MaxContextChars;
        var builder = new StringBuilder(diff);
        var used = 0;
        var appended = 0;
        var skipped = 0;
        foreach (var (path, content) in contextFiles)
        {
            if (used + content.Length > maxContextChars)
            {
                skipped++;
                logger.LogInformation(
                    "Context for {File} skipped: {Chars} chars would exceed QualityAgent:MaxContextChars ({Max})",
                    path,
                    content.Length,
                    maxContextChars);
                continue;
            }

            builder.Append("\n\n<context file=\"").Append(path).Append("\">\n")
                .Append(content)
                .Append("\n</context>");
            used += content.Length;
            appended++;
        }

        logger.LogInformation(
            "LLM context: {Appended} file(s) appended ({Chars} chars), {Skipped} skipped",
            appended,
            used,
            skipped);

        return builder.ToString();
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
        8. Some reviews include <context file="..."> sections holding the full new version of
           changed files. Use them to judge complexity, naming, and duplication accurately,
           but findings must still point at lines the diff adds or changes.
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
