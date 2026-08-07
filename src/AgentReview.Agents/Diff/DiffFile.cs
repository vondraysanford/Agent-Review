namespace AgentReview.Agents.Diff;

/// <summary>
/// One file's worth of a unified diff: the new-side path and the new-side text
/// (context plus added lines) of every hunk, in order. Removed lines are not kept;
/// agents review what the change produces, not what it deletes.
/// </summary>
public sealed record DiffFile(
    string Path,
    DiffChangeKind Kind,
    IReadOnlyList<DiffLine> NewLines)
{
    public bool IsCSharp => Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}
