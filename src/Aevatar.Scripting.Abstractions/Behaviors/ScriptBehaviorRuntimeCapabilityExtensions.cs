using Aevatar.Foundation.Abstractions;
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
