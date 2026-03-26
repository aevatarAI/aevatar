using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GroupChat.Application.Participants;

public sealed class GAgentServiceParticipantRuntimeDispatchPort : IParticipantRuntimeDispatchPort
{
    private readonly IServiceInvocationPort _serviceInvocationPort;

    public GAgentServiceParticipantRuntimeDispatchPort(IServiceInvocationPort serviceInvocationPort)
    {
        _serviceInvocationPort = serviceInvocationPort ?? throw new ArgumentNullException(nameof(serviceInvocationPort));
    }

    public async Task<ParticipantRuntimeDispatchResult?> DispatchAsync(ParticipantRuntimeDispatchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sessionId = GroupParticipantRuntimeSessionId.Build(
            request.GroupId,
            request.ThreadId,
            request.TriggerMessage.TopicId,
            request.ParticipantAgentId,
            request.SourceEventId,
            request.TriggerMessage.MessageId);

        var receipt = await _serviceInvocationPort.InvokeAsync(
            new ServiceInvocationRequest
            {
                Identity = new ServiceIdentity
                {
                    TenantId = request.Binding.TenantId,
                    AppId = request.Binding.AppId,
                    Namespace = request.Binding.Namespace,
                    ServiceId = request.Binding.ServiceId,
                },
                EndpointId = request.Binding.EndpointId,
                Payload = Any.Pack(BuildChatRequest(request, sessionId)),
                CommandId = $"group-chat-dispatch:{request.ParticipantAgentId}:{request.SourceEventId}",
                CorrelationId = $"{request.GroupId}:{request.ThreadId}:{request.TriggerMessage.MessageId}:{request.ParticipantAgentId}",
            },
            ct);
        return new ParticipantRuntimeDispatchResult(receipt.TargetActorId, sessionId);
    }

    private static ChatRequestEvent BuildChatRequest(ParticipantRuntimeDispatchRequest request, string sessionId)
    {
        var chatRequest = new ChatRequestEvent
        {
            Prompt = request.TriggerMessage.Text,
            SessionId = sessionId,
            ScopeId = request.Binding.ScopeId,
        };
        chatRequest.Metadata.Add("group_id", request.GroupId);
        chatRequest.Metadata.Add("thread_id", request.ThreadId);
        chatRequest.Metadata.Add("topic_id", request.TriggerMessage.TopicId);
        chatRequest.Metadata.Add("message_id", request.TriggerMessage.MessageId);
        chatRequest.Metadata.Add("participant_agent_id", request.ParticipantAgentId);
        chatRequest.Metadata.Add("source_event_id", request.SourceEventId);
        chatRequest.Metadata.Add("timeline_cursor", request.TimelineCursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        chatRequest.Metadata.Add("state_version", request.SourceStateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return chatRequest;
    }
}
