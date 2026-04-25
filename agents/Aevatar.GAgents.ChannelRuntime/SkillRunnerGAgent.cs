using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class SkillRunnerGAgent : AIGAgentBase<SkillRunnerState>
{
    private readonly NyxIdApiClient? _nyxIdApiClient;
    private ChannelScheduleRunner? _scheduler;
    private Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease? _retryLease;

    public SkillRunnerGAgent(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        NyxIdApiClient? nyxIdApiClient = null)
        : base(llmProviderFactory, additionalHooks, agentMiddlewares, toolMiddlewares, llmMiddlewares, toolSources)
    {
        _nyxIdApiClient = nyxIdApiClient;
    }

    private ChannelScheduleRunner Scheduler => _scheduler ??= new ChannelScheduleRunner(
        callbackId: SkillRunnerDefaults.TriggerCallbackId,
        schedulableSource: () => State,
        triggerFactory: () => new TriggerSkillRunnerExecutionCommand { Reason = "schedule" },
        persistNextRunEventAsync: nextRunUtc => PersistDomainEventAsync(new SkillRunnerNextRunScheduledEvent
        {
            NextRunAt = Timestamp.FromDateTimeOffset(nextRunUtc),
        }),
        scheduleTimeoutAsync: (id, dueTime, evt, ct) => ScheduleSelfDurableTimeoutAsync(id, dueTime, evt, ct: ct),
        cancelCallbackAsync: (lease, ct) => CancelDurableCallbackAsync(lease, ct),
        logger: Logger,
        ownerDescription: $"Skill runner {Id}");

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await Scheduler.BootstrapOnActivateAsync(ct);
    }

    protected override AIAgentConfigStateOverrides ExtractStateConfigOverrides(SkillRunnerState state)
    {
        return new AIAgentConfigStateOverrides
        {
            HasProviderName = !string.IsNullOrWhiteSpace(state.ProviderName),
            ProviderName = state.ProviderName,
            HasModel = !string.IsNullOrWhiteSpace(state.Model),
            Model = state.Model,
            HasSystemPrompt = !string.IsNullOrWhiteSpace(state.SkillContent),
            SystemPrompt = state.SkillContent,
            HasTemperature = state.HasTemperature,
            Temperature = state.HasTemperature ? state.Temperature : null,
            HasMaxTokens = state.HasMaxTokens,
            MaxTokens = state.HasMaxTokens ? state.MaxTokens : null,
            HasMaxToolRounds = state.HasMaxToolRounds,
            MaxToolRounds = state.HasMaxToolRounds ? state.MaxToolRounds : null,
            HasMaxHistoryMessages = state.HasMaxHistoryMessages,
            MaxHistoryMessages = state.HasMaxHistoryMessages ? state.MaxHistoryMessages : null,
        };
    }

    protected override SkillRunnerState TransitionState(SkillRunnerState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<SkillRunnerInitializedEvent>(ApplyInitialized)
            .On<SkillRunnerNextRunScheduledEvent>(ApplyNextRunScheduled)
            .On<SkillRunnerExecutionCompletedEvent>(ApplyCompleted)
            .On<SkillRunnerExecutionFailedEvent>(ApplyFailed)
            .On<SkillRunnerDisabledEvent>(ApplyDisabled)
            .On<SkillRunnerEnabledEvent>(ApplyEnabled)
            .OrCurrent();

    [EventHandler]
    public async Task HandleInitializeAsync(InitializeSkillRunnerCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.SkillContent))
        {
            Logger.LogWarning("Skill runner {ActorId} initialization ignored because skill_content is empty", Id);
            return;
        }

        var initialized = new SkillRunnerInitializedEvent
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
        await UpsertRegistryAsync(State.Enabled ? SkillRunnerDefaults.StatusRunning : SkillRunnerDefaults.StatusDisabled, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleTriggerAsync(TriggerSkillRunnerExecutionCommand command)
    {
        if (!State.Enabled)
        {
            Logger.LogInformation("Skill runner {ActorId} ignored trigger because it is disabled", Id);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        try
        {
            var output = await ExecuteSkillAsync(now, command.Reason, CancellationToken.None);
            await SendOutputAsync(output, CancellationToken.None);
            await PersistDomainEventAsync(new SkillRunnerExecutionCompletedEvent
            {
                CompletedAt = Timestamp.FromDateTimeOffset(now),
                Output = output,
            });

            await CancelRetryLeaseAsync(CancellationToken.None);
            await Scheduler.ScheduleNextRunAsync(now, CancellationToken.None);
            await UpdateRegistryExecutionAsync(
                SkillRunnerDefaults.StatusRunning,
                Timestamp.FromDateTimeOffset(now),
                State.NextRunAt,
                0,
                string.Empty,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Skill runner {ActorId} execution failed (attempt={Attempt})",
                Id,
                command.RetryAttempt);

            if (command.RetryAttempt < SkillRunnerDefaults.MaxRetryAttempts)
            {
                await ScheduleRetryAsync(command.RetryAttempt + 1, CancellationToken.None);
                return;
            }

            await PersistDomainEventAsync(new SkillRunnerExecutionFailedEvent
            {
                FailedAt = Timestamp.FromDateTimeOffset(now),
                Error = ex.Message,
            });

            await TrySendFailureAsync(ex.Message, CancellationToken.None);
            await Scheduler.ScheduleNextRunAsync(now, CancellationToken.None);
            await UpdateRegistryExecutionAsync(SkillRunnerDefaults.StatusError, State.LastRunAt, State.NextRunAt, State.ErrorCount, State.LastError, CancellationToken.None);
        }
    }

    private async Task ScheduleRetryAsync(int retryAttempt, CancellationToken ct)
    {
        await CancelRetryLeaseAsync(ct);
        _retryLease = await ScheduleSelfDurableTimeoutAsync(
            SkillRunnerDefaults.RetryCallbackId,
            SkillRunnerDefaults.RetryBackoff,
            new TriggerSkillRunnerExecutionCommand { Reason = "retry", RetryAttempt = retryAttempt },
            ct: ct);
        Logger.LogInformation(
            "Skill runner {ActorId} scheduled retry attempt {Attempt} in {Backoff}",
            Id, retryAttempt, SkillRunnerDefaults.RetryBackoff);
    }

    private async Task CancelRetryLeaseAsync(CancellationToken ct)
    {
        if (_retryLease == null)
            return;
        await CancelDurableCallbackAsync(_retryLease, ct);
        _retryLease = null;
    }

    [EventHandler]
    public async Task HandleDisableAsync(DisableSkillRunnerCommand command)
    {
        await Scheduler.CancelAsync(CancellationToken.None);
        await CancelRetryLeaseAsync(CancellationToken.None);

        await PersistDomainEventAsync(new SkillRunnerDisabledEvent
        {
            Reason = command.Reason?.Trim() ?? string.Empty,
        });

        await UpdateRegistryExecutionAsync(SkillRunnerDefaults.StatusDisabled, State.LastRunAt, null, State.ErrorCount, State.LastError, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleEnableAsync(EnableSkillRunnerCommand command)
    {
        if (!State.Enabled)
        {
            await PersistDomainEventAsync(new SkillRunnerEnabledEvent
            {
                Reason = command.Reason?.Trim() ?? string.Empty,
            });
        }

        await Scheduler.ScheduleNextRunAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        await UpdateRegistryExecutionAsync(
            SkillRunnerDefaults.StatusRunning, State.LastRunAt, State.NextRunAt,
            State.ErrorCount, State.LastError, CancellationToken.None);
    }

    private async Task<string> ExecuteSkillAsync(DateTimeOffset now, string? reason, CancellationToken ct)
    {
        var prompt = BuildExecutionPrompt(now, reason);
        var metadata = BuildExecutionMetadata();
        var requestId = Guid.NewGuid().ToString("N");
        var content = new StringBuilder();

        await foreach (var chunk in ChatStreamAsync(prompt, requestId, metadata, ct))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                content.Append(chunk.DeltaContent);
        }

        var output = content.ToString().Trim();
        return string.IsNullOrWhiteSpace(output) ? "No update generated." : output;
    }

    private async Task SendOutputAsync(string output, CancellationToken ct)
    {
        var client = _nyxIdApiClient ?? Services.GetService<NyxIdApiClient>();
        if (client is null)
        {
            Logger.LogWarning("Skill runner {ActorId} has no NyxIdApiClient registered; skipping outbound delivery", Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(State.OutboundConfig?.NyxApiKey) ||
            string.IsNullOrWhiteSpace(State.OutboundConfig?.NyxProviderSlug) ||
            string.IsNullOrWhiteSpace(State.OutboundConfig?.ConversationId))
        {
            Logger.LogWarning("Skill runner {ActorId} has incomplete outbound config; skipping outbound delivery", Id);
            return;
        }

        var deliveryTarget = LarkConversationTargets.Resolve(
            State.OutboundConfig.LarkReceiveId,
            State.OutboundConfig.LarkReceiveIdType,
            State.OutboundConfig.ConversationId);
        if (deliveryTarget.FellBackToPrefixInference)
        {
            // No typed receive_id captured at create time; only legacy state predating the
            // typed fields hits this path. Keep the breadcrumb so format drift is observable
            // when the prefix heuristic stops matching.
            Logger.LogDebug(
                "Skill runner {ActorId} resolved Lark receive target by prefix inference (legacy state): conversationId={ConversationId}, receiveIdType={ReceiveIdType}",
                Id,
                State.OutboundConfig.ConversationId,
                deliveryTarget.ReceiveIdType);
        }

        var body = JsonSerializer.Serialize(new
        {
            receive_id = deliveryTarget.ReceiveId,
            msg_type = "text",
            content = JsonSerializer.Serialize(new { text = output }),
        });

        var response = await client.ProxyRequestAsync(
            State.OutboundConfig.NyxApiKey,
            State.OutboundConfig.NyxProviderSlug,
            $"open-apis/im/v1/messages?receive_id_type={deliveryTarget.ReceiveIdType}",
            "POST", body, null, ct);

        if (LarkProxyResponse.TryGetError(response, out var larkCode, out var detail))
        {
            // Surface downstream rejection so HandleTriggerAsync sees a real failure instead of
            // persisting SkillRunnerExecutionCompletedEvent on a silently-dropped Lark response.
            // The Error field on SkillRunnerExecutionFailedEvent ends up in `/agent-status`'s
            // `last_error`, so for known recurring stale-state codes we expand the bare Lark
            // message into actionable recovery guidance — otherwise the user sees a cryptic
            // `99992361 open_id cross app` and has no way to know they need to rebuild the
            // agent.
            throw new InvalidOperationException(BuildLarkRejectionMessage(larkCode, detail));
        }
    }

    private static string BuildLarkRejectionMessage(int? larkCode, string detail)
    {
        if (larkCode == LarkBotErrorCodes.OpenIdCrossApp)
        {
            // The agent's persisted OutboundConfig was captured before union_id ingress existed
            // (PR #409 added that), so `LarkReceiveIdType=open_id` is permanently scoped to a
            // different Lark app than the customer outbound. Self-heal is not possible without
            // a fresh ingress event carrying union_id; the user must rebuild the agent.
            return
                $"Lark message delivery rejected (code={larkCode}): {detail}. " +
                "This agent was created before cross-app union_id ingress existed; " +
                "delete and recreate it (`/agents` → Delete → `/daily`) to pick up the cross-app safe target.";
        }

        return larkCode is { } code
            ? $"Lark message delivery rejected (code={code}): {detail}"
            : $"Lark message delivery rejected: {detail}";
    }

    private async Task TrySendFailureAsync(string error, CancellationToken ct)
    {
        try { await SendOutputAsync($"Skill runner failed: {error}", ct); }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Skill runner {ActorId} failed to send failure notification", Id);
        }
    }

    private IReadOnlyDictionary<string, string> BuildExecutionMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = State.OutboundConfig?.NyxApiKey ?? string.Empty,
            [ChannelMetadataKeys.ConversationId] = State.OutboundConfig?.ConversationId ?? string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(State.ScopeId))
            metadata["scope_id"] = State.ScopeId;
        return metadata;
    }

    private string BuildExecutionPrompt(DateTimeOffset now, string? reason)
    {
        var prompt = string.IsNullOrWhiteSpace(State.ExecutionPrompt)
            ? "Execute the configured skill now and return plain text only."
            : State.ExecutionPrompt;
        return $"{prompt}\nCurrent UTC time: {now:O}\nTrigger reason: {(string.IsNullOrWhiteSpace(reason) ? "manual" : reason)}";
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
            Platform = ResolvePlatform(State.OutboundConfig?.Platform),
            ConversationId = State.OutboundConfig?.ConversationId ?? string.Empty,
            NyxProviderSlug = State.OutboundConfig?.NyxProviderSlug ?? string.Empty,
            NyxApiKey = State.OutboundConfig?.NyxApiKey ?? string.Empty,
            OwnerNyxUserId = State.OutboundConfig?.OwnerNyxUserId ?? string.Empty,
            AgentType = SkillRunnerDefaults.AgentType,
            TemplateName = State.TemplateName ?? string.Empty,
            ScopeId = State.ScopeId ?? string.Empty,
            ApiKeyId = State.OutboundConfig?.ApiKeyId ?? string.Empty,
            ScheduleCron = State.ScheduleCron ?? string.Empty,
            ScheduleTimezone = State.ScheduleTimezone ?? string.Empty,
            Status = status,
            LarkReceiveId = State.OutboundConfig?.LarkReceiveId ?? string.Empty,
            LarkReceiveIdType = State.OutboundConfig?.LarkReceiveIdType ?? string.Empty,
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

    private static SkillRunnerState ApplyInitialized(SkillRunnerState current, SkillRunnerInitializedEvent evt)
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

        // Missing sampling fields intentionally use upstream model defaults;
        // missing runner limits fall back to SkillRunner defaults.
        if (evt.HasTemperature)
            next.Temperature = evt.Temperature;
        else
            next.ClearTemperature();
        if (evt.HasMaxTokens)
            next.MaxTokens = evt.MaxTokens;
        else
            next.ClearMaxTokens();

        next.MaxToolRounds = evt.HasMaxToolRounds ? evt.MaxToolRounds : SkillRunnerDefaults.DefaultMaxToolRounds;
        next.MaxHistoryMessages = evt.HasMaxHistoryMessages ? evt.MaxHistoryMessages : SkillRunnerDefaults.DefaultMaxHistoryMessages;
        return next;
    }

    private static SkillRunnerState ApplyNextRunScheduled(SkillRunnerState current, SkillRunnerNextRunScheduledEvent evt)
    {
        var next = current.Clone();
        next.NextRunAt = evt.NextRunAt;
        return next;
    }

    private static SkillRunnerState ApplyCompleted(SkillRunnerState current, SkillRunnerExecutionCompletedEvent evt)
    {
        var next = current.Clone();
        next.LastRunAt = evt.CompletedAt;
        next.LastOutput = evt.Output ?? string.Empty;
        next.LastError = string.Empty;
        next.ErrorCount = 0;
        return next;
    }

    private static SkillRunnerState ApplyFailed(SkillRunnerState current, SkillRunnerExecutionFailedEvent evt)
    {
        var next = current.Clone();
        next.LastRunAt = evt.FailedAt;
        next.LastError = evt.Error ?? string.Empty;
        next.ErrorCount += 1;
        return next;
    }

    private static SkillRunnerState ApplyDisabled(SkillRunnerState current, SkillRunnerDisabledEvent _)
    {
        var next = current.Clone();
        next.Enabled = false;
        next.NextRunAt = null;
        return next;
    }

    private static SkillRunnerState ApplyEnabled(SkillRunnerState current, SkillRunnerEnabledEvent _)
    {
        var next = current.Clone();
        next.Enabled = true;
        return next;
    }

    private static string NormalizeProviderName(string? providerName) =>
        string.IsNullOrWhiteSpace(providerName) ? SkillRunnerDefaults.DefaultProviderName : providerName.Trim();

    private static string NormalizeTimezone(string? scheduleTimezone) =>
        string.IsNullOrWhiteSpace(scheduleTimezone) ? SkillRunnerDefaults.DefaultTimezone : scheduleTimezone.Trim();

    private static string ResolvePlatform(string? platform) =>
        string.IsNullOrWhiteSpace(platform) ? SkillRunnerDefaults.DefaultPlatform : platform.Trim();
}
