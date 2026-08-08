using AgentReview.Agents;
using AgentReview.Agents.Configuration;
using AgentReview.Orchestrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentReview.Tests;

/// <summary>
/// Pins the eval scoring rules (strict matching, recall, precision with human
/// verdicts) and the pre-spend budget guardrail. No LLM anywhere.
/// </summary>
public class EvalScoringTests
{
    private static Finding F(string file, int line, string source = "roslyn") =>
        new("issue", file, line, FindingSeverity.Warning, null, source);

    private static EvalLabel L(string file, int line, params string[] agents) =>
        new("id", file, line, agents, "kind", null);

    [Fact]
    public void Matching_IsExactOnFileLineAndAgent()
    {
        var label = L("a.cs", 10, "quality");

        Assert.True(EvalScorer.Matches(F("a.cs", 10), label, "quality"));
        Assert.False(EvalScorer.Matches(F("a.cs", 11), label, "quality"), "off-by-one line must not match");
        Assert.False(EvalScorer.Matches(F("b.cs", 10), label, "quality"), "different file must not match");
        Assert.False(EvalScorer.Matches(F("a.cs", 10), label, "security"), "agent not on the label must not match");
    }

    [Fact]
    public void AgentScore_RecallAndMatchedCounts()
    {
        var labels = new List<EvalLabel>
        {
            L("a.cs", 10, "quality"),
            L("a.cs", 20, "quality"),
            L("a.cs", 30, "security"),
        };
        var findings = new List<Finding> { F("a.cs", 10), F("a.cs", 99) };

        var score = EvalScorer.ScoreAgent("quality", findings, labels);

        Assert.Equal(2, score.LabelsTotal);       // only quality labels count
        Assert.Equal(1, score.LabelsFound);
        Assert.Equal(0.5, score.Recall);
        Assert.Equal(2, score.FindingsTotal);
        Assert.Equal(1, score.FindingsMatched);   // the line-99 finding goes to the worksheet
    }

    [Fact]
    public void AgentScore_NoLabels_RecallIsNull()
    {
        var score = EvalScorer.ScoreAgent("docs", [F("a.cs", 1)], []);

        Assert.Null(score.Recall);
        Assert.Equal(1, score.FindingsTotal);
    }

    [Fact]
    public void Precision_CombinesMatchedAndHumanVerdicts()
    {
        Assert.Equal(0.75, EvalScorer.Precision(matched: 2, agreed: 1, disagreed: 1));
        Assert.Null(EvalScorer.Precision(0, 0, 0));
        Assert.Equal(1.0, EvalScorer.Precision(3, 0, 0));
    }

    [Fact]
    public void AgentFromSource_MapsAllLanes()
    {
        Assert.Equal("quality", EvalScorer.AgentFromSource("roslyn"));
        Assert.Equal("security", EvalScorer.AgentFromSource("semgrep"));
        Assert.Equal("quality", EvalScorer.AgentFromSource("quality-llm"));
        Assert.Equal("docs", EvalScorer.AgentFromSource("docs-llm"));
    }

    [Fact]
    public void BudgetGuard_WorstCase_IsDeterministicFromCaps()
    {
        var guard = CreateGuard(maxPerReviewUsd: 0);

        // 3 agents at (60000+40000)/3+2000 = 35,333 input tokens, 4096 output each;
        // arbiter adds 35,333 input and 1024 output. At 5/25 per MTok: ~$1.04.
        var worstCase = guard.EstimateWorstCaseUsd();

        Assert.InRange(worstCase, 1.00m, 1.10m);
    }

    [Fact]
    public void BudgetGuard_OverBudget_Throws_ZeroDisables()
    {
        var overBudget = CreateGuard(maxPerReviewUsd: 0.10m);
        var ex = Assert.Throws<InvalidOperationException>(overBudget.EnsureWithinBudget);
        Assert.Contains("MaxPerReviewUsd", ex.Message);

        var disabled = CreateGuard(maxPerReviewUsd: 0);
        disabled.EnsureWithinBudget(); // no throw
    }

    private static BudgetGuard CreateGuard(decimal maxPerReviewUsd)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new QualityAgentOptions()));
        services.AddSingleton(Options.Create(new SecurityAgentOptions()));
        services.AddSingleton(Options.Create(new DocsAgentOptions()));
        services.AddSingleton(Options.Create(new SynthesisOptions()));
        var provider = services.BuildServiceProvider();
        return new BudgetGuard(
            provider,
            Options.Create(new BudgetOptions { MaxPerReviewUsd = maxPerReviewUsd }),
            Options.Create(new PricingOptions { InputPerMillionTokens = 5m, OutputPerMillionTokens = 25m }));
    }
}
