namespace AgentReview.Agents.Llm;

/// <summary>
/// Request to an LLM provider. <paramref name="JsonSchema"/>, when set, is a raw JSON Schema
/// string the response text must conform to; providers with native structured output use it
/// directly, others may embed it in the prompt.
/// </summary>
public sealed record LlmRequest(
    string SystemPrompt,
    string UserContent,
    string? JsonSchema,
    int MaxOutputTokens);

public sealed record LlmResponse(
    string Text,
    long InputTokens,
    long OutputTokens,
    string? StopReason);

/// <summary>
/// The LLM seam, following the DocQuery provider pattern: providers swap via DI and
/// configuration, no provider types cross this boundary. Extended beyond DocQuery's
/// plain-string shape because review agents need structured output and token usage.
/// </summary>
public interface ILlmProvider
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
}
