using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Compatibility;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

[GAgent("scheduled.skill-definition")]
[LegacyAgentKind("scheduled.skill-runner")]
[LegacyClrTypeName(SkillRunnerLegacyAliases.ImplementationClr)]
public sealed class SkillDefinitionGAgent : GAgentBase<SkillDefinitionState>
{
    private ChannelScheduleRunner? _scheduler;

    private ChannelScheduleRunner Scheduler => _scheduler ??= new ChannelScheduleRunner(
        callbackId: SkillDefinitionDefaults.TriggerCallbackId,
        schedulableSource: () => State,
        triggerFactory: () => new TriggerSkillDefinitionCommand { Reason = "schedule" },
        persistNextRunEventAsync: nextRunUtc => PersistDomainEventAsync(new SkillDefinitionNextRunScheduledEvent
        {
            NextRunAt = Timestamp.FromDateTimeOffset(nextRunUtc),
        }),
        scheduleTimeoutAsync: (id, dueTime, evt, ct) => ScheduleSelfDurableTimeoutAsync(id, dueTime, evt, ct: ct),
        cancelCallbackAsync: (lease, ct) => CancelDurableCallbackAsync(lease, ct),
        logger: Logger,
        ownerDescription: $"Skill definition {Id}");

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await Scheduler.BootstrapOnActivateAsync(ct);
    }

    protected override SkillDefinitionState TransitionState(SkillDefinitionState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<SkillDefinitionInitializedEvent>(ApplyInitialized)
            .On<SkillDefinitionNextRunScheduledEvent>(ApplyNextRunScheduled)
            .On<SkillDefinitionExecutionDispatchFailedEvent>(ApplyExecutionDispatchFailed)
            .On<SkillDefinitionDisabledEvent>(ApplyDisabled)
            .On<SkillDefinitionEnabledEvent>(ApplyEnabled)
            // Legacy event compat: existing SkillRunnerGAgent instances that were
            // persisted before the split will replay their old event types.
            .On<SkillRunnerInitializedEvent>(ApplyLegacyInitialized)
            .On<SkillRunnerNextRunScheduledEvent>(ApplyLegacyNextRunScheduled)
            .On<SkillRunnerExecutionCompletedEvent>(ApplyLegacyExecutionIgnored)
            .On<SkillRunnerExecutionFailedEvent>(ApplyLegacyExecutionIgnored)
            .On<SkillRunnerDisabledEvent>(ApplyLegacyDisabled)
            .On<SkillRunnerEnabledEvent>(ApplyLegacyEnabled)
            .OrCurrent();

    [EventHandler]
    public async Task HandleInitializeAsync(InitializeSkillDefinitionCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.SkillContent))
        {
            Logger.LogWarning("Skill definition {ActorId} initialization ignored because skill_content is empty", Id);
            return;
        }

        var initialized = new SkillDefinitionInitializedEvent
        {
            SkillName = command.SkillName?.Trim() ?? string.Empty,
            TemplateName = command.TemplateName?.Trim() ?? string.Empty,
            SkillContent = command.SkillContent,
            ExecutionPrompt = command.ExecutionPrompt?.Trim() ?? string.Empty,
            ScheduleCron = command.ScheduleCron?.Trim() ?? string.Empty,
            ScheduleTimezone = NormalizeTimezone(command.ScheduleTimezone),
            OutboundConfig = command.OutboundConfig?.Clone() ?? new SkillRunnerOutboundConfig(),
            Enabled = command.Enabled,
            ScopeId = command.ScopeId?.Trim() ?? string.Empty,
            ProviderName = NormalizeProviderName(command.ProviderName),
            Model = command.Model?.Trim() ?? string.Empty,
        };

        if (command.HasTemperature)
            initialized.Temperature = command.Temperature;
        if (command.HasMaxTokens)
            initialized.MaxTokens = command.MaxTokens;
        if (command.HasMaxToolRounds)
            initialized.MaxToolRounds = command.MaxToolRounds;
        if (command.HasMaxHistoryMessages)
            initialized.MaxHistoryMessages = command.MaxHistoryMessages;

        await PersistDomainEventAsync(initialized);

        await Scheduler.ScheduleNextRunAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        await UpsertRegistryAsync(State.Enabled ? SkillDefinitionDefaults.StatusRunning : SkillDefinitionDefaults.StatusDisabled, CancellationToken.None);
    }

    // Backward compat: existing SkillRunnerGAgent instances send this command type.
    [EventHandler]
    public async Task HandleLegacyInitializeAsync(InitializeSkillRunnerCommand command)
    {
        var converted = new InitializeSkillDefinitionCommand
        {
            SkillName = command.SkillName,
            TemplateName = command.TemplateName,
            SkillContent = command.SkillContent,
            ExecutionPrompt = command.ExecutionPrompt,
            ScheduleCron = command.ScheduleCron,
            ScheduleTimezone = command.ScheduleTimezone,
            OutboundConfig = command.OutboundConfig,
            Enabled = command.Enabled,
            ScopeId = command.ScopeId,
            ProviderName = command.ProviderName,
            Model = command.Model,
        };

        if (command.HasTemperature)
            converted.Temperature = command.Temperature;
        if (command.HasMaxTokens)
            converted.MaxTokens = command.MaxTokens;
        if (command.HasMaxToolRounds)
            converted.MaxToolRounds = command.MaxToolRounds;
        if (command.HasMaxHistoryMessages)
            converted.MaxHistoryMessages = command.MaxHistoryMessages;

        await HandleInitializeAsync(converted);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleTriggerAsync(TriggerSkillDefinitionCommand command)
    {
        if (!State.Enabled)
        {
            Logger.LogInformation("Skill definition {ActorId} ignored trigger because it is disabled", Id);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var executionId = SkillDefinitionDefaults.GenerateExecutionId(Id);

        Exception? dispatchFailure = null;
        try
        {
            var actorRuntime = Services.GetRequiredService<IActorRuntime>();
            _ = await actorRuntime.GetAsync(executionId)
                ?? await actorRuntime.CreateAsync<SkillExecutionGAgent>(executionId, CancellationToken.None);

            var startCommand = new StartSkillExecutionCommand
            {
                DefinitionId = Id,
                ScheduledAt = Timestamp.FromDateTimeOffset(now),
                Reason = command.Reason?.Trim() ?? "schedule",
                SkillContent = State.SkillContent ?? string.Empty,
                ExecutionPrompt = State.ExecutionPrompt ?? string.Empty,
                OutboundConfig = State.OutboundConfig?.Clone() ?? new SkillRunnerOutboundConfig(),
                ScopeId = State.ScopeId ?? string.Empty,
                ProviderName = State.ProviderName ?? SkillDefinitionDefaults.DefaultProviderName,
                Model = State.Model ?? string.Empty,
            };

            if (State.HasTemperature)
                startCommand.Temperature = State.Temperature;
            if (State.HasMaxTokens)
                startCommand.MaxTokens = State.MaxTokens;
            if (State.HasMaxToolRounds)
                startCommand.MaxToolRounds = State.MaxToolRounds;
            if (State.HasMaxHistoryMessages)
                startCommand.MaxHistoryMessages = State.MaxHistoryMessages;

            var dispatchPort = Services.GetRequiredService<IActorDispatchPort>();
            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(now),
                Payload = Any.Pack(startCommand),
                Route = EnvelopeRouteSemantics.CreateDirect(Id, executionId),
            };
            await dispatchPort.DispatchAsync(executionId, envelope, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Skill definition {ActorId} failed to spawn execution {ExecutionId}", Id, executionId);
            await PersistDomainEventAsync(new SkillDefinitionExecutionDispatchFailedEvent
            {
                ExecutionId = executionId,
                FailedAt = Timestamp.FromDateTimeOffset(now),
                Reason = command.Reason?.Trim() ?? "schedule",
                Error = ex.Message,
            });
            dispatchFailure = ex;
        }

        await Scheduler.ScheduleNextRunAsync(now, CancellationToken.None);
        if (dispatchFailure is not null)
        {
            await UpdateRegistryExecutionErrorAsync(
                Timestamp.FromDateTimeOffset(now),
                dispatchFailure.Message,
                CancellationToken.None);
        }
    }

    // Backward compat: existing instances send this command type via durable callbacks.
    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleLegacyTriggerAsync(TriggerSkillRunnerExecutionCommand command)
    {
        await HandleTriggerAsync(new TriggerSkillDefinitionCommand { Reason = command.Reason });
    }

    [EventHandler]
    public async Task HandleDisableAsync(DisableSkillDefinitionCommand command)
    {
        await Scheduler.CancelAsync(CancellationToken.None);

        await PersistDomainEventAsync(new SkillDefinitionDisabledEvent
        {
            Reason = command.Reason?.Trim() ?? string.Empty,
        });

        await UpdateRegistryStatusAsync(SkillDefinitionDefaults.StatusDisabled, CancellationToken.None);
    }

    // Backward compat
    [EventHandler]
    public async Task HandleLegacyDisableAsync(DisableSkillRunnerCommand command)
    {
        await HandleDisableAsync(new DisableSkillDefinitionCommand { Reason = command.Reason });
    }

    [EventHandler]
    public async Task HandleEnableAsync(EnableSkillDefinitionCommand command)
    {
        if (!State.Enabled)
        {
            await PersistDomainEventAsync(new SkillDefinitionEnabledEvent
            {
                Reason = command.Reason?.Trim() ?? string.Empty,
            });
        }

        await Scheduler.ScheduleNextRunAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        await UpdateRegistryStatusAsync(SkillDefinitionDefaults.StatusRunning, CancellationToken.None);
    }

    // Backward compat
    [EventHandler]
    public async Task HandleLegacyEnableAsync(EnableSkillRunnerCommand command)
    {
        await HandleEnableAsync(new EnableSkillDefinitionCommand { Reason = command.Reason });
    }

    private async Task UpsertRegistryAsync(string status, CancellationToken ct)
    {
#pragma warning disable CS0612 // legacy field reads/writes during owner_scope migration
        var legacyOwnerNyxUserId = State.OutboundConfig?.OwnerNyxUserId ?? string.Empty;
        var legacyPlatform = ResolvePlatform(State.OutboundConfig?.Platform);
        var ownerScope = State.OutboundConfig?.OwnerScope
                         ?? OwnerScope.FromLegacyFields(legacyOwnerNyxUserId, legacyPlatform);

        var command = new UserAgentCatalogUpsertCommand
        {
            AgentId = Id,
            Platform = legacyPlatform,
            ConversationId = State.OutboundConfig?.ConversationId ?? string.Empty,
            NyxProviderSlug = State.OutboundConfig?.NyxProviderSlug ?? string.Empty,
            NyxApiKey = State.OutboundConfig?.NyxApiKey ?? string.Empty,
            OwnerNyxUserId = legacyOwnerNyxUserId,
            AgentType = SkillDefinitionDefaults.AgentType,
            TemplateName = State.TemplateName ?? string.Empty,
            ScopeId = State.ScopeId ?? string.Empty,
            ApiKeyId = State.OutboundConfig?.ApiKeyId ?? string.Empty,
            ScheduleCron = State.ScheduleCron ?? string.Empty,
            ScheduleTimezone = State.ScheduleTimezone ?? string.Empty,
            Status = status,
            LarkReceiveId = State.OutboundConfig?.LarkReceiveId ?? string.Empty,
            LarkReceiveIdType = State.OutboundConfig?.LarkReceiveIdType ?? string.Empty,
            LarkReceiveIdFallback = State.OutboundConfig?.LarkReceiveIdFallback ?? string.Empty,
            LarkReceiveIdTypeFallback = State.OutboundConfig?.LarkReceiveIdTypeFallback ?? string.Empty,
        };
#pragma warning restore CS0612

        if (ownerScope is not null)
            command.OwnerScope = ownerScope;

        await UserAgentCatalogStoreCommands.DispatchUpsertAsync(Services, Id, command, ct);
        await UpdateRegistryStatusAsync(status, ct);
    }

    private async Task UpdateRegistryStatusAsync(string status, CancellationToken ct)
    {
        var command = new UserAgentCatalogExecutionUpdateCommand
        {
            AgentId = Id, Status = status,
            NextRunAt = State.NextRunAt,
            ErrorCount = 0, LastError = string.Empty,
        };
        await UserAgentCatalogStoreCommands.DispatchExecutionUpdateAsync(Services, Id, command, ct);
    }

    private async Task UpdateRegistryExecutionErrorAsync(Timestamp lastRunAt, string error, CancellationToken ct)
    {
        var command = new UserAgentCatalogExecutionUpdateCommand
        {
            AgentId = Id,
            Status = SkillDefinitionDefaults.StatusError,
            LastRunAt = lastRunAt,
            NextRunAt = State.NextRunAt,
            ErrorCount = 1,
            LastError = error,
        };
        await UserAgentCatalogStoreCommands.DispatchExecutionUpdateAsync(Services, Id, command, ct);
    }

    private static SkillDefinitionState ApplyInitialized(SkillDefinitionState current, SkillDefinitionInitializedEvent evt)
    {
        var next = current.Clone();
        next.SkillName = evt.SkillName ?? string.Empty;
        next.TemplateName = evt.TemplateName ?? string.Empty;
        next.SkillContent = evt.SkillContent ?? string.Empty;
        next.ExecutionPrompt = evt.ExecutionPrompt ?? string.Empty;
        next.ScheduleCron = evt.ScheduleCron ?? string.Empty;
        next.ScheduleTimezone = NormalizeTimezone(evt.ScheduleTimezone);
        next.OutboundConfig = evt.OutboundConfig?.Clone() ?? new SkillRunnerOutboundConfig();
        next.Enabled = evt.Enabled;
        next.ScopeId = evt.ScopeId ?? string.Empty;
        next.ProviderName = NormalizeProviderName(evt.ProviderName);
        next.Model = evt.Model ?? string.Empty;

        if (evt.HasTemperature)
            next.Temperature = evt.Temperature;
        else
            next.ClearTemperature();
        if (evt.HasMaxTokens)
            next.MaxTokens = evt.MaxTokens;
        else
            next.ClearMaxTokens();

        next.MaxToolRounds = evt.HasMaxToolRounds ? evt.MaxToolRounds : SkillDefinitionDefaults.DefaultMaxToolRounds;
        next.MaxHistoryMessages = evt.HasMaxHistoryMessages ? evt.MaxHistoryMessages : SkillDefinitionDefaults.DefaultMaxHistoryMessages;
        return next;
    }

    private static SkillDefinitionState ApplyNextRunScheduled(SkillDefinitionState current, SkillDefinitionNextRunScheduledEvent evt)
    {
        var next = current.Clone();
        next.NextRunAt = evt.NextRunAt;
        return next;
    }

    private static SkillDefinitionState ApplyExecutionDispatchFailed(
        SkillDefinitionState current,
        SkillDefinitionExecutionDispatchFailedEvent _) => current;

    private static SkillDefinitionState ApplyDisabled(SkillDefinitionState current, SkillDefinitionDisabledEvent _)
    {
        var next = current.Clone();
        next.Enabled = false;
        next.NextRunAt = null;
        return next;
    }

    private static SkillDefinitionState ApplyEnabled(SkillDefinitionState current, SkillDefinitionEnabledEvent _)
    {
        var next = current.Clone();
        next.Enabled = true;
        return next;
    }

    // Legacy event appliers for existing persisted SkillRunnerGAgent state replay.
    private static SkillDefinitionState ApplyLegacyInitialized(SkillDefinitionState current, SkillRunnerInitializedEvent evt)
    {
        var converted = new SkillDefinitionInitializedEvent
        {
            SkillName = evt.SkillName,
            TemplateName = evt.TemplateName,
            SkillContent = evt.SkillContent,
            ExecutionPrompt = evt.ExecutionPrompt,
            ScheduleCron = evt.ScheduleCron,
            ScheduleTimezone = evt.ScheduleTimezone,
            OutboundConfig = evt.OutboundConfig,
            Enabled = evt.Enabled,
            ScopeId = evt.ScopeId,
            ProviderName = evt.ProviderName,
            Model = evt.Model,
        };

        if (evt.HasTemperature)
            converted.Temperature = evt.Temperature;
        if (evt.HasMaxTokens)
            converted.MaxTokens = evt.MaxTokens;
        if (evt.HasMaxToolRounds)
            converted.MaxToolRounds = evt.MaxToolRounds;
        if (evt.HasMaxHistoryMessages)
            converted.MaxHistoryMessages = evt.MaxHistoryMessages;

        return ApplyInitialized(current, converted);
    }

    private static SkillDefinitionState ApplyLegacyNextRunScheduled(SkillDefinitionState current, SkillRunnerNextRunScheduledEvent evt) =>
        ApplyNextRunScheduled(current, new SkillDefinitionNextRunScheduledEvent { NextRunAt = evt.NextRunAt });

    private static SkillDefinitionState ApplyLegacyExecutionIgnored(SkillDefinitionState current, IMessage _) => current;

    private static SkillDefinitionState ApplyLegacyDisabled(SkillDefinitionState current, SkillRunnerDisabledEvent _) =>
        ApplyDisabled(current, new SkillDefinitionDisabledEvent());

    private static SkillDefinitionState ApplyLegacyEnabled(SkillDefinitionState current, SkillRunnerEnabledEvent _) =>
        ApplyEnabled(current, new SkillDefinitionEnabledEvent());

    private static string NormalizeProviderName(string? providerName) =>
        string.IsNullOrWhiteSpace(providerName) ? SkillDefinitionDefaults.DefaultProviderName : providerName.Trim();

    private static string NormalizeTimezone(string? scheduleTimezone) =>
        string.IsNullOrWhiteSpace(scheduleTimezone) ? SkillDefinitionDefaults.DefaultTimezone : scheduleTimezone.Trim();

    private static string ResolvePlatform(string? platform) =>
        string.IsNullOrWhiteSpace(platform) ? SkillDefinitionDefaults.DefaultPlatform : platform.Trim();
}
