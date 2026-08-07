namespace AgentReview.Agents;

/// <summary>
/// The shape every review agent shares: a diff in, schema-valid findings out.
/// Agents register as keyed services under <see cref="Name"/> so the orchestrator
/// can resolve them uniformly (the DocQuery provider pattern).
/// </summary>
public interface IReviewAgent
{
    string Name { get; }

    Task<IReadOnlyList<Finding>> ReviewAsync(string diff, CancellationToken cancellationToken = default);
}
