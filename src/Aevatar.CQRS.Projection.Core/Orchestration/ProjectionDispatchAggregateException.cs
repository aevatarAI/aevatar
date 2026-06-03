namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Represents aggregated projector failures from a single event dispatch.
/// </summary>
public sealed class ProjectionDispatchAggregateException : Exception
{
    // Refactor (iter96/cluster-529):
    //   Old pattern: aggregate exception 只保留首个 inner exception,非首个 OCC 失败不被 classifier 识别
    //   New principle: aggregate failure 中所有 inner exceptions 都参与 classification,non-first OCC 也能被识别并重试
    public ProjectionDispatchAggregateException(
        IReadOnlyList<ProjectionDispatchFailure> failures)
        : base(BuildMessage(failures), BuildInnerException(failures))
    {
        Failures = failures.ToArray();
    }

    public IReadOnlyList<ProjectionDispatchFailure> Failures { get; }

    private static string BuildMessage(IReadOnlyList<ProjectionDispatchFailure> failures)
    {
        if (failures.Count == 0)
            return "Projection dispatch failed.";

        var projectorList = string.Join(", ", failures.Select(x => $"{x.ProjectorName}#{x.ProjectorOrder}"));
        return $"Projection dispatch failed for {failures.Count} projector(s): {projectorList}.";
    }

    private static Exception? BuildInnerException(IReadOnlyList<ProjectionDispatchFailure> failures) =>
        failures.Count switch
        {
            0 => null,
            1 => failures[0].Exception,
            _ => new AggregateException(failures.Select(static failure => failure.Exception)),
        };
}

/// <summary>
/// Failure details for a single projector invocation.
/// </summary>
public sealed record ProjectionDispatchFailure(
    string ProjectorName,
    int ProjectorOrder,
    Exception Exception);
