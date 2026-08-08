using System.Diagnostics;

namespace AgentReview.Agents;

/// <summary>
/// The library's single ActivitySource. Instrumentation here has zero package
/// dependencies; hosts opt in by subscribing a listener or wiring the
/// OpenTelemetry SDK to the "AgentReview" source. When nobody listens,
/// StartActivity returns null and tracing costs nothing.
/// </summary>
public static class AgentReviewDiagnostics
{
    public static readonly ActivitySource Source = new("AgentReview");
}
