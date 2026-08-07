using AgentReview.Agents;
using AgentReview.Agents.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentReview.Tests;

/// <summary>
/// Pins what is docs-specific: no static tool pass ever, and GitHub context
/// widened to Markdown. Shared pipeline mechanics are covered by QualityAgentTests.
/// </summary>
public class DocsAgentTests
{
    // A .cs file (added lines 5-6) and a .md file (added line 3) in one diff.
    private const string MixedDiff = """
        diff --git a/src/B.cs b/src/B.cs
        index 1111111..2222222 100644
        --- a/src/B.cs
        +++ b/src/B.cs
        @@ -3,4 +3,6 @@ public class B
             public void Existing()
             {
             }
        +
        +    public int Compute(int seed) => seed * 31;
         }
        diff --git a/docs/usage.md b/docs/usage.md
        index 3333333..4444444 100644
        --- a/docs/usage.md
        +++ b/docs/usage.md
        @@ -1,2 +1,3 @@
         # Usage
        +Call Compute to get a stable hash.
         More text.
        """;

    private static readonly RepoReference TestRepo = new("vondraysanford", "Agent-Review", "main");

    private static DocsAgent CreateAgent(
        FakeLlmProvider llm,
        FakeFileContentProvider? files = null) =>
        new(
            llm,
            files ?? new FakeFileContentProvider(),
            Options.Create(new DocsAgentOptions()),
            NullLogger<DocsAgent>.Instance);

    [Fact]
    public async Task NoToolCalls_EverMade()
    {
        var llm = new FakeLlmProvider();
        var agent = CreateAgent(llm);

        await agent.ReviewAsync(new ReviewRequest(MixedDiff));

        Assert.Equal(1, llm.Calls);
        // The docs agent is constructed without any IStaticAnalysisClient at all;
        // its constructor signature is the proof no tool can be called.
    }

    [Fact]
    public async Task LlmDocFinding_OnCsLine_Kept()
    {
        var llm = new FakeLlmProvider
        {
            ResponseText = """
                {"findings":[{"issue":"Public method Compute has no XML doc comment","file":"src/B.cs","line":7,"severity":"Warning","suggestion":"/// <summary>Computes a stable hash from the seed.</summary>"}]}
                """,
        };
        var agent = CreateAgent(llm);

        var findings = await agent.ReviewAsync(new ReviewRequest(MixedDiff));

        var finding = Assert.Single(findings);
        Assert.Equal("llm", finding.Source);
        Assert.Equal(7, finding.Line);
        Assert.Contains("<summary>", finding.Suggestion);
    }

    [Fact]
    public async Task LlmDocFinding_OnMarkdownLine_Kept()
    {
        var llm = new FakeLlmProvider
        {
            ResponseText = """
                {"findings":[{"issue":"Doc claims Compute is a stable hash but the code multiplies by a prime without masking overflow","file":"docs/usage.md","line":2,"severity":"Info","suggestion":null}]}
                """,
        };
        var agent = CreateAgent(llm);

        var findings = await agent.ReviewAsync(new ReviewRequest(MixedDiff));

        var finding = Assert.Single(findings);
        Assert.Equal("docs/usage.md", finding.File);
    }

    [Fact]
    public async Task RepoContext_FetchesMarkdownAndCs()
    {
        var files = new FakeFileContentProvider { Content = "full file content" };
        var agent = CreateAgent(new FakeLlmProvider(), files);

        await agent.ReviewAsync(new ReviewRequest(MixedDiff, TestRepo));

        Assert.Contains("src/B.cs", files.RequestedPaths);
        Assert.Contains("docs/usage.md", files.RequestedPaths);
    }

    [Fact]
    public async Task QualityAgent_ContextFilter_StaysCsOnly()
    {
        // Guard: the base-class generalization must not widen quality's context fetch.
        var files = new FakeFileContentProvider { Content = "full file content" };
        var quality = new QualityAgent(
            new FakeLlmProvider(),
            new FakeStaticAnalysisClient(),
            files,
            Options.Create(new QualityAgentOptions()),
            NullLogger<QualityAgent>.Instance);

        await quality.ReviewAsync(new ReviewRequest(MixedDiff, TestRepo));

        Assert.Contains("src/B.cs", files.RequestedPaths);
        Assert.DoesNotContain("docs/usage.md", files.RequestedPaths);
    }
}
