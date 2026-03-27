using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Feeds;
using Aevatar.GroupChat.Abstractions.Commands;
using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GroupChat.Tests.Application;

internal sealed class RecordingActorDispatchPort : IActorDispatchPort
{
    public List<(string actorId, EventEnvelope envelope)> Calls { get; } = [];

    public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        Calls.Add((actorId, envelope));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingActorRuntime : IActorRuntime
{
    public HashSet<string> ExistingActorIds { get; } = [];

    public List<(Type agentType, string? actorId)> CreateCalls { get; } = [];

    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
        CreateAsync(typeof(TAgent), id, ct);

    public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
    {
        CreateCalls.Add((agentType, id));
        if (id != null)
            ExistingActorIds.Add(id);

        return Task.FromResult<IActor>(new StubActor(id ?? "generated-actor"));
    }

    public Task DestroyAsync(string id, CancellationToken ct = default)
    {
        ExistingActorIds.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IActor?> GetAsync(string id)
    {
        return Task.FromResult<IActor?>(ExistingActorIds.Contains(id) ? new StubActor(id) : null);
    }

    public Task<bool> ExistsAsync(string id) => Task.FromResult(ExistingActorIds.Contains(id));

    public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

    public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

    private sealed class StubActor : IActor
    {
        public StubActor(string id)
        {
            Id = id;
            Agent = new StubAgent(id);
        }

        public string Id { get; }

        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent : IAgent
    {
        public StubAgent(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}

internal sealed class RecordingGroupTimelineProjectionPort : IGroupTimelineProjectionPort
{
    public List<string> EnsuredActorIds { get; } = [];

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        EnsuredActorIds.Add(actorId);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingSourceCatalogProjectionPort : ISourceCatalogProjectionPort
{
    public List<string> EnsuredActorIds { get; } = [];

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
    {
        EnsuredActorIds.Add(actorId);
        return Task.CompletedTask;
    }
}

internal sealed class StubGroupTimelineQueryPort : IGroupTimelineQueryPort, IGroupThreadQueryPort
{
    public GroupThreadSnapshot? Thread { get; set; }

    public IReadOnlyList<GroupTimelineMessageSnapshot> MentionedMessages { get; set; } = [];

    public Task<GroupThreadSnapshot?> GetThreadAsync(string groupId, string threadId, CancellationToken ct = default) =>
        Task.FromResult(Thread);

    public Task<IReadOnlyList<GroupTimelineMessageSnapshot>> GetMentionedMessagesAsync(
        string groupId,
        string threadId,
        string participantAgentId,
        long sinceCursor = 0,
        int take = 50,
        CancellationToken ct = default) =>
        Task.FromResult(MentionedMessages);
}

internal sealed class RecordingMentionHintHandler : IGroupMentionHintHandler
{
    public List<GroupMentionHint> Hints { get; } = [];

    public Task HandleAsync(GroupMentionHint hint, CancellationToken ct = default)
    {
        Hints.Add(hint);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingMentionHintPublisher : IGroupMentionHintPublisher
{
    public List<GroupMentionHint> Hints { get; } = [];

    public Task PublishAsync(GroupMentionHint hint, CancellationToken ct = default)
    {
        Hints.Add(hint);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingAgentFeedCommandPort : IAgentFeedCommandPort
{
    public List<AcceptSignalToFeedCommand> AcceptCalls { get; } = [];

    public List<AdvanceFeedCursorCommand> AdvanceCalls { get; } = [];

    public Task<GroupCommandAcceptedReceipt> AcceptSignalAsync(AcceptSignalToFeedCommand command, CancellationToken ct = default)
    {
        AcceptCalls.Add(command);
        return Task.FromResult(new GroupCommandAcceptedReceipt("feed-actor", "cmd", "corr"));
    }

    public Task<GroupCommandAcceptedReceipt> AdvanceCursorAsync(AdvanceFeedCursorCommand command, CancellationToken ct = default)
    {
        AdvanceCalls.Add(command);
        return Task.FromResult(new GroupCommandAcceptedReceipt("feed-actor", "cmd", "corr"));
    }
}

internal class RecordingParticipantReplyRunCommandPort : IParticipantReplyRunCommandPort
{
    public List<StartParticipantReplyRunCommand> StartCalls { get; } = [];

    public List<CompleteParticipantReplyRunCommand> CompleteCalls { get; } = [];

    public virtual Task<GroupCommandAcceptedReceipt> StartAsync(StartParticipantReplyRunCommand command, CancellationToken ct = default)
    {
        StartCalls.Add(command);
        return Task.FromResult(new GroupCommandAcceptedReceipt("reply-run-actor", "cmd", "corr"));
    }

    public virtual Task<GroupCommandAcceptedReceipt> CompleteAsync(CompleteParticipantReplyRunCommand command, CancellationToken ct = default)
    {
        CompleteCalls.Add(command);
        return Task.FromResult(new GroupCommandAcceptedReceipt("reply-run-actor", "cmd", "corr"));
    }
}

internal sealed class StubAgentFeedInterestEvaluator : IAgentFeedInterestEvaluator
{
    public AgentFeedInterestDecision? Decision { get; set; }

    public Func<GroupMentionHint, AgentFeedInterestDecision?>? EvaluateFunc { get; set; }

    public Task<AgentFeedInterestDecision?> EvaluateAsync(GroupMentionHint hint, CancellationToken ct = default) =>
        Task.FromResult(EvaluateFunc?.Invoke(hint) ?? Decision);
}

internal sealed class StubSourceRegistryQueryPort : ISourceRegistryQueryPort
{
    public Dictionary<string, GroupSourceCatalogSnapshot> Sources { get; } = new(StringComparer.Ordinal);

    public Task<GroupSourceCatalogSnapshot?> GetSourceAsync(string sourceId, CancellationToken ct = default) =>
        Task.FromResult(Sources.TryGetValue(sourceId, out var snapshot) ? snapshot : null);
}

internal class RecordingGroupThreadCommandPort : IGroupThreadCommandPort
{
    public List<CreateGroupThreadCommand> CreateCalls { get; } = [];

    public List<PostUserMessageCommand> PostCalls { get; } = [];

    public List<AppendAgentMessageCommand> AppendCalls { get; } = [];

    public virtual Task<GroupCommandAcceptedReceipt> CreateThreadAsync(CreateGroupThreadCommand command, CancellationToken ct = default)
    {
        CreateCalls.Add(command);
        return Task.FromResult(new GroupCommandAcceptedReceipt("actor", "cmd", "corr"));
    }

    public virtual Task<GroupCommandAcceptedReceipt> PostUserMessageAsync(PostUserMessageCommand command, CancellationToken ct = default)
    {
        PostCalls.Add(command);
        return Task.FromResult(new GroupCommandAcceptedReceipt("actor", "cmd", "corr"));
    }

    public virtual Task<GroupCommandAcceptedReceipt> AppendAgentMessageAsync(AppendAgentMessageCommand command, CancellationToken ct = default)
    {
        AppendCalls.Add(command);
        return Task.FromResult(new GroupCommandAcceptedReceipt("actor", "cmd", "corr"));
    }
}

internal sealed class StubParticipantReplyGenerationPort : IParticipantReplyGenerationPort
{
    public ParticipantReplyGenerationResult? Result { get; set; }

    public List<ParticipantReplyGenerationRequest> Requests { get; } = [];

    public Task<ParticipantReplyGenerationResult?> GenerateReplyAsync(
        ParticipantReplyGenerationRequest request,
        CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}

internal sealed class RecordingParticipantRuntimeDispatchPort : IParticipantRuntimeDispatchPort
{
    public ParticipantRuntimeDispatchResult? Result { get; set; }

    public List<ParticipantRuntimeDispatchRequest> Requests { get; } = [];

    public Task<ParticipantRuntimeDispatchResult?> DispatchAsync(ParticipantRuntimeDispatchRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}

internal class RecordingGroupParticipantReplyProjectionPort : IGroupParticipantReplyProjectionPort
{
    public bool ProjectionEnabled { get; set; } = true;

    public List<(string actorId, string sessionId)> EnsureCalls { get; } = [];
    public List<(string actorId, string sessionId)> ReleaseCalls { get; } = [];

    public virtual Task EnsureParticipantReplyProjectionAsync(
        string rootActorId,
        string sessionId,
        CancellationToken ct = default)
    {
        EnsureCalls.Add((rootActorId, sessionId));
        return Task.CompletedTask;
    }

    public virtual Task ReleaseParticipantReplyProjectionAsync(
        string rootActorId,
        string sessionId,
        CancellationToken ct = default)
    {
        ReleaseCalls.Add((rootActorId, sessionId));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingGroupParticipantReplyCompletedPublisher : IGroupParticipantReplyCompletedPublisher
{
    public List<GroupParticipantReplyCompletedEvent> Events { get; } = [];

    public Task PublishAsync(GroupParticipantReplyCompletedEvent evt, CancellationToken ct = default)
    {
        Events.Add(evt);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingServiceInvocationPort : IServiceInvocationPort
{
    public List<ServiceInvocationRequest> Requests { get; } = [];

    public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
        ServiceInvocationRequest request,
        CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(new ServiceInvocationAcceptedReceipt
        {
            RequestId = "request-1",
            ServiceKey = "service-key",
            DeploymentId = "deployment-1",
            TargetActorId = "target-actor-1",
            EndpointId = request.EndpointId,
            CommandId = request.CommandId,
            CorrelationId = request.CorrelationId,
        });
    }
}

internal sealed class RecordingWorkflowChatRunDispatchService
    : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
{
    public CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> Result { get; set; } =
        CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
            new WorkflowChatRunAcceptedReceipt("workflow-run-1", "demo", "cmd-1", "corr-1"));

    public List<WorkflowChatRunRequest> Requests { get; } = [];

    public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
        WorkflowChatRunRequest command,
        CancellationToken ct = default)
    {
        Requests.Add(command);
        return Task.FromResult(Result);
    }
}
