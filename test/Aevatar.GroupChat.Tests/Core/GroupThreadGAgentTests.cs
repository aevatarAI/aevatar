using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Core.GAgents;
using Aevatar.GroupChat.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Core;

public sealed class GroupThreadGAgentTests
{
    [Fact]
    public async Task HandleCreateAndAppendMessagesAsync_ShouldPersistAndReplayThreadState()
    {
        var eventStore = new InMemoryEventStore();
        var actorId = GroupChatActorIds.Thread("group-a", "general");
        var agent = GroupChatTestKit.CreateStatefulAgent<GroupThreadGAgent, GroupThreadState>(
            eventStore,
            actorId,
            static () => new GroupThreadGAgent());
        await agent.ActivateAsync();

        await agent.HandleCreateAsync(GroupChatTestKit.CreateThreadCommand());
        await agent.HandlePostUserMessageAsync(GroupChatTestKit.CreateUserMessageCommand(directHintAgentIds: ["agent-alpha"]));
        await agent.HandleAppendAgentMessageAsync(GroupChatTestKit.CreateAgentMessageCommand());

        agent.State.DisplayName.Should().Be("General");
        agent.State.ParticipantAgentIds.Should().Equal("agent-alpha", "agent-beta");
        agent.State.MessageEntries.Should().HaveCount(2);
        agent.State.MessageEntries[0].TimelineCursor.Should().Be(1);
        agent.State.MessageEntries[1].TimelineCursor.Should().Be(2);
        agent.State.LastAppliedEventVersion.Should().Be(3);

        await agent.DeactivateAsync();

        var replayed = GroupChatTestKit.CreateStatefulAgent<GroupThreadGAgent, GroupThreadState>(
            eventStore,
            actorId,
            static () => new GroupThreadGAgent());
        await replayed.ActivateAsync();

        replayed.State.GroupId.Should().Be("group-a");
        replayed.State.ThreadId.Should().Be("general");
        replayed.State.MessageEntries.Should().HaveCount(2);
        replayed.State.MessageEntries[0].DirectHintAgentIds.Should().ContainSingle().Which.Should().Be("agent-alpha");
        replayed.State.MessageEntries[0].TopicId.Should().Be("general");
        replayed.State.MessageEntries[1].ReplyToMessageId.Should().Be("msg-user-1");
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldRejectDuplicateParticipants()
    {
        var agent = GroupChatTestKit.CreateStatefulAgent<GroupThreadGAgent, GroupThreadState>(
            new InMemoryEventStore(),
            GroupChatActorIds.Thread("group-a", "general"),
            static () => new GroupThreadGAgent());

        var act = () => agent.HandleCreateAsync(GroupChatTestKit.CreateThreadCommand(participantAgentIds: ["agent-alpha", "agent-alpha"]));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*duplicate*");
    }

    [Fact]
    public async Task HandlePostUserMessageAsync_ShouldRejectUnknownMention()
    {
        var agent = GroupChatTestKit.CreateStatefulAgent<GroupThreadGAgent, GroupThreadState>(
            new InMemoryEventStore(),
            GroupChatActorIds.Thread("group-a", "general"),
            static () => new GroupThreadGAgent());
        await agent.HandleCreateAsync(GroupChatTestKit.CreateThreadCommand());

        var act = () => agent.HandlePostUserMessageAsync(
            GroupChatTestKit.CreateUserMessageCommand(directHintAgentIds: ["agent-missing"]));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not registered*");
    }

    [Fact]
    public async Task HandleAppendAgentMessageAsync_ShouldRejectUnknownReplyTarget()
    {
        var agent = GroupChatTestKit.CreateStatefulAgent<GroupThreadGAgent, GroupThreadState>(
            new InMemoryEventStore(),
            GroupChatActorIds.Thread("group-a", "general"),
            static () => new GroupThreadGAgent());
        await agent.HandleCreateAsync(GroupChatTestKit.CreateThreadCommand());

        var act = () => agent.HandleAppendAgentMessageAsync(
            GroupChatTestKit.CreateAgentMessageCommand(replyToMessageId: "missing-message"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not exist*");
    }

    [Fact]
    public async Task HandleAppendAgentMessageAsync_ShouldRejectDuplicateMessageId()
    {
        var agent = GroupChatTestKit.CreateStatefulAgent<GroupThreadGAgent, GroupThreadState>(
            new InMemoryEventStore(),
            GroupChatActorIds.Thread("group-a", "general"),
            static () => new GroupThreadGAgent());
        await agent.HandleCreateAsync(GroupChatTestKit.CreateThreadCommand());
        await agent.HandlePostUserMessageAsync(GroupChatTestKit.CreateUserMessageCommand());

        var act = () => agent.HandleAppendAgentMessageAsync(
            GroupChatTestKit.CreateAgentMessageCommand(messageId: "msg-user-1"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task HandlePostUserMessageAsync_ShouldPreserveSourceIdsInState()
    {
        var agent = GroupChatTestKit.CreateStatefulAgent<GroupThreadGAgent, GroupThreadState>(
            new InMemoryEventStore(),
            GroupChatActorIds.Thread("group-a", "general"),
            static () => new GroupThreadGAgent());
        await agent.HandleCreateAsync(GroupChatTestKit.CreateThreadCommand());

        var command = GroupChatTestKit.CreateUserMessageCommand(directHintAgentIds: ["agent-alpha"]);
        command.SourceRefs.Add(new GroupSourceRef
        {
            SourceKind = GroupSourceKind.Document,
            Locator = "doc://architecture/spec-1",
            SourceId = "doc-1",
        });
        command.EvidenceRefs.Add(new GroupEvidenceRef
        {
            EvidenceId = "ev-1",
            SourceLocator = "doc://architecture/spec-1",
            Locator = "section://1",
            ExcerptSummary = "architecture excerpt",
            SourceId = "doc-1",
        });

        await agent.HandlePostUserMessageAsync(command);

        agent.State.MessageEntries.Should().ContainSingle();
        agent.State.MessageEntries[0].SourceRefs.Should().ContainSingle();
        agent.State.MessageEntries[0].SourceRefs[0].SourceId.Should().Be("doc-1");
        agent.State.MessageEntries[0].EvidenceRefs.Should().ContainSingle();
        agent.State.MessageEntries[0].EvidenceRefs[0].SourceId.Should().Be("doc-1");
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldPreserveServiceRuntimeBindingAcrossReplay()
    {
        var eventStore = new InMemoryEventStore();
        var actorId = GroupChatActorIds.Thread("group-a", "service-thread");
        var agent = GroupChatTestKit.CreateStatefulAgent<GroupThreadGAgent, GroupThreadState>(
            eventStore,
            actorId,
            static () => new GroupThreadGAgent());
        await agent.ActivateAsync();

        var command = GroupChatTestKit.CreateThreadCommand(
            groupId: "group-a",
            threadId: "service-thread",
            displayName: "Service Thread",
            "agent-alpha");
        command.ParticipantRuntimeBindingEntries.Add(new GroupParticipantRuntimeBinding
        {
            ParticipantAgentId = "agent-alpha",
            TargetKind = GroupParticipantRuntimeTargetKind.Service,
            ServiceTarget = new GroupServiceRuntimeTarget
            {
                TenantId = "tenant-1",
                AppId = "app-1",
                Namespace = "ns-1",
                ServiceId = "svc-1",
                EndpointId = "ep-1",
                ScopeId = "scope-1",
            },
        });

        await agent.HandleCreateAsync(command);
        await agent.DeactivateAsync();

        var replayed = GroupChatTestKit.CreateStatefulAgent<GroupThreadGAgent, GroupThreadState>(
            eventStore,
            actorId,
            static () => new GroupThreadGAgent());
        await replayed.ActivateAsync();

        replayed.State.ParticipantRuntimeBindingEntries.Should().ContainSingle();
        var binding = replayed.State.ParticipantRuntimeBindingEntries[0];
        binding.ParticipantAgentId.Should().Be("agent-alpha");
        binding.TargetKind.Should().Be(GroupParticipantRuntimeTargetKind.Service);
        binding.TargetCase.Should().Be(GroupParticipantRuntimeBinding.TargetOneofCase.ServiceTarget);
        binding.ServiceTarget.Should().NotBeNull();
        binding.ServiceTarget!.ServiceId.Should().Be("svc-1");
        binding.WorkflowTarget.Should().BeNull();
        binding.ScriptTarget.Should().BeNull();
        binding.LocalTarget.Should().BeNull();
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldAllowWorkflowRuntimeBindingWithoutDefinitionActorId()
    {
        var eventStore = new InMemoryEventStore();
        var actorId = GroupChatActorIds.Thread("group-a", "workflow-thread");
        var agent = GroupChatTestKit.CreateStatefulAgent<GroupThreadGAgent, GroupThreadState>(
            eventStore,
            actorId,
            static () => new GroupThreadGAgent());
        await agent.ActivateAsync();

        var command = GroupChatTestKit.CreateThreadCommand(
            groupId: "group-a",
            threadId: "workflow-thread",
            displayName: "Workflow Thread",
            "agent-alpha");
        command.ParticipantRuntimeBindingEntries.Add(new GroupParticipantRuntimeBinding
        {
            ParticipantAgentId = "agent-alpha",
            TargetKind = GroupParticipantRuntimeTargetKind.Workflow,
            WorkflowTarget = new GroupWorkflowRuntimeTarget
            {
                WorkflowName = "simple_qa",
                ScopeId = "scope-1",
            },
        });

        await agent.HandleCreateAsync(command);
        await agent.DeactivateAsync();

        var replayed = GroupChatTestKit.CreateStatefulAgent<GroupThreadGAgent, GroupThreadState>(
            eventStore,
            actorId,
            static () => new GroupThreadGAgent());
        await replayed.ActivateAsync();

        replayed.State.ParticipantRuntimeBindingEntries.Should().ContainSingle();
        var binding = replayed.State.ParticipantRuntimeBindingEntries[0];
        binding.TargetKind.Should().Be(GroupParticipantRuntimeTargetKind.Workflow);
        binding.TargetCase.Should().Be(GroupParticipantRuntimeBinding.TargetOneofCase.WorkflowTarget);
        binding.WorkflowTarget.Should().NotBeNull();
        binding.WorkflowTarget!.DefinitionActorId.Should().BeEmpty();
        binding.WorkflowTarget.WorkflowName.Should().Be("simple_qa");
        binding.WorkflowTarget.ScopeId.Should().Be("scope-1");
    }
}
