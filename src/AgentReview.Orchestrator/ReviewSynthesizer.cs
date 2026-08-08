using System.Text;
using System.Text.Json;
using AgentReview.Agents;
using AgentReview.Agents.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentReview.Orchestrator;

public sealed record SynthesizedReview(
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<AgentRunResult> Runs,
    int DuplicatesMerged,
    TimeSpan TotalElapsed);

/// <summary>
/// Merges the fan-out's per-agent findings into one ranked review. Cross-agent
/// duplicates cannot be detected deterministically (the same issue arrives in
/// different words), so an LLM arbiter clusters same-line findings that restate
/// one underlying issue. The arbiter only groups ids; it never rewrites, invents,
/// or deletes findings. Stated rules then pick each cluster's survivor:
/// tool-backed beats llm, then higher severity, then agent priority
/// (security, quality, docs), then ordinal issue text. The survivor keeps the
/// cluster's maximum severity. Arbiter failure degrades to no dedupe: a
/// redundant review is honest, a silently altered one is not.
/// </summary>
public sealed class ReviewSynthesizer(
    ILlmProvider llm,
    IOptions<SynthesisOptions> options,
    ILogger<ReviewSynthesizer> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] AgentPriority = ["security", "quality", "docs"];

    public async Task<SynthesizedReview> SynthesizeAsync(OrchestratedReview review, CancellationToken cancellationToken = default)
    {
        // Provenance: llm findings are re-stamped with their agent's name so the
        // merged review keeps saying exactly which LLM pass produced each finding.
        var attributed = new List<(string Agent, Finding Finding)>();
        foreach (var run in review.Runs.Where(r => r.Findings is not null))
        {
            foreach (var finding in run.Findings!)
            {
                attributed.Add((
                    run.Agent,
                    finding.Source == "llm" ? finding with { Source = $"{run.Agent}-llm" } : finding));
            }
        }

        // Ids are indices into the attributed list, so identical-looking findings
        // never collide and the pass-through rebuild below is positional.
        var indexed = attributed
            .Select((entry, id) => (Id: id, entry.Agent, entry.Finding))
            .ToList();
        var multiAgentCandidates = indexed
            .GroupBy(x => (x.Finding.File, x.Finding.Line))
            .Where(g => g.Select(x => x.Agent).Distinct().Count() > 1)
            .SelectMany(g => g)
            .ToList();

        var clusters = multiAgentCandidates.Count > 0 && options.Value.UseArbiter
            ? await TryGetClustersAsync(multiAgentCandidates, cancellationToken)
            : [];

        var mergedAway = new HashSet<int>();
        var upgrades = new Dictionary<int, FindingSeverity>();
        var duplicates = 0;
        foreach (var cluster in clusters)
        {
            var members = ValidateCluster(cluster, multiAgentCandidates);
            if (members.Count < 2)
            {
                continue;
            }

            var survivor = members
                .OrderByDescending(m => IsToolBacked(m.Finding))
                .ThenByDescending(m => m.Finding.Severity)
                .ThenBy(m => Array.IndexOf(AgentPriority, m.Agent) is var i and >= 0 ? i : int.MaxValue)
                .ThenBy(m => m.Finding.Issue, StringComparer.Ordinal)
                .First();
            var maxSeverity = members.Max(m => m.Finding.Severity);
            upgrades[survivor.Id] = maxSeverity;

            foreach (var member in members.Where(m => m.Id != survivor.Id))
            {
                mergedAway.Add(member.Id);
                duplicates++;
                logger.LogInformation(
                    "Merged duplicate at {File}:{Line}: kept {KeptSource}, dropped {DroppedSource}",
                    member.Finding.File,
                    member.Finding.Line,
                    survivor.Finding.Source,
                    member.Finding.Source);
            }
        }

        // Rebuild the final list: merged-away findings drop, survivors take the
        // cluster's max severity, everything else passes through.
        var final = new List<Finding>();
        foreach (var (id, _, finding) in indexed)
        {
            if (mergedAway.Contains(id))
            {
                continue;
            }

            final.Add(upgrades.TryGetValue(id, out var severity) && severity != finding.Severity
                ? finding with { Severity = severity }
                : finding);
        }

        var ranked = final
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Source, StringComparer.Ordinal)
            .ThenBy(f => f.Issue, StringComparer.Ordinal)
            .ToList();

        logger.LogInformation(
            "Synthesis: {Total} finding(s) from {Agents} agent(s), {Duplicates} duplicate(s) merged",
            ranked.Count,
            review.Runs.Count(r => r.Findings is not null),
            duplicates);

        return new SynthesizedReview(ranked, review.Runs, duplicates, review.TotalElapsed);
    }

    private static bool IsToolBacked(Finding finding) =>
        !finding.Source.EndsWith("-llm", StringComparison.Ordinal);

    /// <summary>
    /// A cluster is honored only when every id exists, ids are distinct, and all
    /// members sit on the same file and line. The arbiter cannot delete what it
    /// cannot correctly name.
    /// </summary>
    private List<(int Id, string Agent, Finding Finding)> ValidateCluster(
        IReadOnlyList<int> ids,
        List<(int Id, string Agent, Finding Finding)> candidates)
    {
        var members = new List<(int Id, string Agent, Finding Finding)>();
        foreach (var id in ids.Distinct())
        {
            var match = candidates.FirstOrDefault(c => c.Id == id);
            if (match.Finding is null)
            {
                logger.LogWarning("Arbiter referenced unknown finding id {Id}; cluster discarded", id);
                return [];
            }

            members.Add(match);
        }

        if (members.Select(m => (m.Finding.File, m.Finding.Line)).Distinct().Count() > 1)
        {
            logger.LogWarning("Arbiter clustered findings across different lines; cluster discarded");
            return [];
        }

        return members;
    }

    private async Task<List<List<int>>> TryGetClustersAsync(
        List<(int Id, string Agent, Finding Finding)> candidates,
        CancellationToken cancellationToken)
    {
        try
        {
            var prompt = new StringBuilder();
            foreach (var group in candidates.GroupBy(c => (c.Finding.File, c.Finding.Line)))
            {
                prompt.Append("Group ").Append(group.Key.File).Append(':').Append(group.Key.Line).Append('\n');
                foreach (var (id, _, finding) in group)
                {
                    prompt.Append("  [").Append(id).Append("] (").Append(finding.Source)
                        .Append(", ").Append(finding.Severity).Append(") ")
                        .Append(finding.Issue).Append('\n');
                }

                prompt.Append('\n');
            }

            var response = await llm.CompleteAsync(
                new LlmRequest(ArbiterSystemPrompt, prompt.ToString(), ArbiterSchema, options.Value.MaxOutputTokens),
                cancellationToken);
            if (response.StopReason is "refusal" or "max_tokens")
            {
                logger.LogWarning("Arbiter stopped with {StopReason}; skipping dedupe", response.StopReason);
                return [];
            }

            var parsed = JsonSerializer.Deserialize<ArbiterResponse>(response.Text, JsonOptions);
            return parsed?.Clusters.Select(c => c.Ids).ToList() ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Arbiter call failed; skipping dedupe");
            return [];
        }
    }

    private sealed record ArbiterResponse(List<ArbiterCluster> Clusters);

    private sealed record ArbiterCluster(List<int> Ids);

    private const string ArbiterSystemPrompt = """
        You are the synthesis arbiter in a multi-agent code review system. You receive
        groups of findings that different agents reported on the same file and line.
        Your only job is to identify which findings within a group are restatements of
        the same underlying issue. Findings about different problems on the same line
        stay unclustered. Never include an id in more than one cluster, and never
        cluster ids from different groups. Return an empty clusters array when nothing
        is a duplicate.
        """;

    private const string ArbiterSchema = """
        {
          "type": "object",
          "properties": {
            "clusters": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "ids": { "type": "array", "items": { "type": "integer" } }
                },
                "required": ["ids"],
                "additionalProperties": false
              }
            }
          },
          "required": ["clusters"],
          "additionalProperties": false
        }
        """;
}
