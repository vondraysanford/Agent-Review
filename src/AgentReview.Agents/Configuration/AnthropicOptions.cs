namespace AgentReview.Agents.Configuration;

public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>
    /// Claude model id, e.g. "claude-opus-5". Required; there is no default because
    /// model names are configuration, never code.
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// Optional API key. Belongs only in gitignored config (appsettings.local.json)
    /// or the environment; when null the SDK falls back to ANTHROPIC_API_KEY.
    /// Never put this in a committed file.
    /// </summary>
    public string? ApiKey { get; set; }
}
