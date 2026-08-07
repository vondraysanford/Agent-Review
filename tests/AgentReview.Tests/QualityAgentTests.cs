using AgentReview.Agents;
using AgentReview.Agents.Configuration;
using AgentReview.Agents.GitHub;
using AgentReview.Agents.Llm;
using AgentReview.Agents.StaticAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentReview.Tests;

/// <summary>
/// Exercises the quality agent's merge pipeline with fakes for both seams.
/// The live Anthropic and MCP paths are covered by the Orchestrator verification
/// run, not unit tests.
/// </summary>
public class QualityAgentTests
{
    // New-side lines 10..16; line 13 is the only added line.
    private const string CsDiff = """
        diff --git a/src/A.cs b/src/A.cs
        index 1111111..2222222 100644
        --- a/src/A.cs
        +++ b/src/A.cs
        @@ -10,7 +10,8 @@ public class A
             public void M()
             {
                 int x = 1;
        +        int unused = 42;
                 Console.WriteLine(x);
             }
         }
        """;

    private const string MarkdownOnlyDiff = """
        diff --git a/README.md b/README.md
        index 1111111..2222222 100644
        --- a/README.md
        +++ b/README.md
        @@ -1,2 +1,3 @@
         # Title
        +A new line of prose.
         Some text.
        """;

    private static QualityAgent CreateAgent(
        FakeLlmProvider llm,
        FakeStaticAnalysisClient analysis,
        QualityAgentOptions? options = null,
        FakeFileContentProvider? files = null) =>
        new(
            llm,
            analysis,
            files ?? new FakeFileContentProvider(),
            Options.Create(options ?? new QualityAgentOptions()),
            NullLogger<QualityAgent>.Instance);

    private static readonly RepoReference TestRepo = new("vondraysanford", "Agent-Review", "main");

    // Full new-revision content of src/A.cs matching CsDiff: line 13 is the added line.
    private const string FullFileContent = """
        namespace Demo;

        public class A
        {
            private int _seed = 7;

            public void Setup()
            {
            }

            public void M()
            {
                int x = 1;
                int unused = 42;
                Console.WriteLine(x);
            }
        }
        """;

    [Fact]
    public async Task AnalyzerFindings_MapSnippetLinesToNewFileLines()
    {
        var analysis = new FakeStaticAnalysisClient
        {
            Findings = [new("CS0219", "The variable 'unused' is assigned but never used", 4, 13, "Warning", "roslyn")],
        };
        var agent = CreateAgent(new FakeLlmProvider(), analysis);

        var findings = await agent.ReviewAsync(new ReviewRequest(CsDiff));

        var finding = Assert.Single(findings);
        Assert.Equal("src/A.cs", finding.File);
        Assert.Equal(13, finding.Line);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal("roslyn", finding.Source);
        Assert.StartsWith("CS0219:", finding.Issue);
    }

    [Fact]
    public async Task AnalyzerFindingsOnContextLines_AreDropped()
    {
        var analysis = new FakeStaticAnalysisClient
        {
            Findings = [new("CA1822", "Member can be marked static", 3, 1, "Warning", "roslyn")],
        };
        var agent = CreateAgent(new FakeLlmProvider(), analysis);

        Assert.Empty(await agent.ReviewAsync(new ReviewRequest(CsDiff)));
    }

    [Fact]
    public async Task CompilerErrors_AreFiltered_WarningsKept()
    {
        var analysis = new FakeStaticAnalysisClient
        {
            Findings =
            [
                new("CS0246", "The type or namespace name 'Foo' could not be found", 4, 1, "Error", "roslyn"),
                new("CS0219", "The variable 'unused' is assigned but never used", 4, 13, "Warning", "roslyn"),
                new("CA1822", "Member can be marked static", 4, 1, "Warning", "roslyn"),
            ],
        };
        var agent = CreateAgent(new FakeLlmProvider(), analysis);

        var findings = await agent.ReviewAsync(new ReviewRequest(CsDiff));

        Assert.Equal(2, findings.Count);
        Assert.DoesNotContain(findings, f => f.Issue.StartsWith("CS0246"));
    }

