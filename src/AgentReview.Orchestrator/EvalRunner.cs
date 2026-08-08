using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentReview.Agents;
using AgentReview.Agents.Diff;
using Microsoft.Extensions.Logging;

namespace AgentReview.Orchestrator;

/// <summary>
/// Runs the full pipeline over the seeded eval cases and scores the results.
/// Two modes: a live run writes machine.json (recall, run costs) plus
/// worksheet.json (unmatched findings pending human verdicts); --score re-reads
/// the filled worksheet without any LLM call and writes final.json with per-agent
/// precision and the human-agreement rate.
/// </summary>
public sealed class EvalRunner(
    ReviewOrchestrator orchestrator,
    ReviewSynthesizer synthesizer,
    RunSummaryCollector collector,
    ILogger<EvalRunner> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<int> RunAsync(string casesDir, string resultsDir, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(casesDir))
        {
            Console.Error.WriteLine($"Eval cases directory not found: {casesDir}");
            return 2;
        }

        Directory.CreateDirectory(resultsDir);
        var caseResults = new List<Dictionary<string, object?>>();
        var worksheet = new List<WorksheetEntry>();

        foreach (var caseDir in Directory.GetDirectories(casesDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(caseDir);
            var diff = await File.ReadAllTextAsync(Path.Combine(caseDir, "diff.diff"), cancellationToken);
            var labels = JsonSerializer.Deserialize<List<EvalLabel>>(
                await File.ReadAllTextAsync(Path.Combine(caseDir, "labels.json"), cancellationToken), Json) ?? [];

            SynthesizedReview synthesized;
            ActivityTraceId traceId;
            using (var root = new Activity($"eval.{name}").Start())
            {
                traceId = root.TraceId;
                var fanOut = await orchestrator.ReviewAsync(new ReviewRequest(diff), cancellationToken);
                synthesized = await synthesizer.SynthesizeAsync(fanOut, cancellationToken);
            }

            var summary = collector.Collect(traceId);

            var perAgent = new Dictionary<string, AgentScore>();
            foreach (var run in synthesized.Runs.Where(r => r.Findings is not null))
            {
                var score = EvalScorer.ScoreAgent(run.Agent, run.Findings!, labels);
                perAgent[run.Agent] = score;

                foreach (var finding in run.Findings!)
                {
                    var matched = labels.Any(l => EvalScorer.Matches(finding, l, run.Agent));
                    if (!matched)
                    {
                        worksheet.Add(new WorksheetEntry(
                            name, run.Agent, finding.Source, finding.File, finding.Line, finding.Issue, "pending"));
                    }
                }
            }

            var synthesizedScore = ScoreSynthesized(synthesized.Findings, labels);
            caseResults.Add(new Dictionary<string, object?>
            {
                ["case"] = name,
                ["labels"] = labels.Count,
                ["perAgent"] = perAgent,
                ["synthesized"] = synthesizedScore,
                ["latencySeconds"] = Math.Round(summary.TotalLatency.TotalSeconds, 1),
                ["inputTokens"] = summary.InputTokens,
                ["outputTokens"] = summary.OutputTokens,
                ["llmCalls"] = summary.LlmCalls,
                ["toolCalls"] = summary.ToolCalls,
                ["toolFailures"] = summary.ToolFailures,
                ["costUsd"] = summary.EstimatedCostUsd,
            });

            logger.LogInformation(
                "Eval {Case}: {Labels} label(s), synthesized {Found}/{Total} found, {Cost} USD",
                name, labels.Count, synthesizedScore.LabelsFound, synthesizedScore.LabelsTotal, summary.EstimatedCostUsd);
        }

        var machine = new Dictionary<string, object?>
        {
            ["generatedAtUtc"] = DateTime.UtcNow.ToString("O"),
            ["cases"] = caseResults,
        };
        await File.WriteAllTextAsync(Path.Combine(resultsDir, "machine.json"), JsonSerializer.Serialize(machine, Json) + "\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(resultsDir, "worksheet.json"), JsonSerializer.Serialize(worksheet, Json) + "\n", cancellationToken);

        // House rule: human judgment gets human-readable context. The worksheet
        // never ships without its review guide.
        var caseDiffs = new List<(string Case, string Diff)>();
        foreach (var caseDir in Directory.GetDirectories(casesDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            caseDiffs.Add((Path.GetFileName(caseDir), await File.ReadAllTextAsync(Path.Combine(caseDir, "diff.diff"), cancellationToken)));
        }

        await File.WriteAllTextAsync(Path.Combine(resultsDir, "review-guide.md"), BuildReviewGuide(caseDiffs, worksheet), cancellationToken);

        Console.WriteLine($"Eval run complete: {caseResults.Count} case(s); {worksheet.Count} unmatched finding(s) pending human verdicts in worksheet.json (see review-guide.md)");
        return 0;
    }

    /// <summary>
    /// The synthesized review is scored against all labels; agent attribution
    /// comes from the finding's source.
    /// </summary>
    private static AgentScore ScoreSynthesized(IReadOnlyList<Finding> findings, IReadOnlyList<EvalLabel> labels)
    {
        var labelsFound = labels.Count(label =>
            findings.Any(f => EvalScorer.Matches(f, label, EvalScorer.AgentFromSource(f.Source))));
        var findingsMatched = findings.Count(f =>
            labels.Any(label => EvalScorer.Matches(f, label, EvalScorer.AgentFromSource(f.Source))));
        return new AgentScore(labels.Count, labelsFound, findings.Count, findingsMatched);
    }

    /// <summary>
    /// Renders the human review guide: each case's new-side code reconstructed from
    /// its diff with real line numbers, followed by that case's pending findings
    /// tagged with their worksheet indices.
    /// </summary>
    public static string BuildReviewGuide(
        IReadOnlyList<(string Case, string Diff)> cases,
        IReadOnlyList<WorksheetEntry> worksheet)
    {
        var guide = new StringBuilder();
        guide.AppendLine("# Worksheet review guide");
        guide.AppendLine();
        guide.AppendLine("Generated with the eval run. For each finding, read the code and set the");
        guide.AppendLine("matching worksheet.json entry (index shown) to \"agree\" or \"disagree\".");
        guide.AppendLine();

        foreach (var (caseName, diff) in cases)
        {
            var pending = worksheet
                .Select((entry, index) => (Entry: entry, Index: index))
                .Where(x => x.Entry.Case == caseName)
                .ToList();

            guide.Append("## Case: ").AppendLine(caseName);
            guide.AppendLine();
            foreach (var file in UnifiedDiffParser.Parse(diff).Where(f => f.NewLines.Count > 0))
            {
                guide.Append("### ").AppendLine(file.Path);
                guide.AppendLine(file.IsCSharp ? "```csharp" : "```");
                foreach (var line in file.NewLines)
                {
                    guide.Append($"{line.NewLineNumber,3}  ").AppendLine(line.Content);
                }

                guide.AppendLine("```");
                guide.AppendLine();
            }

            guide.AppendLine(pending.Count == 0 ? "No pending findings." : "Pending findings:");
            foreach (var (entry, index) in pending)
            {
                guide.AppendLine(
                    $"- **[worksheet index {index}]** `{entry.File}:{entry.Line}` ({entry.Agent}/{entry.Source}): {entry.Issue}");
            }

            guide.AppendLine();
        }

        return guide.ToString();
    }

    /// <summary>Free pass: recompute final metrics from the human-filled worksheet.</summary>
    public async Task<int> ScoreAsync(string resultsDir, CancellationToken cancellationToken = default)
    {
        var worksheetPath = Path.Combine(resultsDir, "worksheet.json");
        var machinePath = Path.Combine(resultsDir, "machine.json");
        if (!File.Exists(worksheetPath) || !File.Exists(machinePath))
        {
            Console.Error.WriteLine("Run --eval first; machine.json and worksheet.json are required.");
            return 2;
        }

        var worksheet = JsonSerializer.Deserialize<List<WorksheetEntry>>(
            await File.ReadAllTextAsync(worksheetPath, cancellationToken), Json) ?? [];
        var pending = worksheet.Count(w => w.Verdict == "pending");
        if (pending > 0)
        {
            Console.Error.WriteLine($"{pending} worksheet entr(ies) still pending; set each verdict to agree or disagree first.");
            return 2;
        }

        using var machineDoc = JsonDocument.Parse(await File.ReadAllTextAsync(machinePath, cancellationToken));
        var perAgentFinal = new Dictionary<string, object>();
        foreach (var agent in ReviewOrchestrator.AgentNames)
        {
            var matched = 0;
            foreach (var c in machineDoc.RootElement.GetProperty("cases").EnumerateArray())
            {
                if (c.GetProperty("perAgent").TryGetProperty(agent, out var score))
                {
                    matched += score.GetProperty("findingsMatched").GetInt32();
                }
            }

            var agreed = worksheet.Count(w => w.Agent == agent && w.Verdict == "agree");
            var disagreed = worksheet.Count(w => w.Agent == agent && w.Verdict == "disagree");
            perAgentFinal[agent] = new
            {
                matched,
                agreed,
                disagreed,
                precision = EvalScorer.Precision(matched, agreed, disagreed),
            };
        }

        var totalMatched = 0;
        var totalFindings = 0;
        foreach (var c in machineDoc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var synth = c.GetProperty("synthesized");
            totalMatched += synth.GetProperty("findingsMatched").GetInt32();
            totalFindings += synth.GetProperty("findingsTotal").GetInt32();
        }

        var agreedAll = worksheet.Count(w => w.Verdict == "agree");
        var final = new
        {
            generatedAtUtc = DateTime.UtcNow.ToString("O"),
            perAgent = perAgentFinal,
            humanAgreement = new
            {
                agreedOrMatched = totalMatched + agreedAll,
                totalReviewed = worksheet.Count + totalMatched,
                rate = worksheet.Count + totalMatched == 0
                    ? (double?)null
                    : (double)(totalMatched + agreedAll) / (worksheet.Count + totalMatched),
            },
        };

        await File.WriteAllTextAsync(Path.Combine(resultsDir, "final.json"), JsonSerializer.Serialize(final, Json) + "\n", cancellationToken);
        Console.WriteLine("final.json written; the README results table can now be filled with measured numbers.");
        return 0;
    }
}
