using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AgentReview.McpServers.StaticAnalysis;

/// <summary>
/// Layer 1 of analyze_csharp: compiler diagnostics only. Parses the code,
/// compiles it against the host's reference assemblies, and reports syntax
/// and semantic diagnostics. Analyzer (CA) rules are layer 2, a separate item.
/// </summary>
public static class CSharpAnalyzer
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(LoadReferences);

    public static IReadOnlyList<AnalysisFinding> Analyze(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);

        var compilation = CSharpCompilation.Create(
            "AnalysisTarget",
            [tree],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return compilation.GetDiagnostics()
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(ToFinding)
            .OrderBy(f => f.Line).ThenBy(f => f.Column)
            .ToList();
    }

    private static AnalysisFinding ToFinding(Diagnostic diagnostic)
    {
        var position = diagnostic.Location.GetLineSpan().StartLinePosition;
        return new AnalysisFinding(
            diagnostic.Id,
            diagnostic.GetMessage(),
            position.Line + 1,
            position.Character + 1,
            diagnostic.Severity.ToString(),
            Source: "roslyn");
    }

    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        // The host runtime's own reference set; supplied snippets are plain C#
        // (plus whatever the shared framework carries), which is all Phase 1 needs.
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return
        [
            .. tpa.Split(Path.PathSeparator)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
        ];
    }
}
