namespace AgentReview.Orchestrator;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>
    /// When true, the OpenTelemetry SDK subscribes to the AgentReview source and
    /// exports spans to the console. Off by default so demo output stays clean;
    /// enable per run with Telemetry__Enabled=true or in appsettings.local.json.
    /// </summary>
    public bool Enabled { get; set; }
}
