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

/// <summary>
/// Fetches a pull request's unified diff, or null when unavailable. Same
/// degrade-to-null contract as file content: the caller decides what failure means.
/// </summary>
public interface IPullRequestClient
{
    Task<string?> GetPullRequestDiffAsync(RepoReference repo, int number, CancellationToken cancellationToken = default);
}
