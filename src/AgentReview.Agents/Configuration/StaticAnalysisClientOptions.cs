namespace AgentReview.Agents.Configuration;

public sealed class StaticAnalysisClientOptions
{
    public const string SectionName = "StaticAnalysisServer";

    /// <summary>Executable that starts the MCP server over stdio, e.g. "dotnet".</summary>
    public string Command { get; set; } = "";

    public string[] Arguments { get; set; } = [];

    /// <summary>Working directory for the server process; null inherits the caller's.</summary>
    public string? WorkingDirectory { get; set; }
}
