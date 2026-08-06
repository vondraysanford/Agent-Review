using System.Text.Json;
using AgentReview.Agents;
using Xunit;

namespace AgentReview.Tests;

/// <summary>
/// Pins the locked Phase 2 finding schema: the camelCase wire shape under
/// JsonSerializerDefaults.Web, the severity ordering synthesis will rank by,
/// and the mapping from the Phase 1 analyzer severity strings.
/// </summary>
public class FindingTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Finding_RoundTripsThroughWebJson()
    {
        var finding = new Finding(
            Issue: "The variable 'unused' is assigned but its value is never used",
            File: "docs/demo.cs",
            Line: 7,
            Severity: FindingSeverity.Warning,
            Suggestion: "Remove the unused assignment",
            Source: "roslyn");

        var json = JsonSerializer.Serialize(finding, WebOptions);

        Assert.Contains("\"issue\":", json);
        Assert.Contains("\"file\":", json);
        Assert.Contains("\"line\":7", json);
        Assert.Contains("\"severity\":\"Warning\"", json);
        Assert.Contains("\"suggestion\":", json);
        Assert.Contains("\"source\":\"roslyn\"", json);

        var roundTripped = JsonSerializer.Deserialize<Finding>(json, WebOptions);
        Assert.Equal(finding, roundTripped);
    }

    [Fact]
    public void NullSuggestion_RoundTripsThroughWebJson()
    {
        var finding = new Finding("Method name does not describe behavior", "src/A.cs", 12, FindingSeverity.Info, null, "llm");

        var json = JsonSerializer.Serialize(finding, WebOptions);
        var roundTripped = JsonSerializer.Deserialize<Finding>(json, WebOptions);

        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped.Suggestion);
        Assert.Equal(finding, roundTripped);
    }

    [Fact]
    public void Severities_RankInSynthesisOrder()
    {
        Assert.True(FindingSeverity.Error > FindingSeverity.Warning);
        Assert.True(FindingSeverity.Warning > FindingSeverity.Info);
    }

    [Fact]
    public void ParseSeverity_MapsAnalyzerVocabulary()
    {
        Assert.Equal(FindingSeverity.Error, Finding.ParseSeverity("Error"));
        Assert.Equal(FindingSeverity.Error, Finding.ParseSeverity("error"));
        Assert.Equal(FindingSeverity.Warning, Finding.ParseSeverity("Warning"));
        Assert.Equal(FindingSeverity.Info, Finding.ParseSeverity("Info"));
        Assert.Equal(FindingSeverity.Info, Finding.ParseSeverity("Hidden"));
        Assert.Equal(FindingSeverity.Info, Finding.ParseSeverity("Unknown"));
        Assert.Equal(FindingSeverity.Info, Finding.ParseSeverity(null));
    }
}
