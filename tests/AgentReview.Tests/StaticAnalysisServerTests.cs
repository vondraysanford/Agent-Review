using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentReview.Tests;

public class StaticAnalysisServerTests
{
    [Fact]
    public void ServerAssembly_ExposesAnalyzeCsharpTool()
    {
        var assembly = Assembly.Load("AgentReview.McpServers.StaticAnalysis");

        var toolNames = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => n is not null)
            .ToList();

        Assert.Contains("analyze_csharp", toolNames);
    }
}
