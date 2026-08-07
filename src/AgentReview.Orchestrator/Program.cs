using System.Diagnostics;
using System.Text.Json;
using AgentReview.Agents;
using AgentReview.Agents.Configuration;
using AgentReview.Agents.Llm;
using AgentReview.Agents.StaticAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Composition root. Today it runs the quality agent on one diff file; in Phase 4 it
// grows into the concurrent fan-out across all three agents plus synthesis.
// Usage, from the repo root (the MCP server path in appsettings.json is repo-relative):
//   dotnet run --project src/AgentReview.Orchestrator -- <path-to-diff>
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);

builder.Services.AddOptions<AnthropicOptions>()
    .Bind(builder.Configuration.GetSection(AnthropicOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Model), "Anthropic:Model is required.")
    .Validate(
        o => !string.IsNullOrWhiteSpace(o.ApiKey)
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
        "Anthropic API key missing: set Anthropic:ApiKey in appsettings.local.json (gitignored) or export ANTHROPIC_API_KEY.")
    .ValidateOnStart();

builder.Services.AddOptions<StaticAnalysisClientOptions>()
    .Bind(builder.Configuration.GetSection(StaticAnalysisClientOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Command), "StaticAnalysisServer:Command is required.")
    .ValidateOnStart();

builder.Services.AddOptions<QualityAgentOptions>()
    .Bind(builder.Configuration.GetSection(QualityAgentOptions.SectionName))
    .Validate(o => o.MaxDiffChars > 0, "QualityAgent:MaxDiffChars must be positive.")
    .Validate(o => o.MaxOutputTokens > 0, "QualityAgent:MaxOutputTokens must be positive.")
    .ValidateOnStart();

builder.Services.AddSingleton<ILlmProvider, AnthropicLlmProvider>();
builder.Services.AddSingleton<IStaticAnalysisClient, StaticAnalysisMcpClient>();
builder.Services.AddKeyedSingleton<IReviewAgent, QualityAgent>("quality");

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Orchestrator");

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project src/AgentReview.Orchestrator -- <path-to-diff>");
    return 2;
}

var diff = await File.ReadAllTextAsync(args[0]);
var agent = host.Services.GetRequiredKeyedService<IReviewAgent>("quality");
var stopwatch = Stopwatch.StartNew();

try
{
    var findings = await agent.ReviewAsync(diff);

    var json = JsonSerializer.Serialize(
        findings,
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    Console.WriteLine(json);
    Console.WriteLine($"{findings.Count} finding(s) from agent '{agent.Name}' in {stopwatch.Elapsed.TotalSeconds:F1}s");
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "Review failed");
    return 1;
}