    [Fact]
    public async Task NonCsFiles_NotSentToAnalyzer_LlmStillCalled()
    {
        var analysis = new FakeStaticAnalysisClient();
        var llm = new FakeLlmProvider();
        var agent = CreateAgent(llm, analysis);

        await agent.ReviewAsync(new ReviewRequest(MarkdownOnlyDiff));

        Assert.Empty(analysis.ReceivedSnippets);
        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task LlmFindings_StampedSourceLlm_SeverityParsed()
    {
        var llm = new FakeLlmProvider
        {
            ResponseText = """
                {"findings":[{"issue":"Parameter name 'amt' is unclear","file":"src/A.cs","line":13,"severity":"Warning","suggestion":"Rename to amount"}]}
                """,
        };
        var agent = CreateAgent(llm, new FakeStaticAnalysisClient());

        var findings = await agent.ReviewAsync(new ReviewRequest(CsDiff));

        var finding = Assert.Single(findings);
        Assert.Equal("llm", finding.Source);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal("Rename to amount", finding.Suggestion);
    }

    [Fact]
    public async Task LlmFindingOnFileNotInDiff_IsDropped()
    {
        var llm = new FakeLlmProvider
        {
            ResponseText = """
                {"findings":[{"issue":"Made up","file":"src/Other.cs","line":13,"severity":"Warning","suggestion":null}]}
                """,
        };
        var agent = CreateAgent(llm, new FakeStaticAnalysisClient());

        Assert.Empty(await agent.ReviewAsync(new ReviewRequest(CsDiff)));
    }

    [Fact]
    public async Task LlmFindingCollidingWithAnalyzerLine_IsDropped()
    {
        var analysis = new FakeStaticAnalysisClient
        {
            Findings = [new("CS0219", "The variable 'unused' is assigned but never used", 4, 13, "Warning", "roslyn")],
        };
        var llm = new FakeLlmProvider
        {
            ResponseText = """
                {"findings":[{"issue":"Unused local variable","file":"src/A.cs","line":13,"severity":"Warning","suggestion":null}]}
                """,
        };
        var agent = CreateAgent(llm, analysis);

        var findings = await agent.ReviewAsync(new ReviewRequest(CsDiff));

        var finding = Assert.Single(findings);
        Assert.Equal("roslyn", finding.Source);
    }

    [Fact]
    public async Task MergedFindings_OrderedBySeverityThenFileThenLine()
    {
        var analysis = new FakeStaticAnalysisClient
        {
            Findings = [new("CS0219", "Unused variable", 4, 13, "Warning", "roslyn")],
        };
        var llm = new FakeLlmProvider
        {
            ResponseText = """
                {"findings":[{"issue":"Likely wrong behavior","file":"src/A.cs","line":12,"severity":"Error","suggestion":null}]}
                """,
        };
        var agent = CreateAgent(llm, analysis);

        var findings = await agent.ReviewAsync(new ReviewRequest(CsDiff));

        Assert.Equal(2, findings.Count);
        Assert.Equal(FindingSeverity.Error, findings[0].Severity);
        Assert.Equal(12, findings[0].Line);
        Assert.Equal(FindingSeverity.Warning, findings[1].Severity);
        Assert.Equal(13, findings[1].Line);
    }

    [Fact]
    public async Task DiffOverMaxChars_ThrowsBeforeAnyToolCall()
    {
        var analysis = new FakeStaticAnalysisClient();
        var llm = new FakeLlmProvider();
        var agent = CreateAgent(llm, analysis, new QualityAgentOptions { MaxDiffChars = 10 });

        await Assert.ThrowsAsync<ArgumentException>(() => agent.ReviewAsync(new ReviewRequest(CsDiff)));
        Assert.Empty(analysis.ReceivedSnippets);
        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task LlmStopReasonMaxTokens_Throws()
    {
        var llm = new FakeLlmProvider { StopReason = "max_tokens" };
        var agent = CreateAgent(llm, new FakeStaticAnalysisClient());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ReviewAsync(new ReviewRequest(CsDiff)));
        Assert.Contains("MaxOutputTokens", ex.Message);
    }

    [Fact]
    public async Task AnalyzerFailure_PropagatesAndFailsReview()
    {
        var analysis = new FakeStaticAnalysisClient
        {
            ThrowOnCall = new InvalidOperationException("MCP server unavailable"),
        };
        var agent = CreateAgent(new FakeLlmProvider(), analysis);

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ReviewAsync(new ReviewRequest(CsDiff)));
    }

