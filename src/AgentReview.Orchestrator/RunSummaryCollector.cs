using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace AgentReview.Orchestrator;

public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    /// <summary>USD per million input tokens for the configured model; 0 = unset, cost omitted.</summary>
    public decimal InputPerMillionTokens { get; set; }

    /// <summary>USD per million output tokens for the configured model; 0 = unset, cost omitted.</summary>
    public decimal OutputPerMillionTokens { get; set; }
}

public sealed record RunSummary(
    TimeSpan TotalLatency,
    long InputTokens,
    long OutputTokens,
    int LlmCalls,
    int ToolCalls,
    int ToolFailures,
    decimal? EstimatedCostUsd);

/// <summary>
/// Aggregates one review's numbers from the same "AgentReview" activity source the
/// exporter consumes, keyed by trace id so concurrent reviews never mix. Token
/// counts are measured; cost is those measurements multiplied by configured rates,
/// and it is reported as such (a stale configured price is an input, never a
/// measurement).
/// </summary>
public sealed class RunSummaryCollector : IDisposable
{
    private sealed class Accumulator
    {
        public TimeSpan TotalLatency;
        public long InputTokens;
        public long OutputTokens;
        public int LlmCalls;
        public int ToolCalls;
        public int ToolFailures;
    }

    private readonly ConcurrentDictionary<ActivityTraceId, Accumulator> _byTrace = new();
    private readonly ActivityListener _listener;
    private readonly PricingOptions _pricing;

    public RunSummaryCollector(IOptions<PricingOptions> pricing)
    {
        _pricing = pricing.Value;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "AgentReview",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = OnActivityStopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    private void OnActivityStopped(Activity activity)
    {
        var acc = _byTrace.GetOrAdd(activity.TraceId, _ => new Accumulator());
        lock (acc)
        {
            if (activity.OperationName == "llm.complete")
            {
                acc.LlmCalls++;
                acc.InputTokens += ToLong(activity.GetTagItem("llm.input_tokens"));
                acc.OutputTokens += ToLong(activity.GetTagItem("llm.output_tokens"));
            }
            else if (activity.OperationName.StartsWith("tool.", StringComparison.Ordinal))
            {
                acc.ToolCalls++;
                if (activity.Status == ActivityStatusCode.Error)
                {
                    acc.ToolFailures++;
                }
            }
            else if (activity.OperationName == "review.fanout")
            {
                acc.TotalLatency = activity.Duration;
            }
        }
    }

    /// <summary>Returns and clears the summary for one review's trace.</summary>
    public RunSummary Collect(ActivityTraceId traceId)
    {
        _byTrace.TryRemove(traceId, out var acc);
        acc ??= new Accumulator();

        decimal? cost = null;
        if (_pricing.InputPerMillionTokens > 0 || _pricing.OutputPerMillionTokens > 0)
        {
            cost = (acc.InputTokens * _pricing.InputPerMillionTokens
                + acc.OutputTokens * _pricing.OutputPerMillionTokens) / 1_000_000m;
        }

        return new RunSummary(
            acc.TotalLatency,
            acc.InputTokens,
            acc.OutputTokens,
            acc.LlmCalls,
            acc.ToolCalls,
            acc.ToolFailures,
            cost);
    }

    private static long ToLong(object? tagValue) => tagValue switch
    {
        long l => l,
        int i => i,
        string s when long.TryParse(s, out var parsed) => parsed,
        _ => 0,
    };

    public void Dispose() => _listener.Dispose();
}
