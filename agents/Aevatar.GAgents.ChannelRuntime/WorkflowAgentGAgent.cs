using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class WorkflowAgentGAgent : GAgentBase<WorkflowAgentState>
{
    private RuntimeCallbackLease? _nextRunLease;

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);

        if (State.Enabled &&
            !string.IsNullOrWhiteSpace(State.ScheduleCron) &&
            (State.NextRunAt == null || State.NextRunAt.ToDateTimeOffset() <= DateTimeOffset.UtcNow))
        {
            await ScheduleNextRunAsync(DateTimeOffset.UtcNow, ct);
        }
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
        });

        if (State.Enabled && !string.IsNullOrWhiteSpace(State.ScheduleCron))
            await ScheduleNextRunAsync(DateTimeOffset.UtcNow, CancellationToken.None);

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
            var receipt = await DispatchWorkflowRunAsync(command.Reason, CancellationToken.None);
            await PersistDomainEventAsync(new WorkflowAgentExecutionDispatchedEvent
            {
                DispatchedAt = Timestamp.FromDateTimeOffset(now),
                WorkflowRunActorId = receipt.ActorId,
                CommandId = receipt.CommandId,
            });

            await ScheduleNextRunAsync(now, CancellationToken.None);
            await UpdateRegistryExecutionAsync(
                WorkflowAgentDefaults.StatusRunning,
                State.LastRunAt,
                State.NextRunAt,
                0,
                string.Empty,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Workflow agent {ActorId} execution dispatch failed", Id);
            await PersistDomainEventAsync(new WorkflowAgentExecutionFailedEvent
            {
                FailedAt = Timestamp.FromDateTimeOffset(now),
                Error = ex.Message,
            });

            await ScheduleNextRunAsync(now, CancellationToken.None);
            await UpdateRegistryExecutionAsync(
                WorkflowAgentDefaults.StatusError,
                State.LastRunAt,
                State.NextRunAt,
                State.ErrorCount,
                State.LastError,
                CancellationToken.None);
        }
    }

    [EventHandler]
    public async Task HandleDisableAsync(DisableWorkflowAgentCommand command)
    {
        if (_nextRunLease != null)
        {
            await CancelDurableCallbackAsync(_nextRunLease, CancellationToken.None);
            _nextRunLease = null;
        }

        await PersistDomainEventAsync(new WorkflowAgentDisabledEvent
        {
            Reason = command.Reason?.Trim() ?? string.Empty,
        });

        await UpdateRegistryExecutionAsync(
            WorkflowAgentDefaults.StatusDisabled,
            State.LastRunAt,
            null,
            State.ErrorCount,
            State.LastError,
            CancellationToken.None);
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

        await ScheduleNextRunAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        await UpdateRegistryExecutionAsync(
            WorkflowAgentDefaults.StatusRunning,
            State.LastRunAt,
            State.NextRunAt,
            State.ErrorCount,
            State.LastError,
            CancellationToken.None);
    }

    private async Task<WorkflowChatRunAcceptedReceipt> DispatchWorkflowRunAsync(string? reason, CancellationToken ct)
    {
        var dispatchService = Services.GetService<ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>();
        if (dispatchService is null)
            throw new InvalidOperationException("Workflow run dispatch service is not registered.");

        var metadata = BuildExecutionMetadata();
        var request = new WorkflowChatRunRequest(
            Prompt: BuildExecutionPrompt(reason),
            WorkflowName: State.WorkflowName,
            ActorId: State.WorkflowActorId,
            SessionId: null,
            InputParts: null,
            WorkflowYamls: null,
            Metadata: metadata,
            ScopeId: State.ScopeId);

        var dispatch = await dispatchService.DispatchAsync(request, ct);
        if (!dispatch.Succeeded || dispatch.Receipt is null)
            throw new InvalidOperationException(MapDispatchError(dispatch.Error));

        return dispatch.Receipt;
    }

    private async Task ScheduleNextRunAsync(DateTimeOffset fromUtc, CancellationToken ct)
    {
        if (!State.Enabled || string.IsNullOrWhiteSpace(State.ScheduleCron))
            return;

        if (!SkillRunnerScheduleCalculator.TryGetNextOccurrence(
                State.ScheduleCron,
                State.ScheduleTimezone,
                fromUtc,
                out var nextRunAtUtc,
                out var error))
        {
            Logger.LogWarning("Workflow agent {ActorId} could not compute next run: {Error}", Id, error);
            return;
        }

        var dueTime = nextRunAtUtc - DateTimeOffset.UtcNow;
        if (dueTime <= TimeSpan.Zero)
            dueTime = TimeSpan.FromSeconds(1);

        if (_nextRunLease != null)
            await CancelDurableCallbackAsync(_nextRunLease, ct);

        _nextRunLease = await ScheduleSelfDurableTimeoutAsync(
            WorkflowAgentDefaults.TriggerCallbackId,
            dueTime,
            new TriggerWorkflowAgentExecutionCommand { Reason = "schedule" },
            ct: ct);

        await PersistDomainEventAsync(new WorkflowAgentNextRunScheduledEvent
        {
            NextRunAt = Timestamp.FromDateTimeOffset(nextRunAtUtc),
        });
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

    private string BuildExecutionPrompt(string? reason)
    {
        var prompt = string.IsNullOrWhiteSpace(State.ExecutionPrompt)
            ? "Run the configured workflow now."
            : State.ExecutionPrompt;

        return $"{prompt}\nTrigger reason: {(string.IsNullOrWhiteSpace(reason) ? "manual" : reason)}";
    }

    private async Task UpsertRegistryAsync(string status, CancellationToken ct)
    {
        var runtime = Services.GetService<IActorRuntime>();
        if (runtime is null)
            return;

        var actor = await runtime.GetAsync(AgentRegistryGAgent.WellKnownId)
                    ?? await runtime.CreateAsync<AgentRegistryGAgent>(AgentRegistryGAgent.WellKnownId, ct);

        var command = new AgentRegistryUpsertCommand
        {
            AgentId = Id,
            Platform = "lark",
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
        string status,
        Timestamp? lastRunAt,
        Timestamp? nextRunAt,
        int errorCount,
        string? lastError,
        CancellationToken ct)
    {
        var runtime = Services.GetService<IActorRuntime>();
        if (runtime is null)
            return;

        var actor = await runtime.GetAsync(AgentRegistryGAgent.WellKnownId)
                    ?? await runtime.CreateAsync<AgentRegistryGAgent>(AgentRegistryGAgent.WellKnownId, ct);

        var command = new AgentRegistryExecutionUpdateCommand
        {
            AgentId = Id,
            Status = status,
            LastRunAt = lastRunAt,
            NextRunAt = nextRunAt,
            ErrorCount = errorCount,
            LastError = lastError ?? string.Empty,
        };

        await actor.HandleEventAsync(BuildDirectEnvelope(actor.Id, command), ct);
    }

    private static EventEnvelope BuildDirectEnvelope(string actorId, IMessage payload)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actorId },
            },
        };
    }

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
        string.IsNullOrWhiteSpace(scheduleTimezone)
            ? WorkflowAgentDefaults.DefaultTimezone
            : scheduleTimezone.Trim();

    private static string MapDispatchError(WorkflowChatRunStartError error) =>
        error switch
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
