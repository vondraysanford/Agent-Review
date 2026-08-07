using System.Diagnostics;
using System.Text.Json;
using AgentReview.Agents;
using AgentReview.Agents.Configuration;
using AgentReview.Agents.GitHub;
using AgentReview.Agents.Llm;
using AgentReview.Agents.StaticAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Composition root. Today it runs the quality agent on one diff file; in Phase 4 it
// grows into the concurrent fan-out across all three agents plus synthesis.
// Usage, from the repo root (the MCP server path in appsettings.json is repo-relative):
//   dotnet run --project src/AgentReview.Orchestrator -- <path-to-diff> [--repo owner/name] [--ref branch-or-sha]
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

builder.Services.AddOptions<GitHubMcpOptions>()
    .Bind(builder.Configuration.GetSection(GitHubMcpOptions.SectionName))
    .Validate(
        o => Uri.TryCreate(o.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
        "GitHubMcp:Endpoint must be an absolute https URL.")
    .ValidateOnStart();

builder.Services.AddSingleton<ILlmProvider, AnthropicLlmProvider>();
builder.Services.AddSingleton<IStaticAnalysisClient, StaticAnalysisMcpClient>();
builder.Services.AddSingleton<IFileContentProvider, GitHubMcpFileContentProvider>();
builder.Services.AddKeyedSingleton<IReviewAgent, QualityAgent>("quality");

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Orchestrator");

string? diffPath = null;
string? repoArg = null;
string? refArg = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--repo":
            repoArg = ++i < args.Length ? args[i] : null;
            break;
        case "--ref":
            refArg = ++i < args.Length ? args[i] : null;
            break;
        default:
            diffPath = args[i];
            break;
    }
}

if (diffPath is null)
{
    Console.Error.WriteLine("Usage: dotnet run --project src/AgentReview.Orchestrator -- <path-to-diff> [--repo owner/name] [--ref branch-or-sha]");
    return 2;
}

RepoReference? repo = null;
if (repoArg is not null)
{
    var parts = repoArg.Split('/');
    if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
    {
        Console.Error.WriteLine("--repo must be owner/name, e.g. vondraysanford/Agent-Review");
        return 2;
    }

    var gitHubOptions = host.Services.GetRequiredService<IOptions<GitHubMcpOptions>>().Value;
    if (string.IsNullOrWhiteSpace(gitHubOptions.Token))
    {
        Console.Error.WriteLine("--repo requires GitHubMcp:Token; set it in appsettings.local.json (gitignored).");
        return 2;
    }

    repo = new RepoReference(parts[0], parts[1], refArg);
}

var diff = await File.ReadAllTextAsync(diffPath);
var agent = host.Services.GetRequiredKeyedService<IReviewAgent>("quality");
var stopwatch = Stopwatch.StartNew();

try
{
    var findings = await agent.ReviewAsync(new ReviewRequest(diff, repo));

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
