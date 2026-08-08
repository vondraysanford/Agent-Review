using AgentReview.Agents;
using AgentReview.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentReview.Tests;

/// <summary>
/// Pins the synthesis contract: the arbiter only clusters, stated rules pick
/// survivors, arbiter failure degrades to no dedupe, and provenance survives.
/// </summary>
public class ReviewSynthesizerTests
{
    private static Finding F(string file, int line, FindingSeverity severity, string source, string issue = "issue") =>
        new(issue, file, line, severity, null, source);

    private static OrchestratedReview Review(params AgentRunResult[] runs) =>
        new(runs, TimeSpan.FromSeconds(1));

    private static AgentRunResult Run(string agent, params Finding[] findings) =>
        new(agent, findings, null, TimeSpan.FromSeconds(1));

    private static ReviewSynthesizer CreateSynthesizer(AgentReview.Agents.Llm.ILlmProvider llm, bool useArbiter = true) =>
        new(
            llm,
            Options.Create(new SynthesisOptions { UseArbiter = useArbiter }),
            NullLogger<ReviewSynthesizer>.Instance);

    [Fact]
    public async Task SingleFindingGroups_PassThrough_NoArbiterCall()
    {
        var llm = new FakeLlmProvider();
        var synthesizer = CreateSynthesizer(llm);
        var review = Review(
            Run("quality", F("a.cs", 1, FindingSeverity.Warning, "roslyn")),
            Run("security", F("a.cs", 2, FindingSeverity.Error, "semgrep")),
            Run("docs", F("b.cs", 1, FindingSeverity.Info, "llm")));

        var result = await synthesizer.SynthesizeAsync(review);

        Assert.Equal(0, llm.Calls);
        Assert.Equal(3, result.Findings.Count);
        Assert.Equal(0, result.DuplicatesMerged);
        Assert.Equal(FindingSeverity.Error, result.Findings[0].Severity);
    }

    [Fact]
    public async Task ArbiterCluster_ToolBeatsLlm_SeverityUpgraded()
    {
        var llm = new FakeLlmProvider { ResponseText = """{"clusters":[{"ids":[0,1]}]}""" };
        var synthesizer = CreateSynthesizer(llm);
        var review = Review(
            Run("security", F("a.cs", 13, FindingSeverity.Warning, "semgrep", "sqli detected")),
            Run("quality", F("a.cs", 13, FindingSeverity.Error, "llm", "string concatenation in sql")));

        var result = await synthesizer.SynthesizeAsync(review);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("semgrep", finding.Source);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
        Assert.Equal(1, result.DuplicatesMerged);
    }

    [Fact]
    public async Task LlmVsLlm_HigherSeverityWins_ThenAgentPriority()
    {
        var llm = new FakeLlmProvider { ResponseText = """{"clusters":[{"ids":[0,1]}]}""" };
        var synthesizer = CreateSynthesizer(llm);
        var review = Review(
            Run("security", F("a.cs", 8, FindingSeverity.Warning, "llm", "hardcoded credentials")),
            Run("quality", F("a.cs", 8, FindingSeverity.Error, "llm", "hardcoded connection string")));

        var result = await synthesizer.SynthesizeAsync(review);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("quality-llm", finding.Source);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
    }

    [Fact]
    public async Task SameLine_DistinctIssues_NoCluster_BothKept()
    {
        var llm = new FakeLlmProvider { ResponseText = """{"clusters":[]}""" };
        var synthesizer = CreateSynthesizer(llm);
        var review = Review(
            Run("quality", F("a.cs", 10, FindingSeverity.Warning, "roslyn", "CA1822: can be static")),
            Run("docs", F("a.cs", 10, FindingSeverity.Warning, "llm", "missing XML docs")));

        var result = await synthesizer.SynthesizeAsync(review);

        Assert.Equal(1, llm.Calls);
        Assert.Equal(2, result.Findings.Count);
        Assert.Equal(0, result.DuplicatesMerged);
    }

    [Fact]
    public async Task ArbiterFailure_DegradesToNoDedupe()
    {
        var llm = new ThrowingLlmProvider();
        var synthesizer = CreateSynthesizer(llm);
        var review = Review(
            Run("security", F("a.cs", 13, FindingSeverity.Error, "semgrep")),
            Run("quality", F("a.cs", 13, FindingSeverity.Warning, "llm")));

        var result = await synthesizer.SynthesizeAsync(review);

        Assert.Equal(2, result.Findings.Count);
        Assert.Equal(0, result.DuplicatesMerged);
    }

    [Fact]
    public async Task ArbiterInventedIds_ClusterDiscarded()
    {
        var llm = new FakeLlmProvider { ResponseText = """{"clusters":[{"ids":[0,99]}]}""" };
        var synthesizer = CreateSynthesizer(llm);
        var review = Review(
            Run("security", F("a.cs", 13, FindingSeverity.Error, "semgrep")),
            Run("quality", F("a.cs", 13, FindingSeverity.Warning, "llm")));

        var result = await synthesizer.SynthesizeAsync(review);

        Assert.Equal(2, result.Findings.Count);
        Assert.Equal(0, result.DuplicatesMerged);
    }

    [Fact]
    public async Task LlmSources_RestampedWithAgentName_ToolSourcesUntouched()
    {
        var synthesizer = CreateSynthesizer(new FakeLlmProvider());
        var review = Review(
            Run("quality", F("a.cs", 1, FindingSeverity.Warning, "roslyn"), F("a.cs", 2, FindingSeverity.Info, "llm")),
            Run("docs", F("b.cs", 1, FindingSeverity.Info, "llm")));

        var result = await synthesizer.SynthesizeAsync(review);

        Assert.Contains(result.Findings, f => f.Source == "roslyn");
        Assert.Contains(result.Findings, f => f.Source == "quality-llm");
        Assert.Contains(result.Findings, f => f.Source == "docs-llm");
        Assert.DoesNotContain(result.Findings, f => f.Source == "llm");
    }

    [Fact]
    public async Task FailedRun_SurfacedInRuns_NotInFindings()
    {
        var synthesizer = CreateSynthesizer(new FakeLlmProvider());
        var review = Review(
            Run("quality", F("a.cs", 1, FindingSeverity.Warning, "roslyn")),
            new AgentRunResult("security", null, "MCP server down", TimeSpan.FromSeconds(1)));

        var result = await synthesizer.SynthesizeAsync(review);

        Assert.Single(result.Findings);
        Assert.Contains(result.Runs, r => r.Agent == "security" && r.Error == "MCP server down");
    }

    private sealed class ThrowingLlmProvider : AgentReview.Agents.Llm.ILlmProvider
    {
        public Task<AgentReview.Agents.Llm.LlmResponse> CompleteAsync(
            AgentReview.Agents.Llm.LlmRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("arbiter down");
    }
}
