using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

public sealed partial class SkillRunnerExecutionDocument : IProjectionReadModel<SkillRunnerExecutionDocument>
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtc != null ? UpdatedAtUtc.ToDateTimeOffset() : default;
        set => UpdatedAtUtc = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public DateTimeOffset CreatedAt
    {
        get => CreatedAtUtc != null ? CreatedAtUtc.ToDateTimeOffset() : default;
        set => CreatedAtUtc = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public DateTimeOffset? RunAt
    {
        get => RunAtUtc != null ? RunAtUtc.ToDateTimeOffset() : null;
        set => RunAtUtc = value.HasValue ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime()) : null;
    }

    public DateTimeOffset? RetiredAt
    {
        get => RetiredAtUtc != null ? RetiredAtUtc.ToDateTimeOffset() : null;
        set => RetiredAtUtc = value.HasValue ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime()) : null;
    }
}
