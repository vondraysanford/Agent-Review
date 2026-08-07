using AgentReview.Agents.Diff;
using Xunit;

namespace AgentReview.Tests;

/// <summary>
/// Pins the diff parser's line mapping, the foundation for grounding analyzer
/// findings in real file positions. Fixtures are git-style unified diffs.
/// </summary>
public class UnifiedDiffParserTests
{
    private const string SingleHunkDiff = """
        diff --git a/src/A.cs b/src/A.cs
        index 1111111..2222222 100644
        --- a/src/A.cs
        +++ b/src/A.cs
        @@ -10,7 +10,8 @@ public class A
             public void M()
             {
                 int x = 1;
        +        int unused = 42;
                 Console.WriteLine(x);
             }
         }
        """;

    [Fact]
    public void SingleHunk_MapsNewLineNumbersAndAddedFlags()
    {
        var files = UnifiedDiffParser.Parse(SingleHunkDiff);

        var file = Assert.Single(files);
        Assert.Equal("src/A.cs", file.Path);
        Assert.Equal(DiffChangeKind.Modified, file.Kind);
        Assert.Equal(7, file.NewLines.Count);
        Assert.Equal(10, file.NewLines[0].NewLineNumber);
        Assert.False(file.NewLines[0].IsAdded);
        Assert.Equal(13, file.NewLines[3].NewLineNumber);
        Assert.True(file.NewLines[3].IsAdded);
        Assert.Equal("        int unused = 42;", file.NewLines[3].Content);
        Assert.Equal(16, file.NewLines[6].NewLineNumber);
    }

    [Fact]
    public void MultipleHunks_ContinueMappingAcrossHunks()
    {
        const string diff = """
            diff --git a/src/A.cs b/src/A.cs
            index 1111111..2222222 100644
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -1,2 +1,3 @@
             using System;
            +using System.Linq;
             namespace X;
            @@ -30,2 +31,3 @@ public class A
                 // end
            +    // added far below
             }
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));
        Assert.Equal(6, file.NewLines.Count);
        Assert.Equal(2, file.NewLines[1].NewLineNumber);
        Assert.True(file.NewLines[1].IsAdded);
        Assert.Equal(31, file.NewLines[3].NewLineNumber);
        Assert.Equal(32, file.NewLines[4].NewLineNumber);
        Assert.True(file.NewLines[4].IsAdded);
    }

    [Fact]
    public void NewFile_AllLinesAdded_KindAdded()
    {
        const string diff = """
            diff --git a/src/New.cs b/src/New.cs
            new file mode 100644
            index 0000000..1111111
            --- /dev/null
            +++ b/src/New.cs
            @@ -0,0 +1,3 @@
            +using System;
            +
            +namespace X;
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));
        Assert.Equal("src/New.cs", file.Path);
        Assert.Equal(DiffChangeKind.Added, file.Kind);
        Assert.Equal(3, file.NewLines.Count);
        Assert.All(file.NewLines, l => Assert.True(l.IsAdded));
        Assert.Equal(1, file.NewLines[0].NewLineNumber);
        Assert.Equal(3, file.NewLines[2].NewLineNumber);
    }

    [Fact]
    public void DeletedFile_KindDeleted_NoNewLines()
    {
        const string diff = """
            diff --git a/src/Gone.cs b/src/Gone.cs
            deleted file mode 100644
            index 1111111..0000000
            --- a/src/Gone.cs
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -using System;
            -namespace X;
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));
        Assert.Equal("src/Gone.cs", file.Path);
        Assert.Equal(DiffChangeKind.Deleted, file.Kind);
        Assert.Empty(file.NewLines);
    }

    [Fact]
    public void RenameWithEdits_UsesNewPath()
    {
        const string diff = """
            diff --git a/src/Old.cs b/src/New.cs
            similarity index 90%
            rename from src/Old.cs
            rename to src/New.cs
            index 1111111..2222222 100644
            --- a/src/Old.cs
            +++ b/src/New.cs
            @@ -1,2 +1,3 @@
             using System;
            +// annotated
             namespace X;
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));
        Assert.Equal("src/New.cs", file.Path);
        Assert.Equal(DiffChangeKind.Renamed, file.Kind);
        Assert.Equal(3, file.NewLines.Count);
    }

    [Fact]
    public void HunkHeaderWithoutCounts_DefaultsToOne()
    {
        const string diff = """
            diff --git a/a.cs b/a.cs
            index 1111111..2222222 100644
            --- a/a.cs
            +++ b/a.cs
            @@ -1 +1 @@
            -old
            +new
            """;

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));
        var line = Assert.Single(file.NewLines);
        Assert.Equal(1, line.NewLineNumber);
        Assert.Equal("new", line.Content);
        Assert.True(line.IsAdded);
    }

    [Fact]
    public void CrlfDiff_TrimsCarriageReturns()
    {
        var diff = string.Join("\r\n",
            "diff --git a/a.cs b/a.cs",
            "index 1111111..2222222 100644",
            "--- a/a.cs",
            "+++ b/a.cs",
            "@@ -1,1 +1,2 @@",
            " int x;",
            "+int y;");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));
        Assert.Equal(2, file.NewLines.Count);
        Assert.Equal("int y;", file.NewLines[1].Content);
    }

    [Fact]
    public void NonDiffInput_ReturnsEmpty()
    {
        Assert.Empty(UnifiedDiffParser.Parse("hello world\nthis is not a diff\n"));
    }
}
