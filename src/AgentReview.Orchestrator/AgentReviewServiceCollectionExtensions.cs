using AgentReview.Agents;
using AgentReview.Agents.Configuration;
using AgentReview.Agents.GitHub;
using AgentReview.Agents.Llm;
using AgentReview.Agents.StaticAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentReview.Orchestrator;

/// <summary>
/// The one composition root for the review pipeline, shared by the console runner
/// and the API host: options with startup validation, the three seams, the keyed
/// agent roster, orchestration, and the cost guardrails.
/// </summary>
public static class AgentReviewServiceCollectionExtensions
{
    public static IServiceCollection AddAgentReview(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AnthropicOptions>()
            .Bind(configuration.GetSection(AnthropicOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Model), "Anthropic:Model is required.")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ApiKey)
                    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
                "Anthropic API key missing: set Anthropic:ApiKey in appsettings.local.json (gitignored) or export ANTHROPIC_API_KEY.")
            .ValidateOnStart();

        services.AddOptions<StaticAnalysisClientOptions>()
            .Bind(configuration.GetSection(StaticAnalysisClientOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Command), "StaticAnalysisServer:Command is required.")
            .ValidateOnStart();

        services.AddOptions<QualityAgentOptions>()
            .Bind(configuration.GetSection(QualityAgentOptions.SectionName))
            .Validate(o => o.MaxDiffChars > 0, "QualityAgent:MaxDiffChars must be positive.")
            .Validate(o => o.MaxOutputTokens > 0, "QualityAgent:MaxOutputTokens must be positive.")
            .ValidateOnStart();

        services.AddOptions<SecurityAgentOptions>()
            .Bind(configuration.GetSection(SecurityAgentOptions.SectionName))
            .Validate(o => o.MaxDiffChars > 0, "SecurityAgent:MaxDiffChars must be positive.")
            .Validate(o => o.MaxOutputTokens > 0, "SecurityAgent:MaxOutputTokens must be positive.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Ruleset), "SecurityAgent:Ruleset is required.")
            .ValidateOnStart();

        services.AddOptions<DocsAgentOptions>()
            .Bind(configuration.GetSection(DocsAgentOptions.SectionName))
            .Validate(o => o.MaxDiffChars > 0, "DocsAgent:MaxDiffChars must be positive.")
            .Validate(o => o.MaxOutputTokens > 0, "DocsAgent:MaxOutputTokens must be positive.")
            .ValidateOnStart();

        services.AddOptions<GitHubMcpOptions>()
            .Bind(configuration.GetSection(GitHubMcpOptions.SectionName))
            .Validate(
                o => Uri.TryCreate(o.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
                "GitHubMcp:Endpoint must be an absolute https URL.")
            .ValidateOnStart();

        services.AddOptions<SynthesisOptions>()
            .Bind(configuration.GetSection(SynthesisOptions.SectionName))
            .Validate(o => o.MaxOutputTokens > 0, "Synthesis:MaxOutputTokens must be positive.")
            .ValidateOnStart();

        services.AddOptions<PricingOptions>()
            .Bind(configuration.GetSection(PricingOptions.SectionName))
            .Validate(o => o.InputPerMillionTokens >= 0 && o.OutputPerMillionTokens >= 0, "Pricing rates must be non-negative.")
            .ValidateOnStart();

        services.AddOptions<BudgetOptions>()
            .Bind(configuration.GetSection(BudgetOptions.SectionName))
            .Validate(o => o.MaxPerReviewUsd >= 0, "Budget:MaxPerReviewUsd must be non-negative.")
            .ValidateOnStart();

        services.AddSingleton<ILlmProvider, AnthropicLlmProvider>();
        services.AddSingleton<IStaticAnalysisClient, StaticAnalysisMcpClient>();
        services.AddSingleton<GitHubMcpFileContentProvider>();
        services.AddSingleton<IFileContentProvider>(sp => sp.GetRequiredService<GitHubMcpFileContentProvider>());
        services.AddSingleton<IPullRequestClient>(sp => sp.GetRequiredService<GitHubMcpFileContentProvider>());
        services.AddKeyedSingleton<IReviewAgent, QualityAgent>("quality");
        services.AddKeyedSingleton<IReviewAgent, SecurityAgent>("security");
        services.AddKeyedSingleton<IReviewAgent, DocsAgent>("docs");
        services.AddSingleton<ReviewOrchestrator>();
        services.AddSingleton<ReviewSynthesizer>();
        services.AddSingleton<RunSummaryCollector>();
        services.AddSingleton<BudgetGuard>();
        services.AddSingleton<EvalRunner>();

        return services;
    }
}
