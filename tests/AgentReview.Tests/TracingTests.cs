using System.Diagnostics;
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
/// Pins the tracing contract with an in-box ActivityListener: the span tree
/// exists with the tags the run-summary item will consume, and the pipeline is
/// unaffected when nobody listens. The llm.complete span lives in the Anthropic
/// adapter and is covered by the live traced run, not fakes.
/// </summary>
public class TracingTests
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

    private static QualityAgent CreateQualityAgent() =>
        new(
            new FakeLlmProvider(),
            new FakeStaticAnalysisClient(),
            new FakeFileContentProvider(),
            Options.Create(new QualityAgentOptions()),
            NullLogger<QualityAgent>.Instance);

    private static (ActivityListener Listener, List<Activity> Activities) Listen()
    {
        var activities = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "AgentReview",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (activities)
                {
                    activities.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, activities);
    }

    [Fact]
    public async Task AgentRun_EmitsAgentSpan_WithTags()
    {
        var (listener, activities) = Listen();
        ActivityTraceId traceId;
        using (listener)
        {
            // A root activity isolates this test's trace from parallel test classes
            // whose agents also emit on the shared source.
            using var root = new Activity("test-root").Start();
            traceId = root.TraceId;
            await CreateQualityAgent().ReviewAsync(new ReviewRequest(CsDiff));
        }

        var agentSpan = Assert.Single(activities, a => a.OperationName == "agent.review" && a.TraceId == traceId);
        Assert.Equal("quality", agentSpan.GetTagItem("agent.name"));
        Assert.Equal(CsDiff.Length, agentSpan.GetTagItem("diff.chars"));
        Assert.NotNull(agentSpan.GetTagItem("findings.count"));
    }

    [Fact]
    public async Task Pipeline_EmitsFanoutAgentAndSynthesisSpans()
    {
        var (listener, activities) = Listen();
        ActivityTraceId traceId;
        using (listener)
        {
            using var root = new Activity("test-root").Start();
            traceId = root.TraceId;
            var services = new ServiceCollection();
            services.AddSingleton<ILlmProvider>(new FakeLlmProvider());
            services.AddSingleton<IStaticAnalysisClient>(new FakeStaticAnalysisClient());
            services.AddSingleton<IFileContentProvider>(new FakeFileContentProvider());
            services.AddSingleton(Options.Create(new QualityAgentOptions()));
            services.AddSingleton(Options.Create(new SecurityAgentOptions()));
            services.AddSingleton(Options.Create(new DocsAgentOptions()));
            services.AddSingleton(Options.Create(new SynthesisOptions()));
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddKeyedSingleton<IReviewAgent, QualityAgent>("quality");
            services.AddKeyedSingleton<IReviewAgent, SecurityAgent>("security");
            services.AddKeyedSingleton<IReviewAgent, DocsAgent>("docs");
            services.AddSingleton(Options.Create(new BudgetOptions()));
            services.AddSingleton(Options.Create(new PricingOptions()));
            services.AddSingleton<BudgetGuard>();
            services.AddSingleton<ReviewOrchestrator>();
            services.AddSingleton<ReviewSynthesizer>();
            using var provider = services.BuildServiceProvider();

            var fanOut = await provider.GetRequiredService<ReviewOrchestrator>()
                .ReviewAsync(new ReviewRequest(CsDiff));
            await provider.GetRequiredService<ReviewSynthesizer>().SynthesizeAsync(fanOut);
        }

        var mine = activities.Where(a => a.TraceId == traceId).ToList();
        Assert.Single(mine, a => a.OperationName == "review.fanout");
        var agentNames = mine
            .Where(a => a.OperationName == "agent.review")
            .Select(a => (string?)a.GetTagItem("agent.name"))
            .Order()
            .ToArray();
        Assert.Equal(["docs", "quality", "security"], agentNames);
        var synthesis = Assert.Single(mine, a => a.OperationName == "synthesis");
        Assert.Equal(0, synthesis.GetTagItem("duplicates.merged"));
    }

    [Fact]
    public async Task NoListener_PipelineUnaffected()
    {
        var findings = await CreateQualityAgent().ReviewAsync(new ReviewRequest(CsDiff));

        Assert.NotNull(findings);
        Assert.Null(Activity.Current);
    }
}
