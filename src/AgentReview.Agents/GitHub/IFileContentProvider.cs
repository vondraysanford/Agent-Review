namespace AgentReview.Agents.GitHub;

/// <summary>
/// Fetches the full new-revision content of a repository file, or null when it is
/// unavailable. Context is an enhancement: implementations report failure as null so
/// reviews degrade to diff-only analysis instead of failing.
/// </summary>
public interface IFileContentProvider
{
    Task<string?> GetFileContentAsync(RepoReference repo, string path, CancellationToken cancellationToken = default);
}
