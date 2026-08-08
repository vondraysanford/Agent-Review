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
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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

builder.Services.AddOptions<SecurityAgentOptions>()
    .Bind(builder.Configuration.GetSection(SecurityAgentOptions.SectionName))
    .Validate(o => o.MaxDiffChars > 0, "SecurityAgent:MaxDiffChars must be positive.")
    .Validate(o => o.MaxOutputTokens > 0, "SecurityAgent:MaxOutputTokens must be positive.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Ruleset), "SecurityAgent:Ruleset is required.")
    .ValidateOnStart();

builder.Services.AddOptions<DocsAgentOptions>()
    .Bind(builder.Configuration.GetSection(DocsAgentOptions.SectionName))
    .Validate(o => o.MaxDiffChars > 0, "DocsAgent:MaxDiffChars must be positive.")
    .Validate(o => o.MaxOutputTokens > 0, "DocsAgent:MaxOutputTokens must be positive.")
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
builder.Services.AddKeyedSingleton<IReviewAgent, SecurityAgent>("security");
builder.Services.AddKeyedSingleton<IReviewAgent, DocsAgent>("docs");
builder.Services.AddSingleton<AgentReview.Orchestrator.ReviewOrchestrator>();
builder.Services.AddSingleton<AgentReview.Orchestrator.ReviewSynthesizer>();

var telemetryEnabled = builder.Configuration.GetSection(AgentReview.Orchestrator.TelemetryOptions.SectionName)
    .Get<AgentReview.Orchestrator.TelemetryOptions>()?.Enabled == true;

builder.Services.AddOptions<AgentReview.Orchestrator.SynthesisOptions>()
    .Bind(builder.Configuration.GetSection(AgentReview.Orchestrator.SynthesisOptions.SectionName))
    .Validate(o => o.MaxOutputTokens > 0, "Synthesis:MaxOutputTokens must be positive.")
    .ValidateOnStart();

builder.Services.AddOptions<AgentReview.Orchestrator.PricingOptions>()
    .Bind(builder.Configuration.GetSection(AgentReview.Orchestrator.PricingOptions.SectionName))
    .Validate(o => o.InputPerMillionTokens >= 0 && o.OutputPerMillionTokens >= 0, "Pricing rates must be non-negative.")
    .ValidateOnStart();
builder.Services.AddSingleton<AgentReview.Orchestrator.RunSummaryCollector>();

builder.Services.AddOptions<AgentReview.Orchestrator.BudgetOptions>()
    .Bind(builder.Configuration.GetSection(AgentReview.Orchestrator.BudgetOptions.SectionName))
    .Validate(o => o.MaxPerReviewUsd >= 0, "Budget:MaxPerReviewUsd must be non-negative.")
    .ValidateOnStart();
builder.Services.AddSingleton<AgentReview.Orchestrator.BudgetGuard>();
builder.Services.AddSingleton<AgentReview.Orchestrator.EvalRunner>();

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Orchestrator");

// The host is used as a DI container, not a running service, so the tracer is
// built directly; disposing it at process exit flushes the exporter.
using var tracerProvider = telemetryEnabled
    ? OpenTelemetry.Sdk.CreateTracerProviderBuilder()
        .ConfigureResource(r => r.AddService("agentreview-orchestrator"))
        .AddSource("AgentReview")
        .AddConsoleExporter()
        .Build()
    : null;

string? diffPath = null;
string? repoArg = null;
string? refArg = null;
var harnessMode = false;
var allMode = false;
var evalMode = false;
var scoreMode = false;
var agentName = "quality";
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--harness":
            harnessMode = true;
            break;
        case "--agent":
            agentName = ++i < args.Length ? args[i] : agentName;
            break;
        case "--all":
            allMode = true;
            break;
        case "--eval":
            evalMode = true;
            break;
        case "--score":
            scoreMode = true;
            break;
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

if (diffPath is null && !harnessMode && !evalMode)
{
    Console.Error.WriteLine("Usage: dotnet run --project src/AgentReview.Orchestrator -- <path-to-diff> [--agent name | --all] [--repo owner/name] [--ref branch-or-sha]");
    Console.Error.WriteLine("       dotnet run --project src/AgentReview.Orchestrator -- --harness [--agent name | --all]");
    Console.Error.WriteLine("       dotnet run --project src/AgentReview.Orchestrator -- --eval [--score]");
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

if (evalMode)
{
    var evalRunner = host.Services.GetRequiredService<AgentReview.Orchestrator.EvalRunner>();
    var casesDir = Path.Combine(Directory.GetCurrentDirectory(), "evals", "cases");
    var resultsDir = Path.Combine(Directory.GetCurrentDirectory(), "evals", "results");
    try
    {
        return scoreMode
            ? await evalRunner.ScoreAsync(resultsDir)
            : await evalRunner.RunAsync(casesDir, resultsDir);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Eval run failed");
        return 1;
    }
}

var agent = host.Services.GetKeyedService<IReviewAgent>(agentName);
if (agent is null)
{
    Console.Error.WriteLine($"Unknown agent '{agentName}'. Known agents: quality, security, docs.");
    return 2;
}

var outputJson = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

if (harnessMode)
{
    return await RunHarnessAsync();
}

var diff = await File.ReadAllTextAsync(diffPath!);

if (allMode)
{
    using var collector = host.Services.GetRequiredService<AgentReview.Orchestrator.RunSummaryCollector>();
    var orchestrator = host.Services.GetRequiredService<AgentReview.Orchestrator.ReviewOrchestrator>();
    var synthesizer = host.Services.GetRequiredService<AgentReview.Orchestrator.ReviewSynthesizer>();

    AgentReview.Orchestrator.SynthesizedReview synthesized;
    System.Diagnostics.ActivityTraceId traceId;
    using (var reviewActivity = new System.Diagnostics.Activity("review").Start())
    {
        traceId = reviewActivity.TraceId;
        var review = await orchestrator.ReviewAsync(new ReviewRequest(diff, repo));
        synthesized = await synthesizer.SynthesizeAsync(review);
    }

    Console.WriteLine(JsonSerializer.Serialize(synthesized.Findings, outputJson));

    foreach (var run in synthesized.Runs)
    {
        Console.WriteLine(run.Findings is not null
            ? $"  {run.Agent}: {run.Findings.Count} finding(s) in {run.Elapsed.TotalSeconds:F1}s"
            : $"  {run.Agent}: FAILED ({run.Error})");
    }

    Console.WriteLine(
        $"{synthesized.Findings.Count} finding(s) after synthesis, {synthesized.DuplicatesMerged} duplicate(s) merged, fan-out {synthesized.TotalElapsed.TotalSeconds:F1}s");

    var summary = collector.Collect(traceId);
    var cost = summary.EstimatedCostUsd is { } usd ? $", ~${usd:F3} at configured rates" : "";
    Console.WriteLine(
        $"summary: {summary.TotalLatency.TotalSeconds:F1}s fan-out, {summary.LlmCalls} LLM call(s) ({summary.InputTokens} in / {summary.OutputTokens} out tokens), {summary.ToolCalls - summary.ToolFailures}/{summary.ToolCalls} tool call(s) ok{cost}");
    return synthesized.Runs.Any(r => r.Findings is not null) ? 0 : 1;
}

var stopwatch = Stopwatch.StartNew();

try
{
    var findings = await agent.ReviewAsync(new ReviewRequest(diff, repo));

    Console.WriteLine(JsonSerializer.Serialize(findings, outputJson));
    Console.WriteLine($"{findings.Count} finding(s) from agent '{agent.Name}' in {stopwatch.Elapsed.TotalSeconds:F1}s");
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "Review failed");
    return 1;
}

// The Phase 2 test harness: run the real agent over every committed sample diff,
// assert schema validity in code, and write the reviews to samples/reviews/ where
// they are committed as the repo's public sample-review artifacts. Reports all
// diffs before failing so one bad sample does not hide the rest.
async Task<int> RunHarnessAsync()
{
    var samplesDir = Path.Combine(Directory.GetCurrentDirectory(), "src", "AgentReview.Orchestrator", "samples");
    if (!Directory.Exists(samplesDir))
    {
        Console.Error.WriteLine($"Samples directory not found at {samplesDir}; run from the repo root.");
        return 2;
    }

    var label = allMode ? "synthesized" : agent.Name;
    var reviewsDir = Path.Combine(samplesDir, "reviews", label);
    Directory.CreateDirectory(reviewsDir);

    var failures = 0;
    var summary = new List<string>();
    foreach (var sampleFile in Directory.GetFiles(samplesDir, "*.diff").OrderBy(f => f, StringComparer.Ordinal))
    {
        var name = Path.GetFileNameWithoutExtension(sampleFile);
        try
        {
            var sampleDiff = await File.ReadAllTextAsync(sampleFile);
            IReadOnlyList<Finding> findings;
            if (allMode)
            {
                var orchestrator = host.Services.GetRequiredService<AgentReview.Orchestrator.ReviewOrchestrator>();
                var synthesizer = host.Services.GetRequiredService<AgentReview.Orchestrator.ReviewSynthesizer>();
                var fanOut = await orchestrator.ReviewAsync(new ReviewRequest(sampleDiff, repo));
                findings = (await synthesizer.SynthesizeAsync(fanOut)).Findings;
            }
            else
            {
                findings = await agent.ReviewAsync(new ReviewRequest(sampleDiff, repo));
            }

            var errors = ValidateSchema(findings, sampleDiff);
            var reviewJson = JsonSerializer.Serialize(findings, outputJson);
            var roundTripped = JsonSerializer.Deserialize<List<Finding>>(reviewJson, outputJson);
            if (roundTripped is null || !roundTripped.SequenceEqual(findings))
            {
                errors.Add("findings did not round-trip through web JSON");
            }

            if (errors.Count == 0)
            {
                await File.WriteAllTextAsync(Path.Combine(reviewsDir, $"{name}.review.json"), reviewJson + "\n");
                var bySource = findings.GroupBy(f => f.Source).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}");
                summary.Add($"PASS {label}/{name}: {findings.Count} finding(s) [{string.Join(", ", bySource)}]");
            }
            else
            {
                failures++;
                summary.Add($"FAIL {label}/{name}: {string.Join("; ", errors)}");
            }
        }
        catch (Exception ex)
        {
            failures++;
            summary.Add($"FAIL {label}/{name}: {ex.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Harness summary:");
    foreach (var line in summary)
    {
        Console.WriteLine("  " + line);
    }

    Console.WriteLine(failures == 0
        ? "All sample reviews are schema-valid."
        : $"{failures} sample review(s) failed.");
    return failures == 0 ? 0 : 1;
}

static List<string> ValidateSchema(IReadOnlyList<Finding> findings, string sampleDiff)
{
    var diffPaths = AgentReview.Agents.Diff.UnifiedDiffParser.Parse(sampleDiff)
        .Select(f => f.Path)
        .ToHashSet(StringComparer.Ordinal);

    var errors = new List<string>();
    foreach (var f in findings)
    {
        if (string.IsNullOrWhiteSpace(f.Issue)) errors.Add($"empty issue at {f.File}:{f.Line}");
        if (string.IsNullOrWhiteSpace(f.File)) errors.Add("empty file path");
        if (string.IsNullOrWhiteSpace(f.Source)) errors.Add($"empty source at {f.File}:{f.Line}");
        if (f.Line < 1) errors.Add($"line {f.Line} out of range at {f.File}");
        if (!Enum.IsDefined(f.Severity)) errors.Add($"undefined severity at {f.File}:{f.Line}");
        if (!diffPaths.Contains(f.File)) errors.Add($"file {f.File} not in the diff");
    }

    for (var i = 1; i < findings.Count; i++)
    {
        if (findings[i].Severity > findings[i - 1].Severity)
        {
            errors.Add($"ordering violated at index {i}: {findings[i].Severity} after {findings[i - 1].Severity}");
        }
    }

    return errors;
}
