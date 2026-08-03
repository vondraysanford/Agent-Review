using AgentReview.McpServers.StaticAnalysis;
using Xunit;

namespace AgentReview.Tests;

public class CSharpAnalyzerTests
{
    // Mirrors the planted-issue fixture in docs/PHASE-1-PLAN.md, minus the
    // Semgrep-only lines that need no compiler diagnostic.
    private const string PlantedIssues = """
        public class Demo
        {
            public void Run()
            {
                int unused = 42;
                return;
                System.Console.WriteLine("never runs");
            }
        }
        """;

    [Fact]
    public void PlantedIssues_ReportUnusedVariable()
    {
        var findings = CSharpAnalyzer.Analyze(PlantedIssues);

        var unused = Assert.Single(findings, f => f.RuleId == "CS0219");
        Assert.Equal(5, unused.Line);
        Assert.Equal("Warning", unused.Severity);
        Assert.Equal("roslyn", unused.Source);
    }

    [Fact]
    public void PlantedIssues_ReportUnreachableCode()
    {
        var findings = CSharpAnalyzer.Analyze(PlantedIssues);

        var unreachable = Assert.Single(findings, f => f.RuleId == "CS0162");
        Assert.Equal(7, unreachable.Line);
    }

    [Fact]
    public void CleanCode_ReturnsNoFindings()
    {
        var findings = CSharpAnalyzer.Analyze(
            "public static class Clean { public static int Add(int a, int b) => a + b; }");

        Assert.Empty(findings);
    }

    [Fact]
    public void PlantedDisposableLeak_ReportsCaFinding()
    {
        // An abandoned disposable local; CA2000's flow analysis skips subtler
        // shapes (e.g. returning stream.Length), verified against the analyzer.
        var findings = CSharpAnalyzer.Analyze("""
            public class Leaky
            {
                public void Open(string path)
                {
                    var stream = new System.IO.FileStream(path, System.IO.FileMode.Open);
                    System.Console.WriteLine(path);
                }
            }
            """);

        Assert.Contains(findings, f => f.RuleId == "CA2000");
    }

    [Fact]
    public void InvalidCode_ReportsErrorSeverity()
    {
        var findings = CSharpAnalyzer.Analyze("public class Broken {");

        Assert.Contains(findings, f => f.Severity == "Error");
    }
}
