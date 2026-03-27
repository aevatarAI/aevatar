using Aevatar.AI.Abstractions;
using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Queries;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Application.Participants;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Application;

public sealed class GAgentServiceParticipantRuntimeDispatchPortTests
{
    [Fact]
    public async Task DispatchAsync_ShouldInvokeConfiguredServiceIdentityAndChatPayload()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var dispatchPort = new GAgentServiceParticipantRuntimeDispatchPort(invocationPort);

        var result = await dispatchPort.DispatchAsync(
            new ParticipantRuntimeDispatchRequest(
                "group-a",
                "general",
                "agent-alpha",
                "evt-user-1",
                2,
                1,
                new GroupTimelineMessageSnapshot(
                    "msg-user-1",
                    1,
                    Aevatar.GroupChat.Abstractions.GroupMessageSenderKind.User,
                    "user-1",
                    "hello @agent-alpha",
                    string.Empty,
                    ["agent-alpha"],
                    "topic-general"),
                new GroupThreadSnapshot(
                    "group-chat:thread:group-a:general",
                    "group-a",
                    "general",
                    "General",
                    ["agent-alpha"],
                    [
                        new GroupParticipantRuntimeBindingSnapshot(
                            "agent-alpha",
                            Aevatar.GroupChat.Abstractions.GroupParticipantRuntimeTargetKind.Service,
                            new GroupServiceRuntimeTargetSnapshot(
                                "tenant-a",
                                "app-a",
                                "demo",
                                "agent-alpha-service",
                                "chat",
                                "scope-1")),
                    ],
                    [],
                    2,
                    "evt-user-1",
                    DateTimeOffset.Parse("2026-03-25T00:00:00+00:00")),
                new GroupParticipantRuntimeBindingSnapshot(
                    "agent-alpha",
                    Aevatar.GroupChat.Abstractions.GroupParticipantRuntimeTargetKind.Service,
                    new GroupServiceRuntimeTargetSnapshot(
                        "tenant-a",
                        "app-a",
                        "demo",
                        "agent-alpha-service",
                        "chat",
                        "scope-1"))),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.BackendKind.Should().Be(ParticipantRuntimeBackendKind.Service);
        result.CompletionMode.Should().Be(ParticipantRuntimeCompletionMode.AsyncObserved);
        result!.RootActorId.Should().Be("target-actor-1");
        result.ReplyMessageId.Should().Be("participant-reply:agent-alpha:evt-user-1");
        GroupParticipantRuntimeSessionId.TryParse(result.SessionId, out var session).Should().BeTrue();
        session.GroupId.Should().Be("group-a");
        session.ThreadId.Should().Be("general");
        session.TopicId.Should().Be("topic-general");
        session.ParticipantAgentId.Should().Be("agent-alpha");
        session.SourceEventId.Should().Be("evt-user-1");
        session.ReplyToMessageId.Should().Be("msg-user-1");

        invocationPort.Requests.Should().ContainSingle();
        var request = invocationPort.Requests[0];
        request.Identity.TenantId.Should().Be("tenant-a");
        request.Identity.AppId.Should().Be("app-a");
        request.Identity.Namespace.Should().Be("demo");
        request.Identity.ServiceId.Should().Be("agent-alpha-service");
        request.EndpointId.Should().Be("chat");
        request.CommandId.Should().Be("group-chat-dispatch:agent-alpha:evt-user-1");
        request.CorrelationId.Should().Be("group-a:general:msg-user-1:agent-alpha");

        var payload = request.Payload.Unpack<ChatRequestEvent>();
        payload.Prompt.Should().Be("hello @agent-alpha");
        payload.SessionId.Should().Be(result.SessionId);
        payload.ScopeId.Should().Be("scope-1");
        payload.Metadata["group_id"].Should().Be("group-a");
        payload.Metadata["thread_id"].Should().Be("general");
        payload.Metadata["topic_id"].Should().Be("topic-general");
        payload.Metadata["message_id"].Should().Be("msg-user-1");
        payload.Metadata["participant_agent_id"].Should().Be("agent-alpha");
        payload.Metadata["source_event_id"].Should().Be("evt-user-1");
        payload.Metadata["timeline_cursor"].Should().Be("1");
        payload.Metadata["state_version"].Should().Be("2");
    }
}
