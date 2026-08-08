using AgentReview.Agents;
using AgentReview.Agents.GitHub;
using AgentReview.Api;
using AgentReview.Orchestrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentReview.Agents.Configuration;
using AgentReview.Agents.Llm;
using AgentReview.Agents.StaticAnalysis;
using Xunit;

namespace AgentReview.Tests;

/// <summary>
/// Handler-level tests for the /review endpoint: validation, diff mode, PR mode,
/// and failure shaping, all against the fake-backed pipeline (no web stack, no LLM).
/// </summary>
public class ReviewEndpointTests
{
    private const string CsDiff = """
        diff --git a/src/A.cs b/src/A.cs
        index 1111111..2222222 100644
        --- a/src/A.cs
        +++ b/src/A.cs
        @@ -1,2 +1,3 @@
         public class A
         {
        +    public int X;
         }
        """;

    private sealed class FakePullRequestClient : IPullRequestClient
    {
        public string? Diff { get; init; }
        public List<int> RequestedNumbers { get; } = [];

        public Task<string?> GetPullRequestDiffAsync(RepoReference repo, int number, CancellationToken cancellationToken = default)
        {
            RequestedNumbers.Add(number);
            return Task.FromResult(Diff);
        }
    }

    private static (ServiceProvider Provider, RunSummaryCollector Collector) BuildPipeline(
        IStaticAnalysisClient? analysis = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmProvider>(new FakeLlmProvider());
        services.AddSingleton(analysis ?? new FakeStaticAnalysisClient());
        services.AddSingleton<IFileContentProvider>(new FakeFileContentProvider());
        services.AddSingleton(Options.Create(new QualityAgentOptions()));
        services.AddSingleton(Options.Create(new SecurityAgentOptions()));
        services.AddSingleton(Options.Create(new DocsAgentOptions()));
        services.AddSingleton(Options.Create(new SynthesisOptions()));
        services.AddSingleton(Options.Create(new BudgetOptions()));
        services.AddSingleton(Options.Create(new PricingOptions()));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddKeyedSingleton<IReviewAgent, QualityAgent>("quality");
        services.AddKeyedSingleton<IReviewAgent, SecurityAgent>("security");
        services.AddKeyedSingleton<IReviewAgent, DocsAgent>("docs");
        services.AddSingleton<BudgetGuard>();
        services.AddSingleton<ReviewOrchestrator>();
        services.AddSingleton<ReviewSynthesizer>();
        var provider = services.BuildServiceProvider();
        return (provider, new RunSummaryCollector(Options.Create(new PricingOptions())));
    }

    private static Task<ReviewOutcome> HandleAsync(
        ReviewApiRequest request,
        ServiceProvider provider,
        RunSummaryCollector collector,
        IPullRequestClient? pullRequests = null) =>
        ReviewEndpoint.HandleAsync(
            request,
            provider.GetRequiredService<ReviewOrchestrator>(),
            provider.GetRequiredService<ReviewSynthesizer>(),
            collector,
            pullRequests ?? new FakePullRequestClient(),
            CancellationToken.None);

    [Fact]
    public async Task RawDiff_ReturnsAcceptedWithRunsAndSummary()
    {
        var (provider, collector) = BuildPipeline();
        using (provider)
        using (collector)
        {
            var outcome = await HandleAsync(new ReviewApiRequest(CsDiff, null, null), provider, collector);

            var accepted = Assert.IsType<ReviewAccepted>(outcome);
            Assert.Equal(3, accepted.Runs.Count);
            Assert.NotNull(accepted.Summary);
        }
    }

    [Fact]
    public async Task EmptyBody_Returns400()
    {
        var (provider, collector) = BuildPipeline();
        using (provider)
        using (collector)
        {
            var outcome = await HandleAsync(new ReviewApiRequest(null, null, null), provider, collector);

            var rejected = Assert.IsType<ReviewRejected>(outcome);
            Assert.Equal(400, rejected.StatusCode);
        }
    }

    [Fact]
    public async Task PullRequestMode_FetchesDiffThroughClient()
    {
        var (provider, collector) = BuildPipeline();
        var pullRequests = new FakePullRequestClient { Diff = CsDiff };
        using (provider)
        using (collector)
        {
            var outcome = await HandleAsync(
                new ReviewApiRequest(null, null, new PullRequestRef("vondraysanford", "Agent-Review", 7)),
                provider,
                collector,
                pullRequests);

            Assert.IsType<ReviewAccepted>(outcome);
            Assert.Equal([7], pullRequests.RequestedNumbers);
        }
    }

    [Fact]
    public async Task PullRequestFetchFails_Returns502()
    {
        var (provider, collector) = BuildPipeline();
        using (provider)
        using (collector)
        {
            var outcome = await HandleAsync(
                new ReviewApiRequest(null, null, new PullRequestRef("o", "r", 1)),
                provider,
                collector,
                new FakePullRequestClient { Diff = null });

            var rejected = Assert.IsType<ReviewRejected>(outcome);
            Assert.Equal(502, rejected.StatusCode);
        }
    }

    [Fact]
    public async Task NotADiff_Returns400()
    {
        var (provider, collector) = BuildPipeline();
        using (provider)
        using (collector)
        {
            var outcome = await HandleAsync(new ReviewApiRequest("hello, not a diff", null, null), provider, collector);

            var rejected = Assert.IsType<ReviewRejected>(outcome);
            Assert.Equal(400, rejected.StatusCode);
        }
    }
}
