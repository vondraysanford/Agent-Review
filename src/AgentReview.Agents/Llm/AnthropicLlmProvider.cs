using System.Diagnostics;
using System.Text.Json;
using AgentReview.Agents.Configuration;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentReview.Agents.Llm;

/// <summary>
/// ILlmProvider backed by the official Anthropic SDK. The key comes from gitignored
/// config (Anthropic:ApiKey) or, when absent, the SDK's ANTHROPIC_API_KEY environment
/// fallback; the model id comes from configuration. Deliberately not unit tested: it is
/// a thin adapter over the SDK, verified live through the Orchestrator runner (the same
/// convention as SemgrepRunner's live CLI path).
/// </summary>
public sealed class AnthropicLlmProvider(
    IOptions<AnthropicOptions> options,
    ILogger<AnthropicLlmProvider> logger) : ILlmProvider
{
    private readonly AnthropicClient _client = string.IsNullOrWhiteSpace(options.Value.ApiKey)
        ? new()
        : new() { ApiKey = options.Value.ApiKey };

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        var parameters = new MessageCreateParams
        {
            Model = o.Model,
            MaxTokens = request.MaxOutputTokens,
            System = request.SystemPrompt,
            Messages = [new() { Role = Role.User, Content = request.UserContent }],
            OutputConfig = request.JsonSchema is null ? null : new OutputConfig
            {
                Format = new JsonOutputFormat
                {
                    Schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.JsonSchema)!,
                },
            },
        };

        using var activity = AgentReviewDiagnostics.Source.StartActivity("llm.complete");
        activity?.SetTag("llm.model", o.Model);

        var stopwatch = Stopwatch.StartNew();
        var response = await _client.Messages.Create(parameters);

        var text = string.Concat(
            response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        var stopReason = response.StopReason?.ToString();

        activity?.SetTag("llm.input_tokens", response.Usage.InputTokens);
        activity?.SetTag("llm.output_tokens", response.Usage.OutputTokens);
        activity?.SetTag("llm.stop_reason", stopReason);

        logger.LogInformation(
            "LLM call: model {Model}, {InputTokens} tokens in, {OutputTokens} tokens out, stop reason {StopReason}, {ElapsedMs} ms",
            o.Model,
            response.Usage.InputTokens,
            response.Usage.OutputTokens,
            stopReason,
            stopwatch.ElapsedMilliseconds);

        return new LlmResponse(text, response.Usage.InputTokens, response.Usage.OutputTokens, stopReason);
    }
}
