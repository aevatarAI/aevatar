using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Identity;

public sealed partial class ManagedCodexCredentialDocument
    : IProjectionReadModel<ManagedCodexCredentialDocument>
{
    public string ActorId => Id;

    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue is null ? default : UpdatedAtUtcValue.ToDateTimeOffset();
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
