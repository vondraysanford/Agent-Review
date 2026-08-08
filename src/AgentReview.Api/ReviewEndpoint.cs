using System.Diagnostics;
using AgentReview.Agents;
using AgentReview.Agents.GitHub;
using AgentReview.Orchestrator;

namespace AgentReview.Api;

public sealed record PullRequestRef(string Owner, string Name, int Number);

public sealed record ReviewApiRequest(string? Diff, RepoReference? Repo, PullRequestRef? PullRequest);

/// <summary>Domain-level outcome; the host maps it to HTTP. Keeps the handler testable without a web stack.</summary>
public abstract record ReviewOutcome;

public sealed record ReviewAccepted(
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<AgentRunResult> Runs,
    int DuplicatesMerged,
    RunSummary Summary) : ReviewOutcome;

public sealed record ReviewRejected(int StatusCode, string Error) : ReviewOutcome;

/// <summary>
/// The /review handler: accepts a raw diff (optionally with repo coordinates for
/// GitHub context) or a pull request reference whose diff is fetched through the
/// GitHub MCP server. Local-only service by standing decision; there is no auth
/// because there is no public deployment.
/// </summary>
public static class ReviewEndpoint
{
    public static async Task<ReviewOutcome> HandleAsync(
        ReviewApiRequest request,
        ReviewOrchestrator orchestrator,
        ReviewSynthesizer synthesizer,
        RunSummaryCollector collector,
        IPullRequestClient pullRequests,
        CancellationToken cancellationToken)
    {
        var diff = request.Diff;
        var repo = request.Repo;

        if (request.PullRequest is { } pr)
        {
            repo = new RepoReference(pr.Owner, pr.Name, request.Repo?.Ref);
            diff = await pullRequests.GetPullRequestDiffAsync(repo, pr.Number, cancellationToken);
            if (string.IsNullOrWhiteSpace(diff))
            {
                return new ReviewRejected(502, $"Could not fetch the diff for {pr.Owner}/{pr.Name}#{pr.Number}.");
            }
        }

        if (string.IsNullOrWhiteSpace(diff))
        {
            return new ReviewRejected(400, "Provide a non-empty diff or a pullRequest reference.");
        }

        // Bad input is the client's fault, not an agent failure: reject unparseable
        // diffs here with a 400 instead of letting all three agents fail into a 502.
        if (AgentReview.Agents.Diff.UnifiedDiffParser.Parse(diff).Count == 0)
        {
            return new ReviewRejected(400, "The submitted content is not a unified diff.");
        }

        SynthesizedReview synthesized;
        ActivityTraceId traceId;
        using (var activity = new Activity("review").Start())
        {
            traceId = activity.TraceId;
            OrchestratedReview fanOut;
            try
            {
                fanOut = await orchestrator.ReviewAsync(new ReviewRequest(diff, repo), cancellationToken);
            }
            catch (ArgumentException ex)
            {
                return new ReviewRejected(400, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return new ReviewRejected(502, ex.Message);
            }

            synthesized = await synthesizer.SynthesizeAsync(fanOut, cancellationToken);
        }

        if (!synthesized.Runs.Any(r => r.Findings is not null))
        {
            var errors = string.Join("; ", synthesized.Runs.Select(r => $"{r.Agent}: {r.Error}"));
            return new ReviewRejected(502, $"All agents failed: {errors}");
        }

        return new ReviewAccepted(
            synthesized.Findings,
            synthesized.Runs,
            synthesized.DuplicatesMerged,
            collector.Collect(traceId));
    }
}
