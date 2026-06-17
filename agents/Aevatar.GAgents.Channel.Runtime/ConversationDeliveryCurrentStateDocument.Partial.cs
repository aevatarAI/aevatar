using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed partial class ConversationDeliveryCurrentStateDocument
    : IProjectionReadModel<ConversationDeliveryCurrentStateDocument>
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
}
