namespace Aevatar.Workflow.Projection.ReadModels;

/// <summary>
/// The bounded full-graph candidate for one owner exceeds the repair/cutover mutation budget
/// (the shared ProjectionGraphDeltaContract.MaximumRepairOrCutoverMutationCount effective
/// mutations). Carries the measured shape so the scope can durably abort the cutover or roll
/// the route back to the compatibility writer instead of retrying a candidate that can never
/// fit.
/// </summary>
public sealed class ProjectionGraphCandidateOverBoundException : InvalidOperationException
{
    public ProjectionGraphCandidateOverBoundException(int mutationCount, int limit)
        : base($"Workflow graph candidate requires {mutationCount} mutations; " +
               $"the bounded cutover limit is {limit}.")
    {
        MutationCount = mutationCount;
        Limit = limit;
    }

    public int MutationCount { get; }

    public int Limit { get; }
}
