namespace AgentReview.Agents.Diff;

/// <summary>
/// One new-side line of a diff hunk. <paramref name="NewLineNumber"/> is the
/// line's position in the new version of the file, computed from the hunk header,
/// which is what makes analyzer results on hunk snippets mappable back to real lines.
/// </summary>
public readonly record struct DiffLine(int NewLineNumber, string Content, bool IsAdded);
