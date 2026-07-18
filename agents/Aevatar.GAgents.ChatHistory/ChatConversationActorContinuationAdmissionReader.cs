using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChatHistory;

public sealed class ChatConversationActorContinuationAdmissionReader
    : IChatConversationContinuationAdmissionReader
{
    private const string PublisherActorId = "chat-conversation-continuation-admission";
    private readonly IActorHandledDispatchPort _handledDispatchPort;
    private readonly TimeProvider _timeProvider;

    public ChatConversationActorContinuationAdmissionReader(
        IActorHandledDispatchPort handledDispatchPort,
        TimeProvider? timeProvider = null)
    {
        _handledDispatchPort = handledDispatchPort
            ?? throw new ArgumentNullException(nameof(handledDispatchPort));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> CanContinueAsync(
        string scopeId,
        string conversationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || string.IsNullOrWhiteSpace(conversationId))
            return false;

        var normalizedScopeId = scopeId.Trim();
        var normalizedConversationId = conversationId.Trim();
        var actorId = ChatHistoryActorIds.Conversation(normalizedScopeId, normalizedConversationId);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(new ChatConversationContinuationAdmissionRequested
            {
                ScopeId = normalizedScopeId,
                ConversationId = normalizedConversationId,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = normalizedConversationId,
            },
        };
        envelope.EnsureRuntime().EnsureDispatch().PropagateFailure = true;
        envelope.EnsureRuntime().EnsureDeduplication().OperationId =
            $"chat-conversation-continuation-admission:{actorId}:{normalizedConversationId}";

        try
        {
            var admission = await _handledDispatchPort.DispatchHandledAsync(actorId, envelope, ct).ConfigureAwait(false);
            return admission.Accepted;
        }
        catch (ActorNotFoundException)
        {
            return false;
        }
        catch (ChatConversationContinuationAdmissionNotFoundException)
        {
            return false;
        }
    }
}
