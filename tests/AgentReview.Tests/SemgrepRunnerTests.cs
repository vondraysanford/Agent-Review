using AgentReview.McpServers.StaticAnalysis;
using Xunit;

namespace AgentReview.Tests;

/// <summary>
/// Offline tests for the Semgrep JSON mapping and input validation. The live
/// CLI path (network, registry rules) is covered by the Claude Code
/// verification, not unit tests.
/// </summary>
public class SemgrepRunnerTests
{
    // Trimmed from real `semgrep scan --json` output.
    private const string SampleOutput = """
        {
          "version": "1.172.0",
          "results": [
            {
              "check_id": "csharp.lang.security.injection.tainted-sql-string.tainted-sql-string",
              "path": "Snippet.cs",
              "start": { "line": 7, "col": 19, "offset": 180 },
              "end": { "line": 7, "col": 78, "offset": 239 },
              "extra": {
                "message": "User data flows into this manually-constructed SQL string.",
                "severity": "ERROR",
                "metadata": {}
              }
            },
            {
              "check_id": "generic.secrets.security.detected-generic-secret.detected-generic-secret",
              "path": "Snippet.cs",
              "start": { "line": 6, "col": 36, "offset": 120 },
              "end": { "line": 6, "col": 62, "offset": 146 },
              "extra": {
                "message": "Generic Secret detected",
                "severity": "INFO",
                "metadata": {}
              }
            }
          ],
          "errors": [],
          "paths": { "scanned": ["Snippet.cs"] }
        }
        """;

    [Fact]
    public void ParseResults_MapsFindingsInLineOrder()
    {
        var findings = SemgrepRunner.ParseResults(SampleOutput);

        Assert.Equal(2, findings.Count);

        Assert.Equal("generic.secrets.security.detected-generic-secret.detected-generic-secret", findings[0].RuleId);
        Assert.Equal(6, findings[0].Line);
        Assert.Equal(36, findings[0].Column);
        Assert.Equal("Info", findings[0].Severity);
        Assert.Equal("semgrep", findings[0].Source);

        Assert.Equal("csharp.lang.security.injection.tainted-sql-string.tainted-sql-string", findings[1].RuleId);
        Assert.Equal(7, findings[1].Line);
        Assert.Equal("Error", findings[1].Severity);
    }

    [Fact]
    public void ParseResults_EmptyResults_ReturnsNoFindings()
    {
        var findings = SemgrepRunner.ParseResults("""{ "results": [], "errors": [] }""");

        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("--config=evil")]
    [InlineData("-o/tmp/x")]
    [InlineData("p/default; rm -rf /")]
    [InlineData("")]
    public void Run_RejectsMalformedRuleset(string ruleset)
    {
        Assert.Throws<ArgumentException>(() => SemgrepRunner.Run("class C {}", ruleset));
    }
}
