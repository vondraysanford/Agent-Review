namespace AgentReview.Agents.Configuration;

public sealed class GitHubMcpOptions
{
    public const string SectionName = "GitHubMcp";

    /// <summary>GitHub's hosted MCP server endpoint; from config, never code.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>
    /// Fine-grained personal access token with Contents: Read-only on the reviewed
    /// repositories. Belongs only in gitignored config (appsettings.local.json) or
    /// the environment. Never put this in a committed file.
    /// </summary>
    public string? Token { get; set; }
}
