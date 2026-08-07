namespace AgentReview.Agents.Configuration;

public sealed class SecurityAgentOptions : ReviewAgentOptions
{
    public const string SectionName = "SecurityAgent";

    /// <summary>
    /// Semgrep registry pack. p/csharp is the default because Phase 1 verified it
    /// catches the planted SQL injection without registry authentication.
    /// </summary>
    public string Ruleset { get; set; } = "p/csharp";
}
