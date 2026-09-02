using Google.Protobuf.Collections;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public static class ProjectionFailureRetentionPolicy
{
    public const int DefaultMaxRetainedFailures = 64;

    public const int DefaultMaxReplayAttempts = 3;

    public static IReadOnlyList<ProjectionFailureDiagnostic> Trim(
        RepeatedField<ProjectionFailureDiagnostic> failures,
        int maxRetainedFailures = DefaultMaxRetainedFailures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        var boundedMax = Math.Max(1, maxRetainedFailures);
        var dropped = new List<ProjectionFailureDiagnostic>();
        while (failures.Count > boundedMax)
        {
            dropped.Add(failures[0].Clone());
            failures.RemoveAt(0);
        }

        return dropped;
    }
}
