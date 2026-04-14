using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed partial class ChannelBotRegistrationDocument : IProjectionReadModel<ChannelBotRegistrationDocument>
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtc != null ? UpdatedAtUtc.ToDateTimeOffset() : default;
        set => UpdatedAtUtc = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
