namespace AgentReview.Agents.Configuration;

/// <summary>
/// The quality agent's cost guardrails, enforced in code before and during each review.
/// </summary>
public sealed class QualityAgentOptions
{
    public const string SectionName = "QualityAgent";

    /// <summary>
    /// Input cap. Characters are a cheap proxy for input tokens (roughly 3 to 4 chars
    /// per token), so the default caps LLM input near 15-20K tokens. Reviews over the
    /// cap fail fast, before any tool or LLM spend.
    /// </summary>
    public int MaxDiffChars { get; set; } = 60_000;

    /// <summary>Output token cap for the agent's single LLM call.</summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>
    /// Cap on the total GitHub-fetched file context appended to the LLM prompt.
    /// Files that would push past the cap are skipped, with a log line.
    /// </summary>
    public int MaxContextChars { get; set; } = 40_000;
}