    [Fact]
    public async Task RepoContext_FullFileAnalyzed_LinesKeptOnlyWhenAdded()
    {
        var files = new FakeFileContentProvider { Content = FullFileContent };
        var analysis = new FakeStaticAnalysisClient
        {
            // Full-file mode: analyzer lines are real file lines. 13 is added, 12 is not.
            Findings =
            [
                new("CS0219", "The variable 'unused' is assigned but never used", 13, 13, "Warning", "roslyn"),
                new("CA1822", "Member can be marked static", 12, 1, "Warning", "roslyn"),
            ],
        };
        var agent = CreateAgent(new FakeLlmProvider(), analysis, files: files);

        var findings = await agent.ReviewAsync(new ReviewRequest(CsDiff, TestRepo));

        Assert.Equal(FullFileContent, Assert.Single(analysis.ReceivedSnippets));
        var finding = Assert.Single(findings);
        Assert.Equal(13, finding.Line);
        Assert.StartsWith("CS0219:", finding.Issue);
    }

    [Fact]
    public async Task RepoContext_FetchReturnsNull_FallsBackToFragment()
    {
        var files = new FakeFileContentProvider { Content = null };
        var analysis = new FakeStaticAnalysisClient();
        var agent = CreateAgent(new FakeLlmProvider(), analysis, files: files);

        await agent.ReviewAsync(new ReviewRequest(CsDiff, TestRepo));

        Assert.Single(files.RequestedPaths);
        var snippet = Assert.Single(analysis.ReceivedSnippets);
        Assert.Contains("int unused = 42;", snippet);
        Assert.DoesNotContain("private int _seed", snippet);
    }

    [Fact]
    public async Task RepoContext_ContextAppendedToLlmContent()
    {
        var files = new FakeFileContentProvider { Content = FullFileContent };
        var llm = new FakeLlmProvider();
        var agent = CreateAgent(llm, new FakeStaticAnalysisClient(), files: files);

        await agent.ReviewAsync(new ReviewRequest(CsDiff, TestRepo));

        Assert.NotNull(llm.LastRequest);
        Assert.Contains("diff --git a/src/A.cs", llm.LastRequest!.UserContent);
        Assert.Contains("<context file=\"src/A.cs\">", llm.LastRequest.UserContent);
        Assert.Contains("private int _seed = 7;", llm.LastRequest.UserContent);
    }

    [Fact]
    public async Task RepoContext_OverMaxContextChars_FileSkipped()
    {
        var files = new FakeFileContentProvider { Content = FullFileContent };
        var llm = new FakeLlmProvider();
        var agent = CreateAgent(
            llm,
            new FakeStaticAnalysisClient(),
            new QualityAgentOptions { MaxContextChars = 10 },
            files);

        var findings = await agent.ReviewAsync(new ReviewRequest(CsDiff, TestRepo));

        Assert.NotNull(llm.LastRequest);
        Assert.DoesNotContain("<context", llm.LastRequest!.UserContent);
        Assert.Empty(findings);
    }

    [Fact]
    public async Task NoRepo_ProviderNeverCalled()
    {
        var files = new FakeFileContentProvider { Content = FullFileContent };
        var agent = CreateAgent(new FakeLlmProvider(), new FakeStaticAnalysisClient(), files: files);

        await agent.ReviewAsync(new ReviewRequest(CsDiff));

        Assert.Empty(files.RequestedPaths);
    }

    private sealed class FakeFileContentProvider : IFileContentProvider
    {
        public string? Content { get; init; }
        public List<string> RequestedPaths { get; } = [];

        public Task<string?> GetFileContentAsync(RepoReference repo, string path, CancellationToken cancellationToken = default)
        {
            RequestedPaths.Add(path);
            return Task.FromResult(Content);
        }
    }

    private sealed class FakeStaticAnalysisClient : IStaticAnalysisClient
    {
        public List<string> ReceivedSnippets { get; } = [];
        public List<StaticAnalysisFinding> Findings { get; init; } = [];
        public Exception? ThrowOnCall { get; init; }

        public Task<IReadOnlyList<StaticAnalysisFinding>> AnalyzeCSharpAsync(string code, CancellationToken cancellationToken = default)
        {
            if (ThrowOnCall is not null)
            {
                throw ThrowOnCall;
            }

            ReceivedSnippets.Add(code);
            return Task.FromResult<IReadOnlyList<StaticAnalysisFinding>>(Findings);
        }
    }

    private sealed class FakeLlmProvider : ILlmProvider
    {
        public string ResponseText { get; init; } = """{"findings":[]}""";
        public string? StopReason { get; init; } = "end_turn";
        public int Calls { get; private set; }
        public LlmRequest? LastRequest { get; private set; }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(new LlmResponse(ResponseText, 100, 50, StopReason));
        }
    }
}
