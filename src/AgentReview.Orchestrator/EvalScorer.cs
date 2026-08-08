using AgentReview.Agents;

namespace AgentReview.Orchestrator;

/// <summary>One planted, labeled issue in an eval case.</summary>
public sealed record EvalLabel(string Id, string File, int Line, string[] Agents, string Kind, string? Note);

/// <summary>
/// One agent's score against a case's labels. Recall is fully determined by the
/// labels; precision needs the human worksheet because an unmatched finding may
/// still be right.
/// </summary>
public sealed record AgentScore(int LabelsTotal, int LabelsFound, int FindingsTotal, int FindingsMatched)
{
    public double? Recall => LabelsTotal == 0 ? null : (double)LabelsFound / LabelsTotal;
}

/// <summary>An unmatched finding awaiting a human agree/disagree verdict.</summary>
public sealed record WorksheetEntry(
    string Case,
    string Agent,
    string Source,
    string File,
    int Line,
    string Issue,
    string Verdict);

/// <summary>
/// Pure scoring rules for the eval harness. The matching rule is deliberately
/// strict and stated: a finding matches a label iff same file, same line, and the
/// finding's agent is listed on the label. Off-by-one lines do not match; honest
/// numbers beat flattering ones.
/// </summary>
public static class EvalScorer
{
    public static bool Matches(Finding finding, EvalLabel label, string agent) =>
        string.Equals(finding.File, label.File, StringComparison.Ordinal)
        && finding.Line == label.Line
        && label.Agents.Contains(agent, StringComparer.Ordinal);

    /// <summary>Maps a synthesized finding's source back to the agent that produced it.</summary>
    public static string AgentFromSource(string source) => source switch
    {
        "roslyn" => "quality",
        "semgrep" => "security",
        _ when source.EndsWith("-llm", StringComparison.Ordinal) => source[..^4],
        _ => source,
    };

    public static AgentScore ScoreAgent(string agent, IReadOnlyList<Finding> findings, IReadOnlyList<EvalLabel> labels)
    {
        var agentLabels = labels.Where(l => l.Agents.Contains(agent, StringComparer.Ordinal)).ToList();
        var labelsFound = agentLabels.Count(label => findings.Any(f => Matches(f, label, agent)));
        var findingsMatched = findings.Count(f => agentLabels.Any(label => Matches(f, label, agent)));
        return new AgentScore(agentLabels.Count, labelsFound, findings.Count, findingsMatched);
    }

    /// <summary>
    /// Final precision once the worksheet is resolved: matched findings count as
    /// correct by construction; unmatched ones count per the human verdict.
    /// </summary>
    public static double? Precision(int matched, int agreed, int disagreed)
    {
        var total = matched + agreed + disagreed;
        return total == 0 ? null : (double)(matched + agreed) / total;
    }
}
