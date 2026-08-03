namespace AgentReview.McpServers.StaticAnalysis;

/// <summary>
/// Server-local result shape shared by both tools. The Phase 2 agent schema
/// maps from this; it is deliberately not that schema.
/// </summary>
public record AnalysisFinding(
    string RuleId,
    string Message,
    int Line,
    int Column,
    string Severity,
    string Source);
