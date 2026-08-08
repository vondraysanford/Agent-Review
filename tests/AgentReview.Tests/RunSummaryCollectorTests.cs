using System.Diagnostics;
using AgentReview.Agents;
using AgentReview.Orchestrator;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentReview.Tests;

/// <summary>
/// Pins the per-review aggregation: token and call totals per trace, error
/// counting, cost only when rates are configured, and trace isolation.
/// </summary>
public class RunSummaryCollectorTests
{
    private static RunSummaryCollector CreateCollector(decimal input = 0, decimal output = 0) =>
        new(Options.Create(new PricingOptions
        {
            InputPerMillionTokens = input,
            OutputPerMillionTokens = output,
        }));

    private static void EmitLlmSpan(long inTokens, long outTokens)
    {
        using var span = AgentReviewDiagnostics.Source.StartActivity("llm.complete");
        span?.SetTag("llm.input_tokens", inTokens);
        span?.SetTag("llm.output_tokens", outTokens);
    }

    private static void EmitToolSpan(bool failed)
    {
        using var span = AgentReviewDiagnostics.Source.StartActivity("tool.analyze_csharp");
        if (failed)
        {
            span?.SetStatus(ActivityStatusCode.Error, "boom");
        }
    }

    [Fact]
    public void Aggregates_TokensCallsAndFailures_PerTrace()
    {
        using var collector = CreateCollector();
        ActivityTraceId traceId;
        using (var root = new Activity("review").Start())
        {
            traceId = root.TraceId;
            EmitLlmSpan(100, 50);
            EmitLlmSpan(200, 75);
            EmitToolSpan(failed: false);
            EmitToolSpan(failed: true);
            using (AgentReviewDiagnostics.Source.StartActivity("review.fanout"))
            {
            }
        }

        var summary = collector.Collect(traceId);

        Assert.Equal(300, summary.InputTokens);
        Assert.Equal(125, summary.OutputTokens);
        Assert.Equal(2, summary.LlmCalls);
        Assert.Equal(2, summary.ToolCalls);
        Assert.Equal(1, summary.ToolFailures);
        Assert.True(summary.TotalLatency >= TimeSpan.Zero);
    }

    [Fact]
    public void Cost_ComputedFromConfiguredRates_NullWhenUnset()
    {
        using var priced = CreateCollector(input: 5m, output: 25m);
        ActivityTraceId tracePriced;
        using (var root = new Activity("review").Start())
        {
            tracePriced = root.TraceId;
            EmitLlmSpan(1_000_000, 100_000);
        }

        var withCost = priced.Collect(tracePriced);
        Assert.Equal(5m + 2.5m, withCost.EstimatedCostUsd);

        using var unpriced = CreateCollector();
        ActivityTraceId traceUnpriced;
        using (var root = new Activity("review").Start())
        {
            traceUnpriced = root.TraceId;
            EmitLlmSpan(1000, 100);
        }

        Assert.Null(unpriced.Collect(traceUnpriced).EstimatedCostUsd);
    }

    [Fact]
    public void SeparateTraces_DoNotMix()
    {
        using var collector = CreateCollector();
        ActivityTraceId first, second;
        using (var root = new Activity("review-a").Start())
        {
            first = root.TraceId;
            EmitLlmSpan(100, 10);
        }

        using (var root = new Activity("review-b").Start())
        {
            second = root.TraceId;
            EmitLlmSpan(200, 20);
        }

        Assert.Equal(100, collector.Collect(first).InputTokens);
        Assert.Equal(200, collector.Collect(second).InputTokens);
    }

    [Fact]
    public void NoSpans_YieldsEmptySummary()
    {
        using var collector = CreateCollector();

        var summary = collector.Collect(ActivityTraceId.CreateRandom());

        Assert.Equal(0, summary.LlmCalls);
        Assert.Equal(0, summary.InputTokens);
        Assert.Null(summary.EstimatedCostUsd);
    }
}
