using System.Text.RegularExpressions;

namespace AgentReview.Agents.Diff;

/// <summary>
/// Minimal parser for git-produced unified diffs. It extracts, per file, the new-side
/// path and the new-side lines of every hunk with their real line numbers.
/// Scope: diffs with "diff --git" file headers (what git diff and the GitHub API emit).
/// Plain multi-file diffs without git headers are not supported; a "---" file boundary
/// inside a hunk is indistinguishable from a removed line without lookahead.
/// </summary>
public static partial class UnifiedDiffParser
{
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    public static IReadOnlyList<DiffFile> Parse(string diff)
    {
        var files = new List<DiffFile>();
        string? newPath = null;
        string? fallbackPath = null;
        var kind = DiffChangeKind.Modified;
        var newLines = new List<DiffLine>();
        var currentNewLine = 0;
        var inHunk = false;
        var haveFile = false;

        void Flush()
        {
            if (haveFile && (newPath ?? fallbackPath) is { } path)
            {
                files.Add(new DiffFile(path, kind, newLines));
            }

            newPath = null;
            fallbackPath = null;
            kind = DiffChangeKind.Modified;
            newLines = [];
            currentNewLine = 0;
            inHunk = false;
            haveFile = false;
        }

        foreach (var rawLine in diff.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                haveFile = true;
                var marker = line.LastIndexOf(" b/", StringComparison.Ordinal);
                if (marker >= 0)
                {
                    fallbackPath = line[(marker + 3)..];
                }

                continue;
            }

            if (inHunk)
            {
                if (line.Length == 0)
                {
                    // Git renders blank context lines as a single space; a truly empty
                    // element is a split artifact at the end of the input.
                    continue;
                }

                switch (line[0])
                {
                    case '+':
                        newLines.Add(new DiffLine(currentNewLine++, line[1..], IsAdded: true));
                        continue;
                    case ' ':
                        newLines.Add(new DiffLine(currentNewLine++, line[1..], IsAdded: false));
                        continue;
                    case '-':
                    case '\\': // "\ No newline at end of file"
                        continue;
                    case '@' when HunkHeader().Match(line) is { Success: true } next:
                        currentNewLine = int.Parse(next.Groups[1].Value);
                        continue;
                    default:
                        inHunk = false;
                        break; // falls through to header handling
                }
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                haveFile = true;
                if (line[4..] == "/dev/null")
                {
                    kind = DiffChangeKind.Added;
                }

                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                haveFile = true;
                var path = line[4..];
                if (path == "/dev/null")
                {
                    kind = DiffChangeKind.Deleted;
                }
                else
                {
                    newPath = path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;
                }

                continue;
            }

            if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                kind = DiffChangeKind.Renamed;
                fallbackPath = line["rename to ".Length..];
                continue;
            }

            if (HunkHeader().Match(line) is { Success: true } m)
            {
                inHunk = true;
                currentNewLine = int.Parse(m.Groups[1].Value);
                continue;
            }

            // Remaining header lines (index, mode, similarity, Binary files, commit text) carry
            // nothing the review needs.
        }

        Flush();
        return files;
    }
}
