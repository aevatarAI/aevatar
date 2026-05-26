using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed partial class TestProjectorDocument : IProjectionReadModel<TestProjectorDocument>
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtc == null ? default : UpdatedAtUtc.ToDateTimeOffset();
        set => UpdatedAtUtc = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
