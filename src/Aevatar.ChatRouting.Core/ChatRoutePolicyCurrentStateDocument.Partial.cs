using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.ChatRouting.Core;

/// <summary>
/// Hand-written half of the proto-generated <see cref="ChatRoutePolicyCurrentStateDocument"/>:
/// adapts the readmodel envelope fields to the <see cref="IProjectionReadModel{T}"/>
/// contract that the projection document stores require.
/// </summary>
public sealed partial class ChatRoutePolicyCurrentStateDocument
    : IProjectionReadModel<ChatRoutePolicyCurrentStateDocument>
{
    string IProjectionReadModel.ActorId => ActorId;

    long IProjectionReadModel.StateVersion => StateVersion;

    string IProjectionReadModel.LastEventId => LastEventId;

    DateTimeOffset IProjectionReadModel.UpdatedAt
        => UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;
}
