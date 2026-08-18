using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Projection.Runtime;

public sealed partial class RuntimeFleetCapabilityAuthorityCurrentStateDocument
    : IProjectionReadModel<RuntimeFleetCapabilityAuthorityCurrentStateDocument>
{
    public string ActorId => AuthorityActorId;

    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch;
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value);
    }
}
