namespace AgentReview.Agents;

/// <summary>
/// GitHub coordinates for the revision a diff belongs to. <paramref name="Ref"/> should
/// point at the diff's new side (branch or commit SHA); when null the repo default is used.
/// </summary>
public sealed record RepoReference(string Owner, string Name, string? Ref);

/// <summary>
/// One review's input: the diff itself plus optional repo coordinates. With coordinates,
/// agents can fetch surrounding file context; without them the diff is all there is.
/// </summary>
public sealed record ReviewRequest(string Diff, RepoReference? Repo = null);
