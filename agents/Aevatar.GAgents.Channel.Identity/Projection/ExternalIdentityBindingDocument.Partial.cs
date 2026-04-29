using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Identity;

public sealed partial class ExternalIdentityBindingDocument
    : IProjectionReadModel<ExternalIdentityBindingDocument>
{
    public string ActorId => Id;

    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue == null ? default : UpdatedAtUtcValue.ToDateTimeOffset();
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public DateTimeOffset? BoundAt
    {
        get => BoundAtUtcValue?.ToDateTimeOffset();
        set => BoundAtUtcValue = value is null
            ? null
            : Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime());
    }

    public DateTimeOffset? RevokedAt
    {
        get => RevokedAtUtcValue?.ToDateTimeOffset();
        set => RevokedAtUtcValue = value is null
            ? null
            : Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime());
    }

    public bool IsActive => !string.IsNullOrEmpty(BindingId) && RevokedAtUtcValue is null;
}
