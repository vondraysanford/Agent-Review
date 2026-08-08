using System.Diagnostics;
using AgentReview.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentReview.Orchestrator;

/// <summary>One agent's outcome inside a fan-out: findings on success, an error message on failure.</summary>
public sealed record AgentRunResult(string Agent, IReadOnlyList<Finding>? Findings, string? Error, TimeSpan Elapsed);

public sealed record OrchestratedReview(IReadOnlyList<AgentRunResult> Runs, TimeSpan TotalElapsed)
{
    public bool AnySucceeded => Runs.Any(r => r.Findings is not null);
}

/// <summary>
/// Fans one review request out to every registered agent concurrently and collects
/// the results. Plain C# orchestration by design decision: Task.WhenAll and keyed DI,
/// no agent framework. A failing agent is captured as an error in its slot; it never
/// sinks the other agents' runs.
/// </summary>
public sealed class ReviewOrchestrator(IServiceProvider services, ILogger<ReviewOrchestrator> logger)
{
    public static readonly string[] AgentNames = ["quality", "security", "docs"];

    public async Task<OrchestratedReview> ReviewAsync(ReviewRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = AgentReviewDiagnostics.Source.StartActivity("review.fanout");
        var total = Stopwatch.StartNew();
        var runs = await Task.WhenAll(AgentNames.Select(name => RunAgentAsync(name, request, cancellationToken)));
        total.Stop();
        activity?.SetTag("agents.total", runs.Length);
        activity?.SetTag("agents.succeeded", runs.Count(r => r.Findings is not null));

        logger.LogInformation(
            "Fan-out complete: {Succeeded}/{Total} agents succeeded, sum of agent time {SumMs} ms, wall clock {TotalMs} ms",
            runs.Count(r => r.Findings is not null),
            runs.Length,
            (long)runs.Sum(r => r.Elapsed.TotalMilliseconds),
            total.ElapsedMilliseconds);

        return new OrchestratedReview(runs, total.Elapsed);
    }

    private async Task<AgentRunResult> RunAgentAsync(string name, ReviewRequest request, CancellationToken cancellationToken)
    {
        var agent = services.GetRequiredKeyedService<IReviewAgent>(name);
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Agent {Agent} starting", name);
        try
        {
            var findings = await agent.ReviewAsync(request, cancellationToken);
            stopwatch.Stop();
            logger.LogInformation("Agent {Agent} finished: {Count} finding(s) in {ElapsedMs} ms", name, findings.Count, stopwatch.ElapsedMilliseconds);
            return new AgentRunResult(name, findings, null, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            logger.LogError(ex, "Agent {Agent} failed after {ElapsedMs} ms", name, stopwatch.ElapsedMilliseconds);
            return new AgentRunResult(name, null, ex.Message, stopwatch.Elapsed);
        }
    }
}
