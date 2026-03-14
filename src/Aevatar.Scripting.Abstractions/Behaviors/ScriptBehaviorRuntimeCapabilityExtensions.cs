using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Queries;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Abstractions.Behaviors;

public static class ScriptBehaviorRuntimeCapabilityExtensions
{
    public static Task PublishAsync<TEvent>(
        this IScriptBehaviorRuntimeCapabilities capabilities,
        TEvent eventPayload,
        TopologyAudience audience,
        CancellationToken ct)
        where TEvent : class, IMessage<TEvent>, new()
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(eventPayload);
        return capabilities.PublishAsync(eventPayload, audience, ct);
    }

    public static Task SendToAsync<TEvent>(
        this IScriptBehaviorRuntimeCapabilities capabilities,
        string targetActorId,
        TEvent eventPayload,
        CancellationToken ct)
        where TEvent : class, IMessage<TEvent>, new()
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(eventPayload);
        return capabilities.SendToAsync(targetActorId, eventPayload, ct);
    }

    public static Task PublishToSelfAsync<TSignal>(
        this IScriptBehaviorRuntimeCapabilities capabilities,
        TSignal signalPayload,
        CancellationToken ct)
        where TSignal : class, IMessage<TSignal>, new()
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(signalPayload);
        return capabilities.PublishToSelfAsync(signalPayload, ct);
    }

    public static async Task<ScriptReadModelSnapshot<TReadModel>?> GetReadModelSnapshotAsync<TReadModel>(
        this IScriptBehaviorRuntimeCapabilities capabilities,
        string actorId,
        CancellationToken ct)
        where TReadModel : class, IMessage<TReadModel>, new()
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var snapshot = await capabilities.GetReadModelSnapshotAsync(actorId, ct);
        if (snapshot == null)
            return null;

        return new ScriptReadModelSnapshot<TReadModel>(
            snapshot.ActorId,
            snapshot.ScriptId,
            snapshot.DefinitionActorId,
            snapshot.Revision,
            snapshot.ReadModelTypeUrl,
            ScriptMessageTypes.Unpack<TReadModel>(snapshot.ReadModelPayload),
            snapshot.StateVersion,
            snapshot.LastEventId,
            snapshot.UpdatedAt);
    }

    public static async Task<TResult?> ExecuteReadModelQueryAsync<TQuery, TResult>(
        this IScriptBehaviorRuntimeCapabilities capabilities,
        string actorId,
        TQuery queryPayload,
        CancellationToken ct)
        where TQuery : class, IMessage<TQuery>, new()
        where TResult : class, IMessage<TResult>, new()
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(queryPayload);
        var payload = await capabilities.ExecuteReadModelQueryAsync(actorId, Any.Pack(queryPayload), ct);
        return ScriptMessageTypes.Unpack<TResult>(payload);
    }

    public static Task RunScriptInstanceAsync<TCommand>(
        this IScriptBehaviorRuntimeCapabilities capabilities,
        string runtimeActorId,
        string runId,
        TCommand inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct)
        where TCommand : class, IMessage<TCommand>, new()
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(inputPayload);
        return capabilities.RunScriptInstanceAsync(
            runtimeActorId,
            runId,
            Any.Pack(inputPayload),
            scriptRevision,
            definitionActorId,
            requestedEventType,
            ct);
    }
}
