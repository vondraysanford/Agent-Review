using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AgentReview.McpServers.StaticAnalysis;

/// <summary>
/// analyze_csharp in two layers: compiler diagnostics from a plain
/// CSharpCompilation, then NetAnalyzers CA rules loaded at runtime from the
/// DLLs the build copies next to the server. If the analyzer DLLs are missing,
/// layer 1 still works alone.
/// </summary>
public static class CSharpAnalyzer
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(LoadReferences);
    private static readonly Lazy<ImmutableArray<DiagnosticAnalyzer>> Analyzers = new(LoadAnalyzers);

    // The input is a snippet or diff, never a whole assembly, so rules about
    // assembly-level attributes and top-level namespace layout can never be
    // acted on and would fire on every single input.
    private static readonly ImmutableHashSet<string> SnippetInapplicableRules =
        ["CA1014", "CA1016", "CA1017", "CA1050"];

    // CA rules mostly default to disabled or Info outside an editor context.
    // The server reports facts and lets agents judge, so every configurable
    // analyzer rule is elevated to Warning to make it visible.
    private static readonly Lazy<ImmutableDictionary<string, ReportDiagnostic>> RuleOptions = new(
        () => Analyzers.Value
            .SelectMany(a => a.SupportedDiagnostics)
            .Where(d => !d.CustomTags.Contains(WellKnownDiagnosticTags.NotConfigurable))
            .Select(d => d.Id)
            .Distinct()
            .ToImmutableDictionary(
                id => id,
                id => SnippetInapplicableRules.Contains(id)
                    ? ReportDiagnostic.Suppress
                    : ReportDiagnostic.Warn));

    public static IReadOnlyList<AnalysisFinding> Analyze(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);

        var compilation = CSharpCompilation.Create(
            "AnalysisTarget",
            [tree],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithSpecificDiagnosticOptions(RuleOptions.Value));

        var diagnostics = compilation.GetDiagnostics().AsEnumerable();

        if (Analyzers.Value.Length > 0)
        {
            diagnostics = diagnostics.Concat(compilation
                .WithAnalyzers(Analyzers.Value)
                .GetAnalyzerDiagnosticsAsync()
                .GetAwaiter().GetResult());
        }

        return diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(ToFinding)
            .OrderBy(f => f.Line).ThenBy(f => f.Column).ThenBy(f => f.RuleId)
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

    private static ImmutableArray<DiagnosticAnalyzer> LoadAnalyzers()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "netanalyzers");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var loader = new LoadFromPathLoader();
        return
        [
            .. Directory.EnumerateFiles(directory, "*.dll")
                .Select(path => new AnalyzerFileReference(path, loader))
                .SelectMany(reference => reference.GetAnalyzers(LanguageNames.CSharp)),
        ];
    }

    private sealed class LoadFromPathLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath)
        {
        }

        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }
}
