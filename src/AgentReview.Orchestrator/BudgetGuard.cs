using AgentReview.Agents.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentReview.Orchestrator;

public sealed class BudgetOptions
{
    public const string SectionName = "Budget";

    /// <summary>Hard ceiling on one review's worst-case cost in USD; 0 disables the check.</summary>
    public decimal MaxPerReviewUsd { get; set; }
}

/// <summary>
/// The per-review budget, enforced before any spend: every input and output is
/// already capped in code (MaxDiffChars, MaxContextChars, MaxOutputTokens), so
/// the worst case is deterministic arithmetic over those caps and the configured
/// rates. A review whose worst case exceeds the budget refuses to start; nothing
/// is billed to find out it was too expensive.
/// </summary>
public sealed class BudgetGuard(
    IServiceProvider services,
    IOptions<BudgetOptions> budget,
    IOptions<PricingOptions> pricing)
{
    /// <summary>Conservative chars-per-token floor; real C# averages 3.5 to 4.</summary>
    private const int CharsPerToken = 3;

    /// <summary>Headroom for system prompts, schemas, and message framing per call.</summary>
    private const int PromptOverheadTokens = 2000;

    public decimal EstimateWorstCaseUsd()
    {
        var p = pricing.Value;
        if (p.InputPerMillionTokens <= 0 && p.OutputPerMillionTokens <= 0)
        {
            return 0m;
        }

        ReviewAgentOptions[] agents =
        [
            services.GetRequiredService<IOptions<QualityAgentOptions>>().Value,
            services.GetRequiredService<IOptions<SecurityAgentOptions>>().Value,
            services.GetRequiredService<IOptions<DocsAgentOptions>>().Value,
        ];
        var synthesis = services.GetRequiredService<IOptions<SynthesisOptions>>().Value;

        long inputTokens = 0;
        long outputTokens = 0;
        foreach (var agent in agents)
        {
            inputTokens += (agent.MaxDiffChars + agent.MaxContextChars) / CharsPerToken + PromptOverheadTokens;
            outputTokens += agent.MaxOutputTokens;
        }

        // The arbiter's input is really bounded by the agents' outputs; the largest
        // agent input bound is a comfortable ceiling.
        inputTokens += agents.Max(a => (a.MaxDiffChars + a.MaxContextChars) / CharsPerToken) + PromptOverheadTokens;
        outputTokens += synthesis.MaxOutputTokens;

        return (inputTokens * p.InputPerMillionTokens + outputTokens * p.OutputPerMillionTokens) / 1_000_000m;
    }

    public void EnsureWithinBudget()
    {
        var max = budget.Value.MaxPerReviewUsd;
        if (max <= 0)
        {
            return;
        }

        var worstCase = EstimateWorstCaseUsd();
        if (worstCase > max)
        {
            throw new InvalidOperationException(
                $"Worst-case review cost ~${worstCase:F2} exceeds Budget:MaxPerReviewUsd ${max:F2}; lower the agent caps or raise the budget.");
        }
    }
}
