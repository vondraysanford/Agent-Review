using System.Text.Json;
using AgentReview.Agents;
using AgentReview.Agents.Configuration;
using AgentReview.Agents.GitHub;
using AgentReview.Agents.Llm;
using AgentReview.Agents.StaticAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentReview.Tests;

/// <summary>
/// Pins the Phase 3 contract the Phase 4 orchestrator depends on: three
/// independent agents, resolvable by key, returning the identical schema.
/// </summary>
public class AgentRosterTests
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

    private const string LlmFindingJson = """
        {"findings":[{"issue":"Something notable","file":"src/A.cs","line":3,"severity":"Warning","suggestion":null}]}
        """;

    private static IReadOnlyList<IReviewAgent> CreateAllAgents()
    {
        IReviewAgent quality = new QualityAgent(
            new FakeLlmProvider { ResponseText = LlmFindingJson },
            new FakeStaticAnalysisClient(),
            new FakeFileContentProvider(),
            Options.Create(new QualityAgentOptions()),
            NullLogger<QualityAgent>.Instance);
        IReviewAgent security = new SecurityAgent(
            new FakeLlmProvider { ResponseText = LlmFindingJson },
            new FakeStaticAnalysisClient(),
            new FakeFileContentProvider(),
            Options.Create(new SecurityAgentOptions()),
            NullLogger<SecurityAgent>.Instance);
        IReviewAgent docs = new DocsAgent(
            new FakeLlmProvider { ResponseText = LlmFindingJson },
            new FakeFileContentProvider(),
            Options.Create(new DocsAgentOptions()),
            NullLogger<DocsAgent>.Instance);
        return [quality, security, docs];
    }

    [Fact]
    public async Task AllThreeAgents_ReviewSameDiff_IdenticalJsonShape()
    {
        var webOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string[]? expectedKeys = null;

        foreach (var agent in CreateAllAgents())
        {
            var findings = await agent.ReviewAsync(new ReviewRequest(CsDiff));
            var finding = Assert.Single(findings);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(finding, webOptions));
            var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();

            expectedKeys ??= keys;
            Assert.Equal(expectedKeys, keys);
        }

        Assert.Equal(["file", "issue", "line", "severity", "source", "suggestion"], expectedKeys);
    }

    [Fact]
    public void AllThreeAgents_ResolveFromKeyedDi()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmProvider>(new FakeLlmProvider());
        services.AddSingleton<IStaticAnalysisClient>(new FakeStaticAnalysisClient());
        services.AddSingleton<IFileContentProvider>(new FakeFileContentProvider());
        services.AddSingleton(Options.Create(new QualityAgentOptions()));
        services.AddSingleton(Options.Create(new SecurityAgentOptions()));
        services.AddSingleton(Options.Create(new DocsAgentOptions()));
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddKeyedSingleton<IReviewAgent, QualityAgent>("quality");
        services.AddKeyedSingleton<IReviewAgent, SecurityAgent>("security");
        services.AddKeyedSingleton<IReviewAgent, DocsAgent>("docs");

        using var provider = services.BuildServiceProvider();

        foreach (var key in new[] { "quality", "security", "docs" })
        {
            var agent = provider.GetRequiredKeyedService<IReviewAgent>(key);
            Assert.Equal(key, agent.Name);
        }
    }

    [Fact]
    public void AgentNames_AreDistinct()
    {
        var names = CreateAllAgents().Select(a => a.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }
}
