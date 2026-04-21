using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class WorkflowAgentGAgent : GAgentBase<WorkflowAgentState>
{
    private ChannelScheduleRunner? _scheduler;

    private ChannelScheduleRunner Scheduler => _scheduler ??= new ChannelScheduleRunner(
        callbackId: WorkflowAgentDefaults.TriggerCallbackId,
        schedulableSource: () => State,
        triggerFactory: () => new TriggerWorkflowAgentExecutionCommand { Reason = "schedule" },
        persistNextRunEventAsync: nextRunUtc => PersistDomainEventAsync(new WorkflowAgentNextRunScheduledEvent
        {
            NextRunAt = Timestamp.FromDateTimeOffset(nextRunUtc),
        }),
        scheduleTimeoutAsync: (id, dueTime, evt, ct) => ScheduleSelfDurableTimeoutAsync(id, dueTime, evt, ct: ct),
        cancelCallbackAsync: (lease, ct) => CancelDurableCallbackAsync(lease, ct),
        logger: Logger,
        ownerDescription: $"Workflow agent {Id}");

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await Scheduler.BootstrapOnActivateAsync(ct);
    }

    protected override WorkflowAgentState TransitionState(WorkflowAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<WorkflowAgentInitializedEvent>(ApplyInitialized)
            .On<WorkflowAgentNextRunScheduledEvent>(ApplyNextRunScheduled)
            .On<WorkflowAgentExecutionDispatchedEvent>(ApplyDispatched)
            .On<WorkflowAgentExecutionFailedEvent>(ApplyFailed)
            .On<WorkflowAgentDisabledEvent>(ApplyDisabled)
            .On<WorkflowAgentEnabledEvent>(ApplyEnabled)
            .OrCurrent();

    [EventHandler]
    public async Task HandleInitializeAsync(InitializeWorkflowAgentCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.WorkflowActorId))
        {
            Logger.LogWarning("Workflow agent {ActorId} initialization ignored because workflow_actor_id is empty", Id);
            return;
        }

        await PersistDomainEventAsync(new WorkflowAgentInitializedEvent
        {
            WorkflowId = command.WorkflowId?.Trim() ?? string.Empty,
            WorkflowName = command.WorkflowName?.Trim() ?? string.Empty,
            WorkflowActorId = command.WorkflowActorId?.Trim() ?? string.Empty,
            ExecutionPrompt = command.ExecutionPrompt?.Trim() ?? string.Empty,
            ScheduleCron = command.ScheduleCron?.Trim() ?? string.Empty,
            ScheduleTimezone = NormalizeTimezone(command.ScheduleTimezone),
            ConversationId = command.ConversationId?.Trim() ?? string.Empty,
            NyxProviderSlug = command.NyxProviderSlug?.Trim() ?? string.Empty,
            NyxApiKey = command.NyxApiKey?.Trim() ?? string.Empty,
            OwnerNyxUserId = command.OwnerNyxUserId?.Trim() ?? string.Empty,
            ApiKeyId = command.ApiKeyId?.Trim() ?? string.Empty,
            Enabled = command.Enabled,
            ScopeId = command.ScopeId?.Trim() ?? string.Empty,
            Platform = command.Platform?.Trim() ?? string.Empty,
        });

        await Scheduler.ScheduleNextRunAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        await UpsertRegistryAsync(State.Enabled ? WorkflowAgentDefaults.StatusRunning : WorkflowAgentDefaults.StatusDisabled, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleTriggerAsync(TriggerWorkflowAgentExecutionCommand command)
    {
        if (!State.Enabled)
        {
            Logger.LogInformation("Workflow agent {ActorId} ignored trigger because it is disabled", Id);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        try
        {
            var receipt = await DispatchWorkflowRunAsync(command.Reason, command.RevisionFeedback, CancellationToken.None);
            await PersistDomainEventAsync(new WorkflowAgentExecutionDispatchedEvent
            {
                DispatchedAt = Timestamp.FromDateTimeOffset(now),
                WorkflowRunActorId = receipt.ActorId,
                CommandId = receipt.CommandId,
            });

            await Scheduler.ScheduleNextRunAsync(now, CancellationToken.None);
            await UpdateRegistryExecutionAsync(
                WorkflowAgentDefaults.StatusRunning, State.LastRunAt, State.NextRunAt,
                0, string.Empty, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Workflow agent {ActorId} execution dispatch failed", Id);
            await PersistDomainEventAsync(new WorkflowAgentExecutionFailedEvent
            {
                FailedAt = Timestamp.FromDateTimeOffset(now),
                Error = ex.Message,
            });

            await Scheduler.ScheduleNextRunAsync(now, CancellationToken.None);
            await UpdateRegistryExecutionAsync(
                WorkflowAgentDefaults.StatusError, State.LastRunAt, State.NextRunAt,
                State.ErrorCount, State.LastError, CancellationToken.None);
        }
    }

    [EventHandler]
    public async Task HandleDisableAsync(DisableWorkflowAgentCommand command)
    {
        await Scheduler.CancelAsync(CancellationToken.None);

        await PersistDomainEventAsync(new WorkflowAgentDisabledEvent
        {
            Reason = command.Reason?.Trim() ?? string.Empty,
        });

        await UpdateRegistryExecutionAsync(
            WorkflowAgentDefaults.StatusDisabled, State.LastRunAt, null,
            State.ErrorCount, State.LastError, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleEnableAsync(EnableWorkflowAgentCommand command)
    {
        if (!State.Enabled)
        {
            await PersistDomainEventAsync(new WorkflowAgentEnabledEvent
            {
                Reason = command.Reason?.Trim() ?? string.Empty,
            });
        }

        await Scheduler.ScheduleNextRunAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        await UpdateRegistryExecutionAsync(
            WorkflowAgentDefaults.StatusRunning, State.LastRunAt, State.NextRunAt,
            State.ErrorCount, State.LastError, CancellationToken.None);
    }

    private async Task<WorkflowChatRunAcceptedReceipt> DispatchWorkflowRunAsync(
        string? reason, string? revisionFeedback, CancellationToken ct)
    {
        var dispatchService = Services.GetService<ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>();
        if (dispatchService is null)
            throw new InvalidOperationException("Workflow run dispatch service is not registered.");

        var request = new WorkflowChatRunRequest(
            Prompt: BuildExecutionPrompt(reason, revisionFeedback),
            WorkflowName: State.WorkflowName,
            ActorId: State.WorkflowActorId,
            SessionId: null,
            InputParts: null,
            WorkflowYamls: null,
            Metadata: BuildExecutionMetadata(),
            ScopeId: State.ScopeId);

        var dispatch = await dispatchService.DispatchAsync(request, ct);
        if (!dispatch.Succeeded || dispatch.Receipt is null)
            throw new InvalidOperationException(MapDispatchError(dispatch.Error));

        return dispatch.Receipt;
    }

    private IReadOnlyDictionary<string, string> BuildExecutionMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = State.NyxApiKey ?? string.Empty,
            [ChannelMetadataKeys.ConversationId] = State.ConversationId ?? string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(State.ScopeId))
            metadata["scope_id"] = State.ScopeId;
        return metadata;
    }

    private string BuildExecutionPrompt(string? reason, string? revisionFeedback)
    {
        var prompt = string.IsNullOrWhiteSpace(State.ExecutionPrompt)
            ? "Run the configured workflow now."
            : State.ExecutionPrompt;

        var lines = new List<string>
        {
            prompt,
            $"Trigger reason: {(string.IsNullOrWhiteSpace(reason) ? "manual" : reason)}",
        };

        var normalized = NormalizeOptional(revisionFeedback);
        if (normalized is not null)
            lines.Add($"Revision feedback: {normalized}");

        return string.Join('\n', lines);
    }

    private async Task UpsertRegistryAsync(string status, CancellationToken ct)
    {
        var runtime = Services.GetService<IActorRuntime>();
        if (runtime is null) return;

        var actor = await runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
                    ?? await runtime.CreateAsync<UserAgentCatalogGAgent>(UserAgentCatalogGAgent.WellKnownId, ct);

        var command = new UserAgentCatalogUpsertCommand
        {
            AgentId = Id,
            Platform = ResolvePlatform(State.Platform),
            ConversationId = State.ConversationId ?? string.Empty,
            NyxProviderSlug = State.NyxProviderSlug ?? string.Empty,
            NyxApiKey = State.NyxApiKey ?? string.Empty,
            OwnerNyxUserId = State.OwnerNyxUserId ?? string.Empty,
            AgentType = WorkflowAgentDefaults.AgentType,
            TemplateName = WorkflowAgentDefaults.TemplateName,
            ScopeId = State.ScopeId ?? string.Empty,
            ApiKeyId = State.ApiKeyId ?? string.Empty,
            ScheduleCron = State.ScheduleCron ?? string.Empty,
            ScheduleTimezone = State.ScheduleTimezone ?? string.Empty,
            Status = status,
        };

        await actor.HandleEventAsync(BuildDirectEnvelope(actor.Id, command), ct);
        await UpdateRegistryExecutionAsync(status, State.LastRunAt, State.NextRunAt, State.ErrorCount, State.LastError, ct);
    }

    private async Task UpdateRegistryExecutionAsync(
        string status, Timestamp? lastRunAt, Timestamp? nextRunAt,
        int errorCount, string? lastError, CancellationToken ct)
    {
        var runtime = Services.GetService<IActorRuntime>();
        if (runtime is null) return;

        var actor = await runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
                    ?? await runtime.CreateAsync<UserAgentCatalogGAgent>(UserAgentCatalogGAgent.WellKnownId, ct);

        var command = new UserAgentCatalogExecutionUpdateCommand
        {
            AgentId = Id, Status = status,
            LastRunAt = lastRunAt, NextRunAt = nextRunAt,
            ErrorCount = errorCount, LastError = lastError ?? string.Empty,
        };
        await actor.HandleEventAsync(BuildDirectEnvelope(actor.Id, command), ct);
    }

    private static EventEnvelope BuildDirectEnvelope(string actorId, IMessage payload) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Payload = Any.Pack(payload),
        Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = actorId } },
    };

    private static WorkflowAgentState ApplyInitialized(WorkflowAgentState current, WorkflowAgentInitializedEvent evt)
    {
        var next = current.Clone();
        next.WorkflowId = evt.WorkflowId ?? string.Empty;
        next.WorkflowName = evt.WorkflowName ?? string.Empty;
        next.WorkflowActorId = evt.WorkflowActorId ?? string.Empty;
        next.ExecutionPrompt = evt.ExecutionPrompt ?? string.Empty;
        next.ScheduleCron = evt.ScheduleCron ?? string.Empty;
        next.ScheduleTimezone = NormalizeTimezone(evt.ScheduleTimezone);
        next.ConversationId = evt.ConversationId ?? string.Empty;
        next.NyxProviderSlug = evt.NyxProviderSlug ?? string.Empty;
        next.NyxApiKey = evt.NyxApiKey ?? string.Empty;
        next.OwnerNyxUserId = evt.OwnerNyxUserId ?? string.Empty;
        next.ApiKeyId = evt.ApiKeyId ?? string.Empty;
        next.Enabled = evt.Enabled;
        next.ScopeId = evt.ScopeId ?? string.Empty;
        next.Platform = evt.Platform ?? string.Empty;
        return next;
    }

    private static WorkflowAgentState ApplyNextRunScheduled(WorkflowAgentState current, WorkflowAgentNextRunScheduledEvent evt)
    {
        var next = current.Clone();
        next.NextRunAt = evt.NextRunAt;
        return next;
    }

    private static WorkflowAgentState ApplyDispatched(WorkflowAgentState current, WorkflowAgentExecutionDispatchedEvent evt)
    {
        var next = current.Clone();
        next.LastRunAt = evt.DispatchedAt;
        next.LastError = string.Empty;
        next.ErrorCount = 0;
        return next;
    }

    private static WorkflowAgentState ApplyFailed(WorkflowAgentState current, WorkflowAgentExecutionFailedEvent evt)
    {
        var next = current.Clone();
        next.LastRunAt = evt.FailedAt;
        next.LastError = evt.Error ?? string.Empty;
        next.ErrorCount += 1;
        return next;
    }

    private static WorkflowAgentState ApplyDisabled(WorkflowAgentState current, WorkflowAgentDisabledEvent _)
    {
        var next = current.Clone();
        next.Enabled = false;
        next.NextRunAt = null;
        return next;
    }

    private static WorkflowAgentState ApplyEnabled(WorkflowAgentState current, WorkflowAgentEnabledEvent _)
    {
        var next = current.Clone();
        next.Enabled = true;
        return next;
    }

    private static string NormalizeTimezone(string? scheduleTimezone) =>
        string.IsNullOrWhiteSpace(scheduleTimezone) ? WorkflowAgentDefaults.DefaultTimezone : scheduleTimezone.Trim();

    private static string ResolvePlatform(string? platform) =>
        string.IsNullOrWhiteSpace(platform) ? WorkflowAgentDefaults.DefaultPlatform : platform.Trim();

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string MapDispatchError(WorkflowChatRunStartError error) => error switch
    {
        WorkflowChatRunStartError.AgentNotFound => "Workflow actor not found.",
        WorkflowChatRunStartError.WorkflowNotFound => "Workflow definition not found.",
        WorkflowChatRunStartError.AgentTypeNotSupported => "Actor is not workflow-capable.",
        WorkflowChatRunStartError.ProjectionDisabled => "Workflow projection is disabled.",
        WorkflowChatRunStartError.WorkflowBindingMismatch => "Workflow binding mismatch.",
        WorkflowChatRunStartError.AgentWorkflowNotConfigured => "Workflow actor is not bound to a workflow.",
        WorkflowChatRunStartError.InvalidWorkflowYaml => "Workflow YAML is invalid.",
        WorkflowChatRunStartError.WorkflowNameMismatch => "Workflow name does not match the bound workflow.",
        WorkflowChatRunStartError.PromptRequired => "Workflow prompt is required.",
        WorkflowChatRunStartError.ConflictingScopeId => "Workflow scope_id is conflicting.",
        _ => "Workflow run dispatch failed.",
    };
}
