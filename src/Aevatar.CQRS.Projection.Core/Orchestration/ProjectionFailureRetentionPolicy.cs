using Google.Protobuf.Collections;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public static class ProjectionFailureRetentionPolicy
{
    public const int DefaultMaxRetainedFailures = 64;

    public static void Trim(
        RepeatedField<ProjectionScopeFailure> failures,
        int maxRetainedFailures = DefaultMaxRetainedFailures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        var boundedMax = Math.Max(1, maxRetainedFailures);
        while (failures.Count > boundedMax)
            failures.RemoveAt(0);
    }
}
