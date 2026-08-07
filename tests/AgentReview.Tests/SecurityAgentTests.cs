using AgentReview.Agents;
using AgentReview.Agents.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentReview.Tests;

/// <summary>
/// Exercises the security agent's Semgrep-backed pipeline with fakes. The shared
/// pipeline mechanics (grounding, guardrails, ordering) are covered by
/// QualityAgentTests; these tests pin what is security-specific.
/// </summary>
public class SecurityAgentTests
{
    // New-side lines 10..16; line 13 is the only added line.
    private const string CsDiff = """
        diff --git a/src/Lookup.cs b/src/Lookup.cs
        index 1111111..2222222 100644
        --- a/src/Lookup.cs
        +++ b/src/Lookup.cs
        @@ -10,7 +10,8 @@ public class Lookup
             public SqlCommand Build(string name)
             {
                 var conn = Open();
        +        var cmd = new SqlCommand("SELECT * FROM T WHERE N = '" + name + "'", conn);
                 return cmd;
             }
         }
        """;

    private static SecurityAgent CreateAgent(
        FakeLlmProvider llm,
        FakeStaticAnalysisClient analysis,
        SecurityAgentOptions? options = null,
        FakeFileContentProvider? files = null) =>
        new(
            llm,
            analysis,
            files ?? new FakeFileContentProvider(),
            Options.Create(options ?? new SecurityAgentOptions()),
            NullLogger<SecurityAgent>.Instance);

    [Fact]
    public async Task SemgrepFindings_MapToAddedLines_SourceSemgrep()
    {
        var analysis = new FakeStaticAnalysisClient
        {
            SemgrepFindings =
            [
                new("csharp.lang.security.sqli.csharp-sqli.csharp-sqli", "Detected a formatted string in a SQL statement", 4, 19, "Error", "semgrep"),
            ],
        };
        var agent = CreateAgent(new FakeLlmProvider(), analysis);

        var findings = await agent.ReviewAsync(new ReviewRequest(CsDiff));

        var finding = Assert.Single(findings);
        Assert.Equal("src/Lookup.cs", finding.File);
        Assert.Equal(13, finding.Line);
        Assert.Equal("semgrep", finding.Source);
        Assert.Contains("csharp-sqli", finding.Issue);
    }

    [Fact]
    public async Task SemgrepErrorSeverity_IsKept()
    {
        // Contrast with the quality agent: Semgrep Errors are real findings,
        // not fragment compiler noise, and must survive.
        var analysis = new FakeStaticAnalysisClient
        {
            SemgrepFindings = [new("csharp-sqli", "SQL injection", 4, 1, "Error", "semgrep")],
        };
        var agent = CreateAgent(new FakeLlmProvider(), analysis);

        var findings = await agent.ReviewAsync(new ReviewRequest(CsDiff));

        var finding = Assert.Single(findings);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
    }

    [Fact]
    public async Task ConfiguredRuleset_PassedToClient()
    {
        var analysis = new FakeStaticAnalysisClient();
        var agent = CreateAgent(
            new FakeLlmProvider(),
            analysis,
            new SecurityAgentOptions { Ruleset = "p/default" });

        await agent.ReviewAsync(new ReviewRequest(CsDiff));

        Assert.Equal("p/default", Assert.Single(analysis.ReceivedRulesets));
    }

    [Fact]
    public async Task LlmSecurityFinding_GroundedAndStamped()
    {
        var llm = new FakeLlmProvider
        {
            ResponseText = """
                {"findings":[{"issue":"Hardcoded password in connection string","file":"src/Lookup.cs","line":13,"severity":"Error","suggestion":"Load credentials from configuration"}]}
                """,
        };
        var agent = CreateAgent(llm, new FakeStaticAnalysisClient());

        var findings = await agent.ReviewAsync(new ReviewRequest(CsDiff));

        var finding = Assert.Single(findings);
        Assert.Equal("llm", finding.Source);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
    }

    [Fact]
    public async Task CollisionWithSemgrepLine_Deduped()
    {
        var analysis = new FakeStaticAnalysisClient
        {
            SemgrepFindings = [new("csharp-sqli", "SQL injection", 4, 1, "Error", "semgrep")],
        };
        var llm = new FakeLlmProvider
        {
            ResponseText = """
                {"findings":[{"issue":"SQL built by concatenation","file":"src/Lookup.cs","line":13,"severity":"Error","suggestion":null}]}
                """,
        };
        var agent = CreateAgent(llm, analysis);

        var findings = await agent.ReviewAsync(new ReviewRequest(CsDiff));

        var finding = Assert.Single(findings);
        Assert.Equal("semgrep", finding.Source);
    }
}
