using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Identity;

public sealed partial class AevatarOAuthClientDocument
    : IProjectionReadModel<AevatarOAuthClientDocument>
{
    public string ActorId => Id;

    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue == null ? default : UpdatedAtUtcValue.ToDateTimeOffset();
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public bool IsProvisioned => !string.IsNullOrEmpty(ClientId);
}
