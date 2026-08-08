using System.Text.Json;
using AgentReview.Api;
using AgentReview.Orchestrator;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// The local review API. Run from the repo root so the MCP server's relative
// project path resolves: dotnet run --project src/AgentReview.Api
// Binds http://localhost:5100 (AirPlay owns 5000 on macOS; that is config, not code).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);

builder.Services.AddAgentReview(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var telemetryEnabled = builder.Configuration.GetSection(TelemetryOptions.SectionName)
    .Get<TelemetryOptions>()?.Enabled == true;
if (telemetryEnabled)
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("agentreview-api"))
        .WithTracing(t => t.AddSource("AgentReview").AddConsoleExporter());
}

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/review", async (
    ReviewApiRequest request,
    ReviewOrchestrator orchestrator,
    ReviewSynthesizer synthesizer,
    RunSummaryCollector collector,
    AgentReview.Agents.GitHub.IPullRequestClient pullRequests,
    CancellationToken cancellationToken) =>
{
    var outcome = await ReviewEndpoint.HandleAsync(
        request, orchestrator, synthesizer, collector, pullRequests, cancellationToken);

    return outcome switch
    {
        ReviewAccepted accepted => Results.Ok(new
        {
            findings = accepted.Findings,
            runs = accepted.Runs.Select(r => new
            {
                agent = r.Agent,
                count = r.Findings?.Count,
                error = r.Error,
                seconds = Math.Round(r.Elapsed.TotalSeconds, 1),
            }),
            duplicatesMerged = accepted.DuplicatesMerged,
            summary = new
            {
                seconds = Math.Round(accepted.Summary.TotalLatency.TotalSeconds, 1),
                llmCalls = accepted.Summary.LlmCalls,
                inputTokens = accepted.Summary.InputTokens,
                outputTokens = accepted.Summary.OutputTokens,
                toolCalls = accepted.Summary.ToolCalls,
                toolFailures = accepted.Summary.ToolFailures,
                estimatedCostUsd = accepted.Summary.EstimatedCostUsd,
            },
        }),
        ReviewRejected rejected when rejected.StatusCode == 400 => Results.BadRequest(new { error = rejected.Error }),
        ReviewRejected rejected => Results.Problem(statusCode: rejected.StatusCode, detail: rejected.Error),
        _ => Results.Problem(statusCode: 500, detail: "Unexpected outcome."),
    };
});

app.Run();
