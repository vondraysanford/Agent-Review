using AgentReview.Agents;
using AgentReview.Agents.Configuration;
using AgentReview.Agents.GitHub;
using AgentReview.Agents.Llm;
using AgentReview.Agents.StaticAnalysis;
using AgentReview.Orchestrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentReview.Tests;

/// <summary>
/// Pins the fan-out contract: all agents run, they run concurrently, and one
/// failing agent never sinks the others.
/// </summary>
public class ReviewOrchestratorTests
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

    private static ServiceProvider BuildProvider(ILlmProvider llm, IStaticAnalysisClient analysis)
    {
        var services = new ServiceCollection();
        services.AddSingleton(llm);
        services.AddSingleton(analysis);
        services.AddSingleton<IFileContentProvider>(new FakeFileContentProvider());
        services.AddSingleton(Options.Create(new QualityAgentOptions()));
        services.AddSingleton(Options.Create(new SecurityAgentOptions()));
        services.AddSingleton(Options.Create(new DocsAgentOptions()));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddKeyedSingleton<IReviewAgent, QualityAgent>("quality");
        services.AddKeyedSingleton<IReviewAgent, SecurityAgent>("security");
        services.AddKeyedSingleton<IReviewAgent, DocsAgent>("docs");
        services.AddSingleton<ReviewOrchestrator>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task FanOut_RunsAllThreeAgents()
    {
        var llm = new FakeLlmProvider();
        using var provider = BuildProvider(llm, new FakeStaticAnalysisClient());
        var orchestrator = provider.GetRequiredService<ReviewOrchestrator>();

        var review = await orchestrator.ReviewAsync(new ReviewRequest(CsDiff));

        Assert.Equal(3, review.Runs.Count);
        Assert.Equal(
            ReviewOrchestrator.AgentNames.Order(),
            review.Runs.Select(r => r.Agent).Order());
        Assert.All(review.Runs, r => Assert.NotNull(r.Findings));
        Assert.Equal(3, llm.Calls);
    }

    [Fact]
    public async Task OneAgentFails_OthersSurvive()
    {
        // The shared static-analysis fake throws, sinking quality and security's
        // tool passes; docs has no tool pass and survives.
        var analysis = new FakeStaticAnalysisClient
        {
            ThrowOnCall = new InvalidOperationException("MCP server down"),
        };
        using var provider = BuildProvider(new FakeLlmProvider(), analysis);
        var orchestrator = provider.GetRequiredService<ReviewOrchestrator>();

        var review = await orchestrator.ReviewAsync(new ReviewRequest(CsDiff));

        Assert.Equal(3, review.Runs.Count);
        var docs = Assert.Single(review.Runs, r => r.Agent == "docs");
        Assert.NotNull(docs.Findings);
        Assert.All(review.Runs.Where(r => r.Agent != "docs"), r =>
        {
            Assert.Null(r.Findings);
            Assert.Contains("MCP server down", r.Error);
        });
        Assert.True(review.AnySucceeded);
    }

    [Fact]
    public async Task FanOut_IsConcurrent()
    {
        // Each LLM call waits for all three to arrive before any completes.
        // Sequential execution would deadlock and hit the gate's timeout.
        var llm = new GatingLlmProvider(expected: 3);
        using var provider = BuildProvider(llm, new FakeStaticAnalysisClient());
        var orchestrator = provider.GetRequiredService<ReviewOrchestrator>();

        var review = await orchestrator.ReviewAsync(new ReviewRequest(CsDiff));

        Assert.All(review.Runs, r => Assert.NotNull(r.Findings));
    }

    [Fact]
    public void Roster_MatchesKeyedRegistrations()
    {
        using var provider = BuildProvider(new FakeLlmProvider(), new FakeStaticAnalysisClient());

        foreach (var name in ReviewOrchestrator.AgentNames)
        {
            Assert.Equal(name, provider.GetRequiredKeyedService<IReviewAgent>(name).Name);
        }
    }

    /// <summary>
    /// Releases no caller until <paramref name="expected"/> concurrent calls have
    /// arrived; times out after 5 seconds so a sequential regression fails fast.
    /// </summary>
    private sealed class GatingLlmProvider(int expected) : ILlmProvider
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _entered) >= expected)
            {
                _gate.TrySetResult();
            }

            await _gate.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return new LlmResponse("""{"findings":[]}""", 10, 5, "end_turn");
        }
    }
}
