using System.Text;
using System.Text.Json;
using AgentReview.Agents.Configuration;
using AgentReview.Agents.Diff;
using AgentReview.Agents.GitHub;
using AgentReview.Agents.Llm;
using AgentReview.Agents.StaticAnalysis;
using Microsoft.Extensions.Logging;

namespace AgentReview.Agents;

/// <summary>
/// The shared review pipeline every diff-reviewing agent runs: guardrails, diff
/// parsing, a per-file static tool pass (full-file when GitHub context is available,
/// hunk fragments otherwise), one LLM pass with a schema-constrained response,
/// grounding of LLM findings against the diff, dedupe against tool findings, and
/// deterministic ordering. Agents supply the tool call, the system prompt, and a
/// noise filter. Fails closed on tool or LLM errors; missing GitHub context degrades.
/// </summary>
public abstract class DiffReviewAgentBase(
    ILlmProvider llm,
    IFileContentProvider fileContent,
    ReviewAgentOptions options,
    ILogger logger) : IReviewAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public abstract string Name { get; }

    /// <summary>The agent's LLM system prompt; the response schema is shared.</summary>
    protected abstract string SystemPrompt { get; }

    /// <summary>The agent's static tool call for one file's worth of code.</summary>
    protected abstract Task<IReadOnlyList<StaticAnalysisFinding>> RunToolAsync(string code, CancellationToken cancellationToken);

    /// <summary>
    /// Tool results to discard as noise inherent to analyzing partial input.
    /// Default keeps everything; Roslyn-backed agents drop CS compiler errors.
    /// </summary>
    protected virtual bool IsToolNoise(StaticAnalysisFinding finding) => false;

    public async Task<IReadOnlyList<Finding>> ReviewAsync(ReviewRequest request, CancellationToken cancellationToken = default)
    {
        var diff = request.Diff;
        if (diff.Length > options.MaxDiffChars)
        {
            throw new ArgumentException(
                $"Diff is {diff.Length} chars; this agent caps input at {options.MaxDiffChars} (MaxDiffChars).",
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
    /// Full-file analysis, used when GitHub context is available. Tool line numbers
    /// are real file lines, so no positional mapping is needed; findings are kept only
    /// on lines this diff added, keeping the review scoped to the change.
    /// </summary>
    private async Task<List<Finding>> AnalyzeFullFileAsync(DiffFile file, string fullContent, CancellationToken cancellationToken)
    {
        var raw = await RunToolAsync(fullContent, cancellationToken);
        var addedLines = file.NewLines.Where(l => l.IsAdded).Select(l => l.NewLineNumber).ToHashSet();

        var kept = new List<Finding>();
        int droppedNoise = 0, droppedUnchanged = 0;
        foreach (var f in raw)
        {
            if (!addedLines.Contains(f.Line))
            {
                droppedUnchanged++;
                continue;
            }

            if (IsToolNoise(f))
            {
                droppedNoise++;
                continue;
            }

            kept.Add(ToFinding(f, file.Path, f.Line));
        }

        logger.LogInformation(
            "Tool pass {File} (full file): {Kept} kept, {DroppedNoise} noise dropped, {DroppedUnchanged} findings on unchanged lines dropped",
            file.Path,
            kept.Count,
            droppedNoise,
            droppedUnchanged);

        return kept;
    }

    /// <summary>
    /// Fragment analysis on hunk text, used when no repo context is available. Tool
    /// lines are snippet-relative and map positionally back to real new-file lines.
    /// </summary>
    private async Task<List<Finding>> AnalyzeFragmentAsync(DiffFile file, CancellationToken cancellationToken)
    {
        var snippet = string.Join('\n', file.NewLines.Select(l => l.Content));
        var raw = await RunToolAsync(snippet, cancellationToken);

        var kept = new List<Finding>();
        int droppedNoise = 0, droppedContext = 0, droppedOutOfRange = 0;
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

            if (IsToolNoise(f))
            {
                droppedNoise++;
                continue;
            }

            kept.Add(ToFinding(f, file.Path, line.NewLineNumber));
        }

        logger.LogInformation(
            "Tool pass {File} (fragment): {Kept} kept, {DroppedNoise} noise dropped, {DroppedContext} context-line findings dropped, {DroppedOutOfRange} out of range",
            file.Path,
            kept.Count,
            droppedNoise,
            droppedContext,
            droppedOutOfRange);

        return kept;
    }

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
        IReadOnlyList<Finding> toolFindings,
        CancellationToken cancellationToken)
    {
        var userContent = BuildUserContent(diff, contextFiles);
        var response = await llm.CompleteAsync(
            new LlmRequest(SystemPrompt, userContent, ResponseSchema, options.MaxOutputTokens),
            cancellationToken);

        if (response.StopReason == "refusal")
        {
            throw new InvalidOperationException("The LLM refused the review request.");
        }

        if (response.StopReason == "max_tokens")
        {
            throw new InvalidOperationException(
                "The LLM response hit the output token cap and is likely truncated; raise MaxOutputTokens.");
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
        var toolLines = toolFindings.Select(f => (f.File, f.Line)).ToHashSet();

        var kept = new List<Finding>();
        foreach (var item in parsed.Findings)
        {
            if (!validLines.TryGetValue(item.File, out var lines) || !lines.Contains(item.Line))
            {
                logger.LogWarning("LLM finding dropped, not grounded in the diff: {File}:{Line}", item.File, item.Line);
                continue;
            }

            if (toolLines.Contains((item.File, item.Line)))
            {
                // Same line as a deterministic tool finding: almost always the same
                // observation restated, and the tool version is the one to trust.
                logger.LogInformation("LLM finding dropped, duplicates a tool finding at {File}:{Line}", item.File, item.Line);
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

        var maxContextChars = options.MaxContextChars;
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
                    "Context for {File} skipped: {Chars} chars would exceed MaxContextChars ({Max})",
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
