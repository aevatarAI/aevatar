using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Serialization;
using Google.Protobuf;

namespace Aevatar.Scripting.Projection.Projectors;

internal static class ScriptCommittedStateProjectionSupport
{
    public static ScriptFactContext CreateFactContext(
        string actorId,
        ScriptBehaviorState state,
        ScriptDomainFactCommitted fact,
        string eventTypeUrl)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fact);

        return new ScriptFactContext(
            fact.ActorId ?? actorId,
            fact.DefinitionActorId ?? state.DefinitionActorId ?? string.Empty,
            string.IsNullOrWhiteSpace(fact.ScriptId) ? state.ScriptId ?? string.Empty : fact.ScriptId,
            string.IsNullOrWhiteSpace(fact.Revision) ? state.Revision ?? string.Empty : fact.Revision,
            fact.RunId ?? string.Empty,
            fact.CommandId ?? string.Empty,
            fact.CorrelationId ?? string.Empty,
            fact.EventSequence,
            fact.StateVersion,
            fact.EventType ?? eventTypeUrl,
            fact.OccurredAtUnixTimeMs);
    }

    public static async ValueTask<IMessage?> BuildSemanticReadModelAsync(
        string actorId,
        ScriptBehaviorState state,
        ScriptDomainFactCommitted fact,
        ScriptBehaviorArtifact artifact,
        IProtobufMessageCodec codec)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(codec);

        var payload = fact.DomainEventPayload?.Clone()
            ?? throw new InvalidOperationException("Committed script fact payload cannot be null.");
        var eventTypeUrl = payload.TypeUrl ?? string.Empty;
        if (!artifact.Descriptor.DomainEvents.TryGetValue(eventTypeUrl, out var domainEventRegistration))
        {
            throw new InvalidOperationException(
                $"Script behavior actor `{actorId}` cannot project undeclared domain event type `{eventTypeUrl}`.");
        }

        var currentState = codec.Unpack(state.StateRoot, artifact.Descriptor.StateClrType);
        var domainEvent = codec.Unpack(payload, domainEventRegistration.MessageClrType)
            ?? throw new InvalidOperationException($"Failed to unpack domain event payload `{eventTypeUrl}`.");
        var factContext = CreateFactContext(actorId, state, fact, eventTypeUrl);

        var behavior = artifact.CreateBehavior();
        try
        {
            return behavior.ProjectReadModel(currentState, domainEvent, factContext);
        }
        finally
        {
            if (behavior is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (behavior is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
