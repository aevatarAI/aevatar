using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Queries;
using Aevatar.GroupChat.Application.Participants;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Application;

public sealed class WorkflowParticipantRuntimeDispatchPortTests
{
    [Fact]
    public async Task DispatchAsync_ShouldStartWorkflowRunWithGroupChatSessionAnnotations()
    {
        var dispatchService = new RecordingWorkflowChatRunDispatchService();
        var dispatchPort = new WorkflowParticipantRuntimeDispatchPort(dispatchService);

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
                    GroupMessageSenderKind.User,
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
                            GroupParticipantRuntimeTargetKind.Workflow,
                            WorkflowTarget: new GroupWorkflowRuntimeTargetSnapshot(
                                "workflow-definition-1",
                                "group_chat_reply",
                                "scope-1")),
                    ],
                    [],
                    2,
                    "evt-user-1",
                    DateTimeOffset.Parse("2026-03-25T00:00:00+00:00")),
                new GroupParticipantRuntimeBindingSnapshot(
                    "agent-alpha",
                    GroupParticipantRuntimeTargetKind.Workflow,
                    WorkflowTarget: new GroupWorkflowRuntimeTargetSnapshot(
                        "workflow-definition-1",
                        "group_chat_reply",
                        "scope-1"))),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.BackendKind.Should().Be(ParticipantRuntimeBackendKind.Workflow);
        result.CompletionMode.Should().Be(ParticipantRuntimeCompletionMode.AsyncObserved);
        result.RootActorId.Should().Be("workflow-run-1");
        result.ReplyMessageId.Should().Be("participant-reply:agent-alpha:evt-user-1");

        dispatchService.Requests.Should().ContainSingle();
        var request = dispatchService.Requests[0];
        request.Prompt.Should().Be("hello @agent-alpha");
        request.ActorId.Should().Be("workflow-definition-1");
        request.WorkflowName.Should().Be("group_chat_reply");
        request.ScopeId.Should().Be("scope-1");
        request.SessionId.Should().Be(result.SessionId);
        request.Metadata.Should().NotBeNull();
        request.Metadata!["group_id"].Should().Be("group-a");
        request.Metadata["thread_id"].Should().Be("general");
        request.Metadata["topic_id"].Should().Be("topic-general");
        request.Metadata["message_id"].Should().Be("msg-user-1");
        request.Metadata["participant_agent_id"].Should().Be("agent-alpha");
        request.Metadata["source_event_id"].Should().Be("evt-user-1");
        request.Metadata["timeline_cursor"].Should().Be("1");
        request.Metadata["state_version"].Should().Be("2");
    }

    [Fact]
    public async Task DispatchAsync_ShouldAllowWorkflowNameWithoutDefinitionActorId()
    {
        var dispatchService = new RecordingWorkflowChatRunDispatchService();
        var dispatchPort = new WorkflowParticipantRuntimeDispatchPort(dispatchService);

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
                    GroupMessageSenderKind.User,
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
                            GroupParticipantRuntimeTargetKind.Workflow,
                            WorkflowTarget: new GroupWorkflowRuntimeTargetSnapshot(
                                string.Empty,
                                "simple_qa",
                                "scope-1")),
                    ],
                    [],
                    2,
                    "evt-user-1",
                    DateTimeOffset.Parse("2026-03-25T00:00:00+00:00")),
                new GroupParticipantRuntimeBindingSnapshot(
                    "agent-alpha",
                    GroupParticipantRuntimeTargetKind.Workflow,
                    WorkflowTarget: new GroupWorkflowRuntimeTargetSnapshot(
                        string.Empty,
                        "simple_qa",
                        "scope-1"))),
            CancellationToken.None);

        result.Should().NotBeNull();
        dispatchService.Requests.Should().ContainSingle();
        var request = dispatchService.Requests[0];
        request.ActorId.Should().BeNull();
        request.WorkflowName.Should().Be("simple_qa");
        request.ScopeId.Should().Be("scope-1");
    }
}
