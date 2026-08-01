using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentReview.Tests;

public class StaticAnalysisServerTests
{
    // Checklist item 1 state: the server connects with zero tools. This flips
    // to asserting the expected tools once analyze_csharp lands in item 2.
    [Fact]
    public void ServerAssembly_ExposesNoMcpToolsYet()
    {
        var assembly = Assembly.Load("AgentReview.McpServers.StaticAnalysis");

        var toolTypes = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .ToList();

        Assert.Empty(toolTypes);
    }
}
