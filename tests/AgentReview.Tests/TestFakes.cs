using AgentReview.Agents;
using AgentReview.Agents.GitHub;
using AgentReview.Agents.Llm;
using AgentReview.Agents.StaticAnalysis;

namespace AgentReview.Tests;

/// <summary>
/// Hand-rolled fakes for the three agent seams, shared by every agent test class.
/// </summary>
internal sealed class FakeStaticAnalysisClient : IStaticAnalysisClient
{
    public List<string> ReceivedSnippets { get; } = [];
    public List<StaticAnalysisFinding> Findings { get; init; } = [];
    public List<string> ReceivedRulesets { get; } = [];
    public List<StaticAnalysisFinding> SemgrepFindings { get; init; } = [];
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

    public Task<IReadOnlyList<StaticAnalysisFinding>> RunSemgrepAsync(string code, string ruleset, CancellationToken cancellationToken = default)
    {
        if (ThrowOnCall is not null)
        {
            throw ThrowOnCall;
        }

        ReceivedSnippets.Add(code);
        ReceivedRulesets.Add(ruleset);
        return Task.FromResult<IReadOnlyList<StaticAnalysisFinding>>(SemgrepFindings);
    }
}

internal sealed class FakeLlmProvider : ILlmProvider
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

internal sealed class FakeFileContentProvider : IFileContentProvider
{
    public string? Content { get; init; }
    public List<string> RequestedPaths { get; } = [];

    public Task<string?> GetFileContentAsync(RepoReference repo, string path, CancellationToken cancellationToken = default)
    {
        RequestedPaths.Add(path);
        return Task.FromResult(Content);
    }
}
