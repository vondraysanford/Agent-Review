namespace AgentReview.Orchestrator;

public sealed class SynthesisOptions
{
    public const string SectionName = "Synthesis";

    /// <summary>
    /// When true, an LLM arbiter clusters same-line findings that restate one
    /// underlying issue before the stated survivor rules run. When false, all
    /// same-line groups pass through unmerged (fully deterministic mode).
    /// </summary>
    public bool UseArbiter { get; set; } = true;

    /// <summary>Output cap for the arbiter call; its answer is a tiny id list.</summary>
    public int MaxOutputTokens { get; set; } = 1024;
}
