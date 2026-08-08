using System.Text.RegularExpressions;
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
/// The Phase 4 end-to-end proof, deterministic: a planted diff through the full
/// DI pipeline (three agents, fan-out, synthesis) with scripted fakes, asserting
/// the one coherent, ordered review top to bottom. The scripted LLM routes on the
/// real system prompts, so a prompt rewrite that breaks routing fails loudly here.
/// </summary>
public class EndToEndPipelineTests
{
    // New-file diff for src/Demo/AccountService.cs; added lines 1..19.
    // Planted: hardcoded credentials (8), stale comment (10), undocumented API (11),
    // unused variable (13), SQL injection (15), unreachable code (17).
    private const string PlantedDiff = """
        diff --git a/src/Demo/AccountService.cs b/src/Demo/AccountService.cs
        new file mode 100644
        index 0000000..aaaaaaa
        --- /dev/null
        +++ b/src/Demo/AccountService.cs
        @@ -0,0 +1,19 @@
        +using System;
        +using Microsoft.Data.SqlClient;
        +
        +namespace Demo;
        +
        +public class AccountService
        +{
        +    private const string ConnectionString = "Server=prod-db;Database=accounts;User Id=svc;Password=hunter2;";
        +
        +    // Returns null when the account does not exist.
        +    public SqlCommand FindAccount(string accountName)
        +    {
        +        int unused = 42;
        +        var conn = new SqlConnection(ConnectionString);
        +        var cmd = new SqlCommand("SELECT * FROM Accounts WHERE Name = '" + accountName + "'", conn);
        +        return cmd;
        +        Console.WriteLine("never runs");
        +    }
        +}
        """;

    private const string File = "src/Demo/AccountService.cs";

    private static async Task<SynthesizedReview> RunPipelineAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILlmProvider>(new ScriptedLlmProvider());
        services.AddSingleton<IStaticAnalysisClient>(new FakeStaticAnalysisClient
        {
            Findings =
            [
                new("CS0219", "The variable 'unused' is assigned but its value is never used", 13, 13, "Warning", "roslyn"),
                new("CS0162", "Unreachable code detected", 17, 9, "Warning", "roslyn"),
            ],
            SemgrepFindings =
            [
                new("csharp-sqli", "Detected a formatted string in a SQL statement", 15, 19, "Error", "semgrep"),
            ],
        });
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
            .ReviewAsync(new ReviewRequest(PlantedDiff));
        return await provider.GetRequiredService<ReviewSynthesizer>()
            .SynthesizeAsync(fanOut);
    }

    [Fact]
    public async Task PlantedDiff_ProducesOneCoherentOrderedReview()
    {
        var review = await RunPipelineAsync();

        Assert.All(review.Runs, r => Assert.NotNull(r.Findings));
        Assert.Equal(1, review.DuplicatesMerged); // quality-llm's SQL finding merged into semgrep's

        // 8 attributed findings minus 1 merged duplicate.
        Assert.Equal(7, review.Findings.Count);

        // Exact ranked order: Errors first (by line), then Warnings by line and source.
        var ordered = review.Findings.Select(f => (f.Source, f.Line, f.Severity)).ToList();
        Assert.Equal(
            [
                ("security-llm", 8, FindingSeverity.Error),
                ("semgrep", 15, FindingSeverity.Error),
                ("docs-llm", 10, FindingSeverity.Warning),
                ("docs-llm", 11, FindingSeverity.Warning),
                ("roslyn", 13, FindingSeverity.Warning),
                ("quality-llm", 14, FindingSeverity.Warning),
                ("roslyn", 17, FindingSeverity.Warning),
            ],
            ordered);

        // Every provenance lane is represented in one review.
        var sources = review.Findings.Select(f => f.Source).ToHashSet();
        Assert.Superset(new HashSet<string> { "roslyn", "semgrep", "quality-llm", "security-llm", "docs-llm" }, sources);
    }

    [Fact]
    public async Task OrderedReview_SeverityNeverIncreases()
    {
        var review = await RunPipelineAsync();

        for (var i = 1; i < review.Findings.Count; i++)
        {
            Assert.True(review.Findings[i].Severity <= review.Findings[i - 1].Severity);
        }
    }

    /// <summary>
    /// Routes on the real system prompts: each agent gets its scripted findings,
    /// the arbiter clusters every id it is shown (the fixture's only multi-agent
    /// group is the genuine duplicate pair).
    /// </summary>
    private sealed class ScriptedLlmProvider : ILlmProvider
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            string text;
            if (request.SystemPrompt.Contains("synthesis arbiter"))
            {
                var ids = Regex.Matches(request.UserContent, @"\[(\d+)\]")
                    .Select(m => m.Groups[1].Value);
                text = $$"""{"clusters":[{"ids":[{{string.Join(',', ids)}}]}]}""";
            }
            else if (request.SystemPrompt.Contains("quality reviewer"))
            {
                text = $$"""
                    {"findings":[
                      {"issue":"Connection created but ownership of disposal is unclear","file":"{{File}}","line":14,"severity":"Warning","suggestion":null},
                      {"issue":"SQL built by string concatenation","file":"{{File}}","line":15,"severity":"Warning","suggestion":null}
                    ]}
                    """;
            }
            else if (request.SystemPrompt.Contains("security reviewer"))
            {
                text = $$"""
                    {"findings":[
                      {"issue":"Hardcoded database credentials in connection string","file":"{{File}}","line":8,"severity":"Error","suggestion":"Load from configuration"}
                    ]}
                    """;
            }
            else if (request.SystemPrompt.Contains("documentation reviewer"))
            {
                text = $$"""
                    {"findings":[
                      {"issue":"Comment claims a null return but the method never returns null","file":"{{File}}","line":10,"severity":"Warning","suggestion":"Correct the comment"},
                      {"issue":"New public method has no XML documentation","file":"{{File}}","line":11,"severity":"Warning","suggestion":null}
                    ]}
                    """;
            }
            else
            {
                throw new InvalidOperationException("Unrecognized system prompt; routing keys need updating.");
            }

            return Task.FromResult(new LlmResponse(text, 100, 50, "end_turn"));
        }
    }
}
