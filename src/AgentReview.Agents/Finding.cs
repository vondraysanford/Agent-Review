using System.Text.Json.Serialization;

namespace AgentReview.Agents;

/// <summary>
/// Severity of a <see cref="Finding"/>. Values are ordered so synthesis can rank
/// findings by comparing enum values directly. Serialized as the enum name
/// ("Warning", not 1) to match the Phase 1 wire vocabulary.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FindingSeverity>))]
public enum FindingSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// The shared finding schema. Every review agent returns exactly this shape.
/// Locked in Phase 2: synthesis and the eval harness both depend on it, so a
/// change here means changing them too. Serialize with
/// <c>JsonSerializerDefaults.Web</c> (camelCase), the convention the MCP server
/// already uses.
/// </summary>
/// <param name="Issue">What is wrong, in one sentence.</param>
/// <param name="File">Repo-relative path the finding is in.</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Severity">Ranked severity; see <see cref="FindingSeverity"/>.</param>
/// <param name="Suggestion">Concrete fix, or null when there is no specific one.</param>
/// <param name="Source">Which analyzer or LLM produced it, e.g. "roslyn", "semgrep", "llm".</param>
public record Finding(
    string Issue,
    string File,
    int Line,
    FindingSeverity Severity,
    string? Suggestion,
    string Source)
{
    /// <summary>
    /// Maps the analyzer severity vocabulary (Roslyn's DiagnosticSeverity strings,
    /// Semgrep's normalized PascalCase) to <see cref="FindingSeverity"/>.
    /// Case-insensitive; anything that is not Error or Warning (Info, Hidden,
    /// Unknown, null) folds to Info rather than failing, because a finding with a
    /// strange severity is still a finding.
    /// </summary>
    public static FindingSeverity ParseSeverity(string? severity) => severity?.ToLowerInvariant() switch
    {
        "error" => FindingSeverity.Error,
        "warning" => FindingSeverity.Warning,
        _ => FindingSeverity.Info,
    };
}
