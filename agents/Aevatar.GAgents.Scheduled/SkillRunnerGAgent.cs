using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.LLMProviders;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

// Refactor (iter1/cluster-001):
//   Old pattern: SkillRunnerGAgent pushed execution summaries into the well-known catalog actor.
//   New principle: Runner-owned committed events are the execution fact source for catalog projection.
[GAgent("scheduled.skill-runner")]
public sealed class SkillRunnerGAgent : AIGAgentBase<SkillRunnerState>
{
    private static readonly TimeSpan LongOutputDocumentDecisionTimeout = TimeSpan.FromSeconds(45);
    private const string LarkDocxCreateToolName = "lark_docx_create";

    private readonly NyxIdApiClient? _nyxIdApiClient;
    private readonly ILarkCardKitClient? _larkCardKitClient;
    private readonly ILarkOutboundDispatcher? _larkOutboundDispatcher;
    private readonly IOwnerLlmConfigSource? _ownerLlmConfigSource;
    private readonly IRemoteSkillFetcher? _remoteSkillFetcher;
    private readonly ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>? _workflowDispatchService;
    private readonly IClock _clock;
    // Per-run counter for nyxid_proxy outcomes, populated by the instance-owned
    // NyxIdProxyToolFailureCountingMiddleware appended to the tool-call middleware chain.
    // The runner reads it after each ChatStreamAsync to enforce the safety net for issue
    // #439 — see EnsureToolStatusAllowsCompletion.
    private readonly SkillRunnerToolFailureCounter _toolFailureCounter;
    private readonly SkillRunnerInteractiveDeliverySignalCollector _interactiveDeliverySignals;
    private string? _systemPromptOverride;
    private Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease? _oneShotLease;
    private Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease? _retryLease;

    public SkillRunnerGAgent(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        NyxIdApiClient? nyxIdApiClient = null,
        IOwnerLlmConfigSource? ownerLlmConfigSource = null,
        IRemoteSkillFetcher? remoteSkillFetcher = null,
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>? workflowDispatchService = null,
        IToolApprovalHandler? approvalHandler = null,
        IClock? clock = null,
        ILarkOutboundDispatcher? larkOutboundDispatcher = null,
        ILarkCardKitClient? larkCardKitClient = null)
        : this(
            BuildToolMiddlewareChain(toolMiddlewares),
            llmProviderFactory,
            additionalHooks,
            agentMiddlewares,
            llmMiddlewares,
            toolSources,
            nyxIdApiClient,
            ownerLlmConfigSource,
            remoteSkillFetcher,
            workflowDispatchService,
            approvalHandler,
            clock,
            larkOutboundDispatcher,
            larkCardKitClient)
    {
    }

    private SkillRunnerGAgent(
        ToolMiddlewareChain toolMiddlewareChain,
        ILLMProviderFactory? llmProviderFactory,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares,
        IEnumerable<IAgentToolSource>? toolSources,
        NyxIdApiClient? nyxIdApiClient,
        IOwnerLlmConfigSource? ownerLlmConfigSource,
        IRemoteSkillFetcher? remoteSkillFetcher,
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>? workflowDispatchService,
        IToolApprovalHandler? approvalHandler,
        IClock? clock,
        ILarkOutboundDispatcher? larkOutboundDispatcher,
        ILarkCardKitClient? larkCardKitClient)
        : base(
            llmProviderFactory,
            additionalHooks,
            agentMiddlewares,
            toolMiddlewareChain.Middlewares,
            llmMiddlewares,
            toolSources,
            approvalHandler)
    {
        _nyxIdApiClient = nyxIdApiClient;
        _larkOutboundDispatcher = larkOutboundDispatcher;
        _larkCardKitClient = larkCardKitClient;
        _ownerLlmConfigSource = ownerLlmConfigSource;
        _remoteSkillFetcher = remoteSkillFetcher;
        _workflowDispatchService = workflowDispatchService;
        _clock = clock ?? new SystemClock();
        _toolFailureCounter = toolMiddlewareChain.Counter;
        _interactiveDeliverySignals = toolMiddlewareChain.InteractiveDeliverySignals;
    }

    private readonly record struct ToolMiddlewareChain(
        IReadOnlyList<IToolCallMiddleware> Middlewares,
        SkillRunnerToolFailureCounter Counter,
        SkillRunnerInteractiveDeliverySignalCollector InteractiveDeliverySignals);

    private sealed record SkillRunnerExecutionPlan(
        SkillRunnerExecutionKind Kind,
        string SkillName,
        string SkillVersion,
        string Instructions,
        SkillWorkflowDescriptor? Workflow,
        SkillRunnerSkillReference? SkillRef);

    private sealed record WorkflowSelection(
        string WorkflowId,
        IReadOnlyList<WorkflowChatInlineYamlDocument> Documents);

    private sealed record SkillRunnerExecutionResult(
        string Output,
        SkillRunnerExecutionKind ExecutionKind,
        string SkillName,
        string SkillVersion,
        string WorkflowId,
        WorkflowChatRunAcceptedReceipt? WorkflowReceipt)
    {
        public static SkillRunnerExecutionResult Prompt(
            string output,
            SkillRunnerExecutionPlan plan) =>
            new(
                output,
                SkillRunnerExecutionKind.Prompt,
                plan.SkillName,
                plan.SkillVersion,
                string.Empty,
                null);

        public static SkillRunnerExecutionResult Workflow(
            string output,
            SkillRunnerExecutionPlan plan,
            WorkflowChatRunAcceptedReceipt receipt) =>
            new(
                output,
                SkillRunnerExecutionKind.Workflow,
                plan.SkillName,
                plan.SkillVersion,
                plan.Workflow?.WorkflowId ?? string.Empty,
                receipt);
    }

    private sealed class SkillRunnerExecutionException : InvalidOperationException
    {
        public SkillRunnerExecutionException(
            string message,
            SkillRunnerExecutionErrorCode errorCode,
            SkillRunnerExecutionKind executionKind = SkillRunnerExecutionKind.Unspecified,
            string skillName = "",
            string skillVersion = "",
            string workflowId = "")
            : base(message)
        {
            ErrorCode = errorCode;
            ExecutionKind = executionKind;
            SkillName = skillName;
            SkillVersion = skillVersion;
            WorkflowId = workflowId;
        }

        public SkillRunnerExecutionErrorCode ErrorCode { get; }
        public SkillRunnerExecutionKind ExecutionKind { get; }
        public string SkillName { get; }
        public string SkillVersion { get; }
        public string WorkflowId { get; }
    }

    private sealed class SkillRunnerVisibleDeliveryException : InvalidOperationException
    {
        public SkillRunnerVisibleDeliveryException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Test-only accessor for the per-run nyxid_proxy counter.</summary>
    internal SkillRunnerToolFailureCounter ToolFailureCounterForTesting => _toolFailureCounter;

    private static ToolMiddlewareChain BuildToolMiddlewareChain(
        IEnumerable<IToolCallMiddleware>? input)
    {
        var counter = new SkillRunnerToolFailureCounter();
        var interactiveDeliverySignals = new SkillRunnerInteractiveDeliverySignalCollector();
        var combined = (input ?? Array.Empty<IToolCallMiddleware>()).ToList();
        combined.Add(new NyxIdProxyToolFailureCountingMiddleware(counter));
        combined.Add(new SkillRunnerInteractiveDeliveryTrackingMiddleware(interactiveDeliverySignals));
        return new ToolMiddlewareChain(combined, counter, interactiveDeliverySignals);
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await RecoverExternalTriggerDeliveriesAsync(ct);
    }

    protected override AIAgentConfigStateOverrides ExtractStateConfigOverrides(SkillRunnerState state)
    {
        return new AIAgentConfigStateOverrides
        {
            HasProviderName = !string.IsNullOrWhiteSpace(state.ProviderName),
            ProviderName = state.ProviderName,
            HasModel = !string.IsNullOrWhiteSpace(state.Model),
            Model = state.Model,
            HasSystemPrompt = !HasSkillReference(state.SkillRef) && !string.IsNullOrWhiteSpace(state.SkillContent),
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
            .On<SkillRunnerExecutionRejectedEvent>(ApplyRejected)
            .On<SkillRunnerOneShotRetiredEvent>(ApplyOneShotRetired)
            .On<SkillRunnerExternalTriggerAdmittedEvent>(ApplyExternalTriggerAdmitted)
            .On<SkillRunnerExternalTriggerDispatchRequestedEvent>(ApplyExternalTriggerDispatchRequested)
            .On<SkillRunnerExternalTriggerRejectedEvent>(ApplyExternalTriggerRejected)
            .On<SkillRunnerExternalTriggerDuplicateIgnoredEvent>(ApplyExternalTriggerDuplicateIgnored)
            .On<SkillRunnerCronOccurrenceDuplicateIgnoredEvent>(ApplyCronOccurrenceDuplicateIgnored)
            .On<DeliveryProducedEvent>(ApplyDeliveryProduced)
            .On<SkillRunnerDisabledEvent>(ApplyDisabled)
            .On<SkillRunnerEnabledEvent>(ApplyEnabled)
            .OrCurrent();

    [EventHandler]
    public async Task HandleInitializeAsync(InitializeSkillRunnerCommand command)
    {
        var skillRef = NormalizeSkillReference(command.SkillRef ?? new SkillRunnerSkillReference());
        var scheduleMode = NormalizeScheduleMode(command.ScheduleMode);
        var hasSkillRef = HasSkillReference(skillRef) && !string.IsNullOrWhiteSpace(skillRef.Name);
        var hasInlineSkillContent = !string.IsNullOrWhiteSpace(command.SkillContent);
        var oneShotMessage = command.OneShotMessage?.Trim() ?? string.Empty;
        var hasOneShotMessage = scheduleMode == SkillRunnerScheduleMode.OneShot &&
                                !string.IsNullOrWhiteSpace(oneShotMessage);
        if (!hasSkillRef && !hasInlineSkillContent && !hasOneShotMessage)
        {
            Logger.LogWarning(
                "Skill runner {ActorId} initialization ignored because skill_ref.name, legacy skill_content, and one_shot_message are all empty",
                Id);
            return;
        }

        if (hasSkillRef && hasInlineSkillContent && !skillRef.AllowInlineFallback)
        {
            Logger.LogWarning(
                "Skill runner {ActorId} initialization ignored because skill_ref.name and skill_content were both provided without allow_inline_fallback",
                Id);
            return;
        }

        var outboundConfig = command.OutboundConfig?.Clone() ?? new SkillRunnerOutboundConfig();
        if (command.OutputFormat != SkillRunnerOutputFormat.Auto || outboundConfig.OutputFormat == SkillRunnerOutputFormat.Auto)
            outboundConfig.OutputFormat = command.OutputFormat;
#pragma warning disable CS0612 // legacy credential field must remain empty on new writes
        outboundConfig.NyxApiKey = string.Empty;
#pragma warning restore CS0612

        var initialized = new SkillRunnerInitializedEvent
        {
            SkillName = command.SkillName?.Trim() ?? string.Empty,
            TemplateName = command.TemplateName?.Trim() ?? string.Empty,
            SkillContent = hasSkillRef && !skillRef.AllowInlineFallback
                ? string.Empty
                : command.SkillContent,
            SkillRef = hasSkillRef ? skillRef : null,
            ExecutionPrompt = command.ExecutionPrompt?.Trim() ?? string.Empty,
            ScheduleCron = command.ScheduleCron?.Trim() ?? string.Empty,
            ScheduleTimezone = NormalizeTimezone(command.ScheduleTimezone),
            ScheduleMode = scheduleMode,
            OneShotRunAt = command.OneShotRunAt,
            OneShotMessage = oneShotMessage,
            OutboundConfig = outboundConfig,
            Enabled = command.Enabled,
            ScopeId = command.ScopeId?.Trim() ?? string.Empty,
            ProviderName = NormalizeProviderName(command.ProviderName),
            Model = command.Model?.Trim() ?? string.Empty,
            RequiresNyxidProxySuccess = command.RequiresNyxidProxySuccess,
        };

        if (command.HasTemperature)
            initialized.Temperature = command.Temperature;
        if (command.HasMaxTokens)
            initialized.MaxTokens = command.MaxTokens;
        if (command.HasMaxToolRounds)
            initialized.MaxToolRounds = command.MaxToolRounds;
        if (command.HasMaxHistoryMessages)
            initialized.MaxHistoryMessages = command.MaxHistoryMessages;

        initialized.ExternalTriggerSources.AddRange(command.ExternalTriggerSources
            .Select(NormalizeExternalTriggerSource)
            .Where(static source => !string.IsNullOrWhiteSpace(source.SourceId)));

        await PersistDomainEventAsync(initialized);

        await ScheduleOneShotRunAsync(_clock.UtcNow, CancellationToken.None);
        await UpsertRegistryAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleAdmitExternalTriggerAsync(AdmitSkillRunnerExternalTriggerCommand command)
    {
        var now = _clock.UtcNow;
        var identity = NormalizeExternalTriggerIdentity(command.Identity, now);
        if (!IsValidExternalTriggerIdentity(identity))
        {
            await PersistDomainEventAsync(new SkillRunnerExternalTriggerRejectedEvent
            {
                Identity = identity,
                RejectedAt = Timestamp.FromDateTimeOffset(now),
                Reason = SkillRunnerDefaults.ExternalTriggerRejectedReasonMalformedDelivery,
            });
            return;
        }

        var source = State.FindExternalTriggerSource(identity.SourceId);
        if (source is null)
        {
            await PersistDomainEventAsync(new SkillRunnerExternalTriggerRejectedEvent
            {
                Identity = identity,
                RejectedAt = Timestamp.FromDateTimeOffset(now),
                Reason = SkillRunnerDefaults.ExternalTriggerRejectedReasonUnknownSource,
            });
            return;
        }

        if (!source.Enabled)
        {
            await PersistDomainEventAsync(new SkillRunnerExternalTriggerRejectedEvent
            {
                Identity = NormalizeExternalTriggerIdentity(identity, source, now),
                RejectedAt = Timestamp.FromDateTimeOffset(now),
                Reason = SkillRunnerDefaults.ExternalTriggerRejectedReasonDisabledSource,
            });
            return;
        }

        identity = NormalizeExternalTriggerIdentity(identity, source, now);
        if (State.FindExternalTriggerDelivery(identity) is not null)
        {
            await PersistDomainEventAsync(new SkillRunnerExternalTriggerDuplicateIgnoredEvent
            {
                Identity = identity,
                IgnoredAt = Timestamp.FromDateTimeOffset(now),
                Reason = SkillRunnerDefaults.ExternalTriggerDuplicateReasonAlreadyAdmitted,
            });
            return;
        }

        await PersistDomainEventAsync(new SkillRunnerExternalTriggerAdmittedEvent
        {
            Identity = identity,
            AdmittedAt = Timestamp.FromDateTimeOffset(now),
        });

        await DispatchExternalTriggerExecutionAsync(identity, dispatchAttempt: 1, ct: CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleTriggerAsync(TriggerSkillRunnerExecutionCommand command)
    {
        var cronOccurrenceKey = ResolveCronOccurrenceKey(command);
        if (ShouldSkipCronOccurrence(command, cronOccurrenceKey))
        {
            await PersistDomainEventAsync(new SkillRunnerCronOccurrenceDuplicateIgnoredEvent
            {
                CronOccurrenceKey = cronOccurrenceKey,
                IgnoredAt = Timestamp.FromDateTimeOffset(_clock.UtcNow),
            });
            return;
        }

        var externalIdentity = NormalizeExternalTriggerIdentity(command.ExternalTriggerIdentity, _clock.UtcNow);
        var hasExternalTrigger = IsValidExternalTriggerIdentity(externalIdentity);
        if (hasExternalTrigger && State.IsExternalTriggerTerminal(externalIdentity))
        {
            await PersistDomainEventAsync(new SkillRunnerExternalTriggerDuplicateIgnoredEvent
            {
                Identity = externalIdentity,
                IgnoredAt = Timestamp.FromDateTimeOffset(_clock.UtcNow),
                Reason = SkillRunnerDefaults.ExternalTriggerDuplicateReasonAlreadyAdmitted,
            });
            return;
        }

        if (!State.Enabled)
        {
            Logger.LogInformation("Skill runner {ActorId} ignored trigger because it is disabled", Id);
            await PersistDomainEventAsync(new SkillRunnerExecutionRejectedEvent
            {
                RejectedAt = Timestamp.FromDateTimeOffset(_clock.UtcNow),
                Reason = SkillRunnerDefaults.RejectionReasonRunnerDisabled,
                ExternalTriggerIdentity = hasExternalTrigger ? externalIdentity : null,
                CronOccurrenceKey = cronOccurrenceKey,
            });
            if (State.ScheduleMode == SkillRunnerScheduleMode.OneShot && !hasExternalTrigger)
                await RetireOneShotAsync(_clock.UtcNow, SkillRunnerDefaults.OneShotRetirementReasonRejected, CancellationToken.None);
            return;
        }

        var now = _clock.UtcNow;
        try
        {
            var result = await ExecuteSkillAsync(now, command.Reason, CancellationToken.None);
            // Streaming-edit delivery happens in-line during ExecuteSkillAsync via the
            // SkillRunnerStreamingReplySink (POST initial + PUT each delta — Lark's text-edit
            // verb; PATCH on the same path is reserved for cards). When streaming can't be
            // configured (no NyxID client, missing outbound config) ExecuteSkillAsync falls
            // back to a one-shot SendOutputAsync at finalize, so we never need a second
            // outbound call here. Persist the run as completed using the buffered final text.
            await PersistDomainEventAsync(new SkillRunnerExecutionCompletedEvent
            {
                CompletedAt = Timestamp.FromDateTimeOffset(now),
                Output = result.Output,
                ExecutionKind = result.ExecutionKind,
                SkillName = result.SkillName,
                SkillVersion = result.SkillVersion,
                WorkflowId = result.WorkflowId,
                WorkflowActorId = result.WorkflowReceipt?.ActorId ?? string.Empty,
                WorkflowName = result.WorkflowReceipt?.WorkflowName ?? string.Empty,
                WorkflowCommandId = result.WorkflowReceipt?.CommandId ?? string.Empty,
                WorkflowCorrelationId = result.WorkflowReceipt?.CorrelationId ?? string.Empty,
                ExternalTriggerIdentity = hasExternalTrigger ? externalIdentity : null,
                CronOccurrenceKey = cronOccurrenceKey,
            });

            await CancelRetryLeaseAsync(CancellationToken.None);
            if (State.ScheduleMode == SkillRunnerScheduleMode.OneShot && !hasExternalTrigger)
            {
                await RetireOneShotAsync(now, SkillRunnerDefaults.OneShotRetirementReasonCompleted, CancellationToken.None);
                return;
            }

            await ScheduleOneShotRunAsync(now, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Skill runner {ActorId} execution failed (attempt={Attempt})",
                Id,
                command.RetryAttempt);

            // If Lark already has a visible card, retrying the whole run would create a
            // second card/message. Persist the failure immediately and let the failure
            // notification carry the recovery signal instead of duplicating the report.
            if (ex is not SkillRunnerVisibleDeliveryException &&
                command.RetryAttempt < SkillRunnerDefaults.MaxRetryAttempts)
            {
                await ScheduleRetryAsync(command, command.RetryAttempt + 1, CancellationToken.None);
                return;
            }

            var executionFailure = ex as SkillRunnerExecutionException;
            await PersistDomainEventAsync(new SkillRunnerExecutionFailedEvent
            {
                FailedAt = Timestamp.FromDateTimeOffset(now),
                Error = ex.Message,
                ExecutionKind = executionFailure?.ExecutionKind ?? SkillRunnerExecutionKind.Unspecified,
                SkillName = executionFailure?.SkillName ?? string.Empty,
                SkillVersion = executionFailure?.SkillVersion ?? string.Empty,
                WorkflowId = executionFailure?.WorkflowId ?? string.Empty,
                ErrorCode = executionFailure?.ErrorCode ?? SkillRunnerExecutionErrorCode.Unspecified,
                ExternalTriggerIdentity = hasExternalTrigger ? externalIdentity : null,
                CronOccurrenceKey = cronOccurrenceKey,
            });

            await TrySendFailureAsync(ex.Message, CancellationToken.None);
            if (State.ScheduleMode == SkillRunnerScheduleMode.OneShot && !hasExternalTrigger)
            {
                await RetireOneShotAsync(now, SkillRunnerDefaults.OneShotRetirementReasonFailed, CancellationToken.None);
                return;
            }

            await ScheduleOneShotRunAsync(now, CancellationToken.None);
        }
    }

    private async Task RetireOneShotAsync(DateTimeOffset retiredAt, string reason, CancellationToken ct)
    {
        await CancelOneShotLeaseAsync(ct);
        await CancelRetryLeaseAsync(ct);
        await PersistDomainEventAsync(new SkillRunnerOneShotRetiredEvent
        {
            RetiredAt = Timestamp.FromDateTimeOffset(retiredAt),
            Reason = reason,
        }, ct);
    }

    private async Task ScheduleRetryAsync(TriggerSkillRunnerExecutionCommand command, int retryAttempt, CancellationToken ct)
    {
        await CancelRetryLeaseAsync(ct);
        var retryCommand = command.Clone();
        retryCommand.RetryAttempt = retryAttempt;
        var cronOccurrenceKey = ResolveCronOccurrenceKey(command);
        var options = string.IsNullOrWhiteSpace(cronOccurrenceKey)
            ? null
            : CreateCronOccurrencePropagationOptions(cronOccurrenceKey);
        _retryLease = await ScheduleSelfDurableTimeoutAsync(
            SkillRunnerDefaults.RetryCallbackId,
            SkillRunnerDefaults.RetryBackoff,
            retryCommand,
            options,
            ct: ct);
        Logger.LogInformation(
            "Skill runner {ActorId} scheduled retry attempt {Attempt} in {Backoff}",
            Id, retryAttempt, SkillRunnerDefaults.RetryBackoff);
    }

    private bool ShouldSkipCronOccurrence(
        TriggerSkillRunnerExecutionCommand command,
        string cronOccurrenceKey) =>
        command.RetryAttempt == 0 &&
        !string.IsNullOrWhiteSpace(cronOccurrenceKey) &&
        State.IsCronOccurrenceTerminal(cronOccurrenceKey);

    private string ResolveCronOccurrenceKey(TriggerSkillRunnerExecutionCommand command)
    {
        if (!IsCronScheduleTrigger(command))
            return string.Empty;

        if (TryReadCronOccurrenceKeyFromBaggage(out var baggageKey))
            return baggageKey;

        return ActiveInboundEnvelope?.Id?.Trim() ?? string.Empty;
    }

    private bool TryReadCronOccurrenceKeyFromBaggage(out string cronOccurrenceKey)
    {
        cronOccurrenceKey = string.Empty;
        if (ActiveInboundEnvelope?.Propagation?.Baggage is null)
            return false;

        if (!ActiveInboundEnvelope.Propagation.Baggage.TryGetValue(
                ScheduledDispatchMetadataKeys.IdempotencyKey,
                out var value))
        {
            return false;
        }

        cronOccurrenceKey = value?.Trim() ?? string.Empty;
        return !string.IsNullOrEmpty(cronOccurrenceKey);
    }

    private static bool IsCronScheduleTrigger(TriggerSkillRunnerExecutionCommand command) =>
        string.Equals(command.Reason?.Trim(), SkillRunnerDefaults.ScheduleTriggerReason, StringComparison.Ordinal) &&
        !IsValidExternalTriggerIdentity(command.ExternalTriggerIdentity);

    private static EventEnvelopePublishOptions CreateCronOccurrencePropagationOptions(string cronOccurrenceKey)
    {
        var options = new EventEnvelopePublishOptions
        {
            Propagation = new EventEnvelopePropagationOverrides(),
        };
        options.Propagation.Baggage[ScheduledDispatchMetadataKeys.IdempotencyKey] = cronOccurrenceKey.Trim();
        return options;
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
        await CancelOneShotLeaseAsync(CancellationToken.None);
        await CancelRetryLeaseAsync(CancellationToken.None);

        await PersistDomainEventAsync(new SkillRunnerDisabledEvent
        {
            Reason = command.Reason?.Trim() ?? string.Empty,
        });
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

        await ScheduleOneShotRunAsync(_clock.UtcNow, CancellationToken.None);
    }

    private async Task ScheduleOneShotRunAsync(DateTimeOffset sampledUtc, CancellationToken ct)
    {
        if (!State.Enabled ||
            State.ScheduleMode != SkillRunnerScheduleMode.OneShot ||
            State.RetiredAt != null ||
            State.OneShotRunAt == null)
        {
            return;
        }

        var runAtUtc = State.OneShotRunAt.ToDateTimeOffset().ToUniversalTime();
        if (runAtUtc <= sampledUtc)
        {
            Logger.LogWarning(
                "Skill runner {ActorId} skipped one-shot scheduling because run_at_utc is not in the future",
                Id);
            return;
        }

        await CancelOneShotLeaseAsync(ct);
        _oneShotLease = await ScheduleSelfDurableTimeoutAsync(
            SkillRunnerDefaults.TriggerCallbackId,
            runAtUtc - sampledUtc,
            new TriggerSkillRunnerExecutionCommand { Reason = SkillRunnerDefaults.OneShotTriggerReason },
            ct: ct);
        await PersistDomainEventAsync(new SkillRunnerNextRunScheduledEvent
        {
            NextRunAt = Timestamp.FromDateTimeOffset(runAtUtc),
        }, ct);
    }

    private async Task CancelOneShotLeaseAsync(CancellationToken ct)
    {
        if (_oneShotLease == null)
            return;
        await CancelDurableCallbackAsync(_oneShotLease, ct);
        _oneShotLease = null;
    }

    private async Task RecoverExternalTriggerDeliveriesAsync(CancellationToken ct)
    {
        foreach (var record in State.RecoverableExternalTriggerDeliveries())
        {
            var identity = NormalizeExternalTriggerIdentity(record.Identity, _clock.UtcNow);
            if (!IsValidExternalTriggerIdentity(identity))
                continue;

            var nextAttempt = record.DispatchAttempt + 1;
            if (nextAttempt > SkillRunnerDefaults.ExternalTriggerMaxDispatchAttempts)
            {
                await PersistDomainEventAsync(new SkillRunnerExternalTriggerRejectedEvent
                {
                    Identity = identity,
                    RejectedAt = Timestamp.FromDateTimeOffset(_clock.UtcNow),
                    Reason = SkillRunnerDefaults.ExternalTriggerRejectedReasonDispatchAttemptsExhausted,
                }, ct);
                continue;
            }

            await DispatchExternalTriggerExecutionAsync(identity, nextAttempt, ct);
        }
    }

    private async Task DispatchExternalTriggerExecutionAsync(
        SkillRunnerExternalTriggerIdentity identity,
        int dispatchAttempt,
        CancellationToken ct)
    {
        await SendToAsync(
            Id,
            new TriggerSkillRunnerExecutionCommand
            {
                Reason = SkillRunnerDefaults.ExternalTriggerReason,
                ExternalTriggerIdentity = identity.Clone(),
            },
            ct);

        await PersistDomainEventAsync(new SkillRunnerExternalTriggerDispatchRequestedEvent
        {
            Identity = identity.Clone(),
            RequestedAt = Timestamp.FromDateTimeOffset(_clock.UtcNow),
            DispatchAttempt = dispatchAttempt,
        }, ct);
    }

    private async Task<SkillRunnerExecutionResult> ExecuteSkillAsync(DateTimeOffset now, string? reason, CancellationToken ct)
    {
        // Reset before each run so retries / scheduled triggers each see a clean slate.
        // The counter is populated by NyxIdProxyToolFailureCountingMiddleware as the LLM
        // fans out nyxid_proxy calls inside the ChatStreamAsync loop.
        _toolFailureCounter.Reset();
        _interactiveDeliverySignals.Reset();

        if (State.ScheduleMode == SkillRunnerScheduleMode.OneShot &&
            !string.IsNullOrWhiteSpace(State.OneShotMessage) &&
            !HasSkillReference(State.SkillRef) &&
            string.IsNullOrWhiteSpace(State.SkillContent))
        {
            var output = State.OneShotMessage.Trim();
            await SendTextOutputAsync(output, ct);
            return new SkillRunnerExecutionResult(
                output,
            SkillRunnerExecutionKind.Prompt,
            string.IsNullOrWhiteSpace(State.SkillName) ? SkillRunnerDefaults.OneShotSkillName : State.SkillName,
            string.Empty,
            string.Empty,
            null);
        }

        var plan = await BuildExecutionPlanAsync(ct);
        if (plan.Kind == SkillRunnerExecutionKind.Workflow)
            return await ExecuteWorkflowSkillAsync(plan, now, reason, ct);

        return SkillRunnerExecutionResult.Prompt(
            await ExecutePromptSkillAsync(plan, now, reason, ct),
            plan);
    }

    private async Task<string> ExecutePromptSkillAsync(
        SkillRunnerExecutionPlan plan,
        DateTimeOffset now,
        string? reason,
        CancellationToken ct)
    {
        var prompt = BuildExecutionPrompt(now, reason);
        var metadata = await BuildExecutionMetadataAsync(ct);
        var llmControl = await BuildExecutionLlmControlAsync(ct);
        var requestId = Guid.NewGuid().ToString("N");
        var toolContext = llmControl.ToToolContext(BuildExecutionToolContext(requestId, metadata));
        var content = new StringBuilder();

        var sink = await TryCreateStreamingSinkAsync(ct);
        var streamingState = sink is null
            ? null
            : new SkillRunnerStreamingRunState(sink, SkillRunnerDefaults.StreamingEditThrottle, TimeProvider.System);
        try
        {
            _systemPromptOverride = plan.Instructions;
            await foreach (var chunk in ChatStreamAsync(
                               [ContentPart.TextPart(prompt)],
                               requestId,
                               llmControl,
                               toolContext,
                               metadata,
                               ct))
            {
                if (string.IsNullOrEmpty(chunk.DeltaContent))
                    continue;
                content.Append(chunk.DeltaContent);
                if (streamingState is not null &&
                    content.Length <= SkillRunnerStreamingReplySink.MaxLarkTextLength)
                    // Per-delta `content.ToString()` is O(n) per call → O(n²) for the whole
                    // turn. Acceptable for bounded skill output (≤30 KB capped, and the
                    // actor-owned streaming state dedupes against `_lastEmittedText` so most allocations don't even
                    // make it onto the wire). If a future skill produces materially longer
                    // output, switch the sink contract to `(StringBuilder, Range)` snapshots
                    // or a `ReadOnlyMemory<char>` view so the accumulator isn't re-stringified
                    // every delta.
                    await streamingState.OnDeltaAsync(content.ToString(), ct);
            }

            var output = content.ToString().Trim();
            if (string.IsNullOrWhiteSpace(output))
                output = "No update generated.";

            // Issue #439 safety net (PR #471 + this PR): refuse to record fake-success runs.
            // Two failure modes are caught here:
            //   * all-fail — every nyxid_proxy call failed, the LLM's plain-text output is
            //     structurally indistinguishable from a real "no activity" report;
            //   * never-called — when State.RequiresNyxidProxySuccess is set, a run that
            //     completes with zero successful nyxid_proxy calls means the LLM bypassed
            //     tools entirely and produced text from prior context (the original #439
            //     symptom: 52 commits in 24h reported as "No meaningful public GitHub
            //     activity"). The original safety net only covered the all-fail case
            //     (failureCount > 0); this gap was flagged in PR #471 review and is closed
            //     here for fetch-and-summarize templates that opt in.
            // Throw before delivery so HandleTriggerAsync's catch path persists
            // SkillRunnerExecutionFailedEvent instead of a clean SkillRunnerExecutionCompletedEvent —
            // must fire BEFORE chunked dispatch so we don't post part-1 of a report
            // we're about to flag as failed.
            EnsureToolStatusAllowsCompletion(
                _toolFailureCounter.FailureCount,
                _toolFailureCounter.SuccessCount,
                State.RequiresNyxidProxySuccess,
                _toolFailureCounter.LatestFailure ?? _toolFailureCounter.FirstFailure);

            await PersistInteractiveDeliverySignalsAsync(requestId, CancellationToken.None);

            if (State.RecentDeliveries.Any(entry =>
                    entry.Status == DeliveryStatus.Succeeded &&
                    entry.DeliveryKind == DeliveryKind.InteractiveCard &&
                    string.Equals(entry.RequestId, requestId, StringComparison.Ordinal)))
            {
                Logger.LogInformation(
                    "Skill runner {ActorId} skipped outer Lark reply because the current run already committed a successful interactive/card delivery.",
                    Id);
                return output;
            }

            var chunks = await BuildOutputChunksAsync(
                output,
                requestId,
                llmControl,
                toolContext,
                metadata,
                ct);
            await DispatchOutputChunksAsync(
                streamingState,
                chunks,
                requestId,
                preferCardKit: ShouldPreferCardKitOutput(),
                ct);

            return output;
        }
        finally
        {
            _systemPromptOverride = null;
            sink?.Dispose();
        }
    }

    private async Task<SkillRunnerExecutionResult> ExecuteWorkflowSkillAsync(
        SkillRunnerExecutionPlan plan,
        DateTimeOffset now,
        string? reason,
        CancellationToken ct)
    {
        var workflow = plan.Workflow ?? throw new SkillRunnerExecutionException(
            "Workflow execution plan is missing a selected workflow.",
            SkillRunnerExecutionErrorCode.WorkflowSelectionRequired,
            SkillRunnerExecutionKind.Workflow,
            plan.SkillName,
            plan.SkillVersion);
        var selection = BuildWorkflowSelection(workflow);
        var dispatchService = _workflowDispatchService;
        if (dispatchService is null)
        {
            throw new SkillRunnerExecutionException(
                "Workflow dispatch service is not available for scheduled skill runner.",
                SkillRunnerExecutionErrorCode.WorkflowDispatchUnavailable,
                SkillRunnerExecutionKind.Workflow,
                plan.SkillName,
                plan.SkillVersion,
                workflow.WorkflowId);
        }

        var requestId = Guid.NewGuid().ToString("N");
        var prompt = BuildExecutionPrompt(now, reason);
        var nyxApiKey = await ResolveNyxApiKeyAsync(ct);
        var command = new WorkflowChatRunRequest(
            Prompt: prompt,
            Source: WorkflowChatSource.InlineYamlBundle(
                null,
                selection.Documents),
            SessionId: requestId,
            Metadata: await BuildExecutionMetadataAsync(ct),
            ScopeId: State.ScopeId,
            LlmControl: ToWorkflowLlmControl(await BuildExecutionLlmControlAsync(nyxApiKey, ct)),
            CallerCredential: new WorkflowCallerCredential(nyxApiKey),
            CommandIdSeed: requestId,
            CorrelationIdSeed: requestId);

        var result = await dispatchService.DispatchAsync(command, ct);
        if (!result.Succeeded || result.Receipt is null)
        {
            throw new SkillRunnerExecutionException(
                $"Workflow start failed: {result.Error}",
                SkillRunnerExecutionErrorCode.WorkflowDispatchRejected,
                SkillRunnerExecutionKind.Workflow,
                plan.SkillName,
                plan.SkillVersion,
                workflow.WorkflowId);
        }

        var receipt = result.Receipt;
        var output =
            $"Workflow start accepted: workflow_id={workflow.WorkflowId}, actor_id={receipt.ActorId}, command_id={receipt.CommandId}, correlation_id={receipt.CorrelationId}.";
        await SendOutputAsync(output, ct);
        return SkillRunnerExecutionResult.Workflow(output, plan, receipt);
    }

    /// <summary>
    /// Sends the chunk sequence produced by <see cref="SkillRunnerOutputChunker.Split"/>.
    /// Default Auto output prefers a single CardKit interactive message after the run has
    /// passed the tool-success safety net; explicit Text output can still use the legacy
    /// streaming-edit sink for chunk[0]. Overflow chunks are posted as plain text.
    /// </summary>
    /// <remarks>
    /// Failure semantics match the pre-chunking single-message path: any send rejection
    /// throws and propagates to <c>HandleTriggerAsync</c>'s retry/persist contract. A failure
    /// on chunk N &gt; 0 means chunks 0..N-1 already landed in chat — that's intentional
    /// partial visibility. Atomic multi-message delivery would require either a Lark-side
    /// transactional API (none exists) or buffering until all chunks succeed.
    /// </remarks>
    private async Task DispatchOutputChunksAsync(
        SkillRunnerStreamingRunState? streamingState,
        IReadOnlyList<string> chunks,
        string requestId,
        bool preferCardKit,
        CancellationToken ct)
    {
        if (chunks.Count == 0)
            return;

        if (streamingState is not null)
        {
            await streamingState.FinalizeAsync(chunks[0], ct);
            await PersistDeliveryProducedAsync(
                DeliveryKind.TextMessage,
                DeliveryStatus.Succeeded,
                requestId,
                sourceEventId: string.Empty,
                larkMessageId: streamingState.PlatformMessageId,
                cardId: string.Empty,
                ct);
        }
        else if (preferCardKit && chunks.Count == 1 && await TryDispatchCardKitOutputAsync(chunks[0], requestId, ct))
        {
            return;
        }
        else
        {
            // No CardKit/text streaming sink (explicit text mode, no NyxID client, missing
            // outbound config, or tests injecting a null client). Fall back to a one-shot
            // text send so the user still receives the report.
            await SendTextOutputAsync(chunks[0], providerSlugOverride: null, requestId, ct);
        }

        for (var i = 1; i < chunks.Count; i++)
            await SendTextOutputAsync(chunks[i], providerSlugOverride: null, requestId, ct);
    }

    private bool ShouldPreferCardKitOutput() =>
        State.OutboundConfig?.OutputFormat is null or SkillRunnerOutputFormat.Auto;

    private async Task<bool> TryDispatchCardKitOutputAsync(string output, string requestId, CancellationToken ct)
    {
        var sink = await TryCreateCardKitSinkAsync(ct);
        if (sink is null)
            return false;

        var result = await sink.SendFinalAsync(output, ct);
        if (result.Succeeded)
        {
            await PersistDeliveryProducedAsync(
                DeliveryKind.StreamingCard,
                DeliveryStatus.Succeeded,
                requestId,
                sourceEventId: string.Empty,
                larkMessageId: result.MessageId,
                cardId: result.CardId,
                ct);
            return true;
        }

        if (!result.VisibleMessageCreated)
        {
            await PersistDeliveryProducedAsync(
                DeliveryKind.StreamingCard,
                DeliveryStatus.FailedPreSend,
                requestId,
                sourceEventId: string.Empty,
                larkMessageId: string.Empty,
                cardId: result.CardId,
                ct);
            Logger.LogWarning(
                "Skill runner {ActorId} CardKit delivery failed before any visible Lark card was sent; falling back to text. card_id={CardId}, lark_code={LarkCode}, detail={Detail}",
                Id,
                result.CardId,
                result.LarkCode,
                result.Detail);
            return false;
        }

        await PersistDeliveryProducedAsync(
            DeliveryKind.StreamingCard,
            DeliveryStatus.FailedPostSend,
            requestId,
            sourceEventId: string.Empty,
            larkMessageId: result.MessageId,
            cardId: result.CardId,
            ct);
        throw new SkillRunnerVisibleDeliveryException(BuildLarkRejectionMessage(result.LarkCode, result.Detail));
    }

    private async Task<SkillRunnerCardKitReplySink?> TryCreateCardKitSinkAsync(CancellationToken ct)
    {
        if (!ShouldPreferCardKitOutput())
            return null;

        var client = _nyxIdApiClient ?? Services.GetService<NyxIdApiClient>();
        if (client is null)
        {
            Logger.LogWarning(
                "Skill runner {ActorId} has no NyxIdApiClient registered; CardKit delivery is disabled, falling back to text.",
                Id);
            return null;
        }

        if (string.IsNullOrWhiteSpace(State.OutboundConfig?.NyxProviderSlug) ||
            string.IsNullOrWhiteSpace(State.OutboundConfig?.ConversationId))
        {
            Logger.LogWarning(
                "Skill runner {ActorId} has incomplete outbound config (NyxProviderSlug/ConversationId); CardKit delivery is disabled, falling back to text.",
                Id);
            return null;
        }

        var nyxApiKey = await ResolveNyxApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(nyxApiKey))
        {
            Logger.LogWarning(
                "Skill runner {ActorId} could not resolve Nyx API key; CardKit delivery is disabled, falling back to text.",
                Id);
            return null;
        }

        var primary = LarkConversationTargets.Resolve(
            State.OutboundConfig.LarkReceiveId,
            State.OutboundConfig.LarkReceiveIdType,
            State.OutboundConfig.ConversationId);

        return new SkillRunnerCardKitReplySink(
            ResolveLarkCardKitClient(client, State.OutboundConfig.NyxProviderSlug),
            ResolveLarkOutboundDispatcher(client),
            new LarkSendNewMessageRequest(
                NyxApiKey: nyxApiKey,
                State.OutboundConfig.NyxProviderSlug,
                MessageType: "interactive",
                ContentJson: string.Empty,
                PrimaryTarget: primary,
                FallbackTarget: ResolveFallbackTarget()),
            Logger);
    }

    /// <summary>
    /// Constructs the legacy text streaming-edit sink for explicit text output. Auto output
    /// now uses CardKit after the run passes the tool-success safety net, which avoids Lark's
    /// text-message edit cap and prevents partial hallucinated reports from becoming visible.
    /// </summary>
    private async Task<SkillRunnerStreamingReplySink?> TryCreateStreamingSinkAsync(CancellationToken ct)
    {
        // Issue #439: when the run
        // is gated by EnsureToolStatusAllowsCompletion (RequiresNyxidProxySuccess set),
        // streaming each delta would POST/PUT the partial text to Lark live — i.e. a
        // hallucinated report would already be visible in the user's DM by the
        // time the guard fires, and each retry would repost it. Disable live streaming
        // for those skills so the message only POSTs through the chunked-dispatch path
        // AFTER the guard has confirmed at least one nyxid_proxy success. Trade-off: the
        // user no longer sees the report grow live, but output integrity wins over the
        // streaming-edit UX for fetch-and-summarize skills.
        if (State.RequiresNyxidProxySuccess)
            return null;

        if (State.OutboundConfig?.OutputFormat != SkillRunnerOutputFormat.Text)
            return null;

        var client = _nyxIdApiClient ?? Services.GetService<NyxIdApiClient>();
        if (client is null)
        {
            // Tests and very early bootstrap can run without an injected NyxID client; falling
            // back to one-shot SendOutputAsync is correct, but a log line makes the degradation
            // visible (otherwise streaming-edit silently never engages and the only symptom is
            // the wall-of-text UX users complained about in #423).
            Logger.LogWarning(
                "Skill runner {ActorId} has no NyxIdApiClient registered; streaming-edit delivery is disabled, falling back to one-shot SendOutputAsync.",
                Id);
            return null;
        }

        if (string.IsNullOrWhiteSpace(State.OutboundConfig?.NyxProviderSlug) ||
            string.IsNullOrWhiteSpace(State.OutboundConfig?.ConversationId))
        {
            Logger.LogWarning(
                "Skill runner {ActorId} has incomplete outbound config (NyxProviderSlug/ConversationId); streaming-edit delivery is disabled, falling back to one-shot SendOutputAsync.",
                Id);
            return null;
        }

        var nyxApiKey = await ResolveNyxApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(nyxApiKey))
        {
            Logger.LogWarning(
                "Skill runner {ActorId} could not resolve Nyx API key; streaming-edit delivery is disabled, falling back to one-shot SendOutputAsync.",
                Id);
            return null;
        }

        var primary = LarkConversationTargets.Resolve(
            State.OutboundConfig.LarkReceiveId,
            State.OutboundConfig.LarkReceiveIdType,
            State.OutboundConfig.ConversationId);

        return new SkillRunnerStreamingReplySink(
            ResolveLarkOutboundDispatcher(client),
            new LarkSendNewMessageRequest(
                NyxApiKey: nyxApiKey,
                State.OutboundConfig.NyxProviderSlug,
                MessageType: "text",
                ContentJson: string.Empty,
                PrimaryTarget: primary,
                FallbackTarget: ResolveFallbackTarget()),
            BuildLarkRejectionMessage,
            Logger,
            client);
    }

    /// <summary>
    /// Actor-owned coalescing state for one scheduled output stream.
    /// </summary>
    /// <remarks>
    /// Refactor (iter15/cluster-027-streaming-reply-timer-business-dispatch):
    ///   Old pattern: timer callback directly inspected/mutated pending output and performed Lark POST/PUT from callback timing
    ///   New principle: ExecuteSkillAsync owns throttle state, emitted text, and final dispatch ordering before calling the Lark transport sink.
    /// </remarks>
    private sealed class SkillRunnerStreamingRunState
    {
        private readonly SkillRunnerStreamingReplySink _sink;
        private readonly TimeSpan _throttle;
        private readonly TimeProvider _timeProvider;
        private string _lastEmittedText = string.Empty;
        private DateTimeOffset _lastEmitAt = DateTimeOffset.MinValue;
        private int _chunksEmitted;
        private string _pendingText = string.Empty;

        public SkillRunnerStreamingRunState(
            SkillRunnerStreamingReplySink sink,
            TimeSpan throttle,
            TimeProvider timeProvider)
        {
            _sink = sink;
            _throttle = throttle < TimeSpan.Zero ? TimeSpan.Zero : throttle;
            _timeProvider = timeProvider;
        }

        public Task OnDeltaAsync(string accumulatedText, CancellationToken ct) =>
            TryDispatchAsync(accumulatedText, isFinal: false, ct);

        public Task FinalizeAsync(string finalText, CancellationToken ct) =>
            TryDispatchAsync(finalText, isFinal: true, ct);

        public string? PlatformMessageId => _sink.PlatformMessageId;

        private async Task TryDispatchAsync(string text, bool isFinal, CancellationToken ct)
        {
            var capped = SkillRunnerStreamingReplySink.TruncateForLark(text);
            if (string.IsNullOrWhiteSpace(capped))
                return;

            if (string.Equals(capped, _lastEmittedText, StringComparison.Ordinal))
            {
                if (isFinal || string.Equals(capped, _pendingText, StringComparison.Ordinal))
                    ClearPending();
                return;
            }

            if (!isFinal)
            {
                var elapsed = _timeProvider.GetUtcNow() - _lastEmitAt;
                if (elapsed < _throttle)
                {
                    StashPending(capped);
                    return;
                }
            }

            await _sink.DispatchAsync(capped, isFinal, ct).ConfigureAwait(false);
            if (_sink.ChunksEmitted > _chunksEmitted)
            {
                _lastEmittedText = capped;
                _lastEmitAt = _timeProvider.GetUtcNow();
                _chunksEmitted = _sink.ChunksEmitted;
                if (isFinal || string.Equals(_pendingText, capped, StringComparison.Ordinal))
                    ClearPending();
            }
        }

        private void StashPending(string text)
        {
            _pendingText = text;
        }

        private void ClearPending()
        {
            _pendingText = string.Empty;
        }
    }

    /// <summary>
    /// Runner-layer safety net for issue #439. Two fake-success modes are caught here:
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>all-fail</b> (<paramref name="failureCount"/> &gt; 0, <paramref name="successCount"/> == 0):
    ///     every nyxid_proxy call failed, but the LLM's plain-text output is structurally
    ///     indistinguishable from a real "no activity" report. The prompt-layer §9 Source
    ///     health footer can be dropped by a weaker model, and the runner has no other way
    ///     to tell.
    ///   </description></item>
    ///   <item><description>
    ///     <b>never-called</b> (<paramref name="requiresNyxidProxySuccess"/> == true,
    ///     <paramref name="successCount"/> == 0): the LLM bypassed tools entirely and produced
    ///     text from prior context. For fetch-and-summarize skills this is
    ///     exactly the original #439 symptom (52 commits in 24h reported as "No meaningful
    ///     public GitHub activity"). Skills that don't depend on tool data (e.g. pure LLM
    ///     transformations) leave the flag false and pass through.
    ///   </description></item>
    /// </list>
    /// Throwing here routes through HandleTriggerAsync's existing catch path, which preserves
    /// the retry budget and (after retries are exhausted) persists SkillRunnerExecutionFailedEvent
    /// so <c>/agent-status</c> reports a non-zero <c>error_count</c> with a meaningful
    /// <c>last_error</c> instead of a fake-success run. Mixed runs (any successful nyxid_proxy
    /// call) still complete normally — partial data is more useful than a blanket failure, and
    /// the prompt-layer Source health footer surfaces the failed queries.
    /// </summary>
    internal static void EnsureToolStatusAllowsCompletion(
        int failureCount,
        int successCount,
        bool requiresNyxidProxySuccess,
        SkillRunnerToolFailureSample? latestFailure = null)
    {
        if (failureCount > 0 && successCount == 0)
        {
            var diagnostic = latestFailure?.ToDiagnosticString();
            var diagnosticSentence = string.IsNullOrWhiteSpace(diagnostic)
                ? string.Empty
                : $" 最近失败：{diagnostic}.";
            throw new InvalidOperationException(
                $"定时任务的数据源请求全部失败（nyxid_proxy {failureCount} 次），已拒绝把这次执行记录成空报告。{diagnosticSentence} " +
                "通常是 Ornn skill 里的目标服务、仓库、组织或 API 路径写错，也可能是上游服务暂时不可用；请检查 skill 指令或重新配置该定时任务。");
        }

        if (requiresNyxidProxySuccess && successCount == 0)
        {
            throw new InvalidOperationException(
                "这个定时任务要求至少成功读取一次数据源，但本次执行没有任何成功的 nyxid_proxy 调用。 " +
                "模型生成了输出，却没有取到实时数据；已拒绝把这次执行记录成成功。请检查 Ornn skill 指令和任务的数据源配置。");
        }
    }

    protected override string DecorateSystemPrompt(string basePrompt) =>
        _systemPromptOverride ?? base.DecorateSystemPrompt(basePrompt);

    private async Task<SkillRunnerExecutionPlan> BuildExecutionPlanAsync(CancellationToken ct)
    {
        var skillRef = State.SkillRef;
        if (HasSkillReference(skillRef))
            return await BuildRemoteExecutionPlanAsync(skillRef, ct);

        if (string.IsNullOrWhiteSpace(State.SkillContent))
        {
            throw new SkillRunnerExecutionException(
                "Skill runner requires either skill_ref.name or legacy skill_content.",
                SkillRunnerExecutionErrorCode.SkillReferenceRequired);
        }

        return new SkillRunnerExecutionPlan(
            SkillRunnerExecutionKind.Prompt,
            State.SkillName ?? string.Empty,
            string.Empty,
            State.SkillContent,
            null,
            null);
    }

    private async Task<SkillRunnerExecutionPlan> BuildRemoteExecutionPlanAsync(
        SkillRunnerSkillReference skillRef,
        CancellationToken ct)
    {
        var normalized = NormalizeSkillReference(skillRef);
        if (!string.IsNullOrEmpty(normalized.Version))
        {
            throw new SkillRunnerExecutionException(
                "Versioned scheduled skill references are not supported yet.",
                SkillRunnerExecutionErrorCode.SkillVersionUnsupported,
                skillName: normalized.Name,
                skillVersion: normalized.Version,
                workflowId: normalized.WorkflowId);
        }

        if (string.IsNullOrWhiteSpace(normalized.Name))
        {
            if (normalized.AllowInlineFallback && !string.IsNullOrWhiteSpace(State.SkillContent))
            {
                return new SkillRunnerExecutionPlan(
                    SkillRunnerExecutionKind.Prompt,
                    State.SkillName ?? string.Empty,
                    string.Empty,
                    State.SkillContent,
                    null,
                    normalized);
            }

            throw new SkillRunnerExecutionException(
                "Scheduled skill reference name is required.",
                SkillRunnerExecutionErrorCode.SkillReferenceRequired);
        }

        if (normalized.Source != SkillRunnerSkillSource.Ornn)
        {
            throw new SkillRunnerExecutionException(
                "Scheduled skill runner only supports Ornn skill references.",
                SkillRunnerExecutionErrorCode.SkillReferenceRequired,
                skillName: normalized.Name,
                skillVersion: normalized.Version,
                workflowId: normalized.WorkflowId);
        }

        var fetcher = _remoteSkillFetcher;
        if (fetcher is null)
        {
            if (normalized.AllowInlineFallback && !string.IsNullOrWhiteSpace(State.SkillContent))
            {
                return new SkillRunnerExecutionPlan(
                    SkillRunnerExecutionKind.Prompt,
                    normalized.Name,
                    string.Empty,
                    State.SkillContent,
                    null,
                    normalized);
            }

            throw new SkillRunnerExecutionException(
                "Remote skill fetcher is not available for scheduled skill runner.",
                SkillRunnerExecutionErrorCode.SkillFetcherUnavailable,
                skillName: normalized.Name,
                skillVersion: normalized.Version,
                workflowId: normalized.WorkflowId);
        }

        SkillDefinition? skill;
        try
        {
            skill = await fetcher.FetchSkillAsync(await ResolveNyxApiKeyAsync(ct), normalized.Name, ct);
        }
        catch (RemoteSkillFetchException ex) when (
            ex.FailureKind == RemoteSkillFetchFailureKind.AccessDenied ||
            ex.HttpStatus == 403)
        {
            throw new SkillRunnerExecutionException(
                $"Scheduled skill '{normalized.Name}' access denied while fetching through NyxID proxy. " +
                "The scheduled agent API key is missing proxy scope or service authorization for the Ornn service. " +
                "Reconnect the Ornn service in NyxID and recreate or rotate the scheduled agent key.",
                SkillRunnerExecutionErrorCode.SkillAccessDenied,
                skillName: normalized.Name,
                skillVersion: normalized.Version,
                workflowId: normalized.WorkflowId);
        }

        if (skill is null)
        {
            if (normalized.AllowInlineFallback && !string.IsNullOrWhiteSpace(State.SkillContent))
            {
                return new SkillRunnerExecutionPlan(
                    SkillRunnerExecutionKind.Prompt,
                    normalized.Name,
                    string.Empty,
                    State.SkillContent,
                    null,
                    normalized);
            }

            throw new SkillRunnerExecutionException(
                $"Scheduled skill '{normalized.Name}' was not found.",
                SkillRunnerExecutionErrorCode.SkillNotFound,
                skillName: normalized.Name,
                skillVersion: normalized.Version,
                workflowId: normalized.WorkflowId);
        }

        var selectedWorkflow = SelectWorkflow(skill, normalized);
        var instructions = string.IsNullOrWhiteSpace(skill.Instructions)
            ? State.SkillContent
            : skill.Instructions;
        return new SkillRunnerExecutionPlan(
            selectedWorkflow is null ? SkillRunnerExecutionKind.Prompt : SkillRunnerExecutionKind.Workflow,
            string.IsNullOrWhiteSpace(skill.Name) ? normalized.Name : skill.Name.Trim(),
            normalized.Version,
            instructions ?? string.Empty,
            selectedWorkflow,
            normalized);
    }

    private static SkillRunnerSkillReference NormalizeSkillReference(SkillRunnerSkillReference skillRef)
    {
        var normalized = skillRef.Clone();
        normalized.Name = normalized.Name?.Trim() ?? string.Empty;
        normalized.Version = normalized.Version?.Trim() ?? string.Empty;
        normalized.WorkflowId = normalized.WorkflowId?.Trim() ?? string.Empty;
        if (normalized.Source == SkillRunnerSkillSource.Unspecified)
            normalized.Source = SkillRunnerSkillSource.Ornn;
        return normalized;
    }

    private SkillWorkflowDescriptor? SelectWorkflow(
        SkillDefinition skill,
        SkillRunnerSkillReference skillRef)
    {
        var workflows = skill.Workflows?
            .Where(static workflow => workflow.WorkflowYamls.Any(static yaml => !string.IsNullOrWhiteSpace(yaml)))
            .ToArray() ?? [];
        if (workflows.Length == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(skillRef.WorkflowId))
        {
            var selected = workflows.FirstOrDefault(workflow =>
                string.Equals(workflow.WorkflowId?.Trim(), skillRef.WorkflowId, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                throw new SkillRunnerExecutionException(
                    $"Workflow '{skillRef.WorkflowId}' was not found in scheduled skill '{skillRef.Name}'.",
                    SkillRunnerExecutionErrorCode.WorkflowNotFound,
                    SkillRunnerExecutionKind.Workflow,
                    skill.Name,
                    skillRef.Version,
                    skillRef.WorkflowId);
            }

            return selected;
        }

        if (workflows.Length > 1)
        {
            throw new SkillRunnerExecutionException(
                $"Scheduled skill '{skillRef.Name}' has multiple workflows; skill_ref.workflow_id is required.",
                SkillRunnerExecutionErrorCode.WorkflowSelectionRequired,
                SkillRunnerExecutionKind.Workflow,
                skill.Name,
                skillRef.Version);
        }

        return workflows[0];
    }

    private static WorkflowSelection BuildWorkflowSelection(SkillWorkflowDescriptor workflow)
    {
        var workflowId = workflow.WorkflowId?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(workflowId))
        {
            throw new SkillRunnerExecutionException(
                "Selected workflow descriptor has no workflow_id.",
                SkillRunnerExecutionErrorCode.WorkflowSelectionRequired,
                SkillRunnerExecutionKind.Workflow);
        }

        var documents = workflow.WorkflowYamls
            .Where(static yaml => !string.IsNullOrWhiteSpace(yaml))
            .Select(static yaml => new WorkflowChatInlineYamlDocument(string.Empty, yaml.Trim()))
            .ToArray();
        if (documents.Length == 0)
        {
            throw new SkillRunnerExecutionException(
                $"Selected workflow '{workflowId}' has no workflow YAML.",
                SkillRunnerExecutionErrorCode.WorkflowNotFound,
                SkillRunnerExecutionKind.Workflow,
                workflowId: workflowId);
        }

        return new WorkflowSelection(workflowId, documents);
    }

    private static WorkflowLlmControl ToWorkflowLlmControl(LLMControlContext llmControl) =>
        new(
            ModelOverride: llmControl.ModelOverride,
            MaxToolRoundsOverride: llmControl.MaxToolRoundsOverride,
            UserMemoryPrompt: llmControl.UserMemoryPrompt,
            RoutePreference: llmControl.NyxIdRoutePreference);

    private async Task PersistInteractiveDeliverySignalsAsync(string requestId, CancellationToken ct)
    {
        foreach (var signal in _interactiveDeliverySignals.Signals)
        {
            await PersistDeliveryProducedAsync(
                signal.DeliveryKind,
                signal.Status,
                string.IsNullOrWhiteSpace(signal.RequestId) ? requestId : signal.RequestId,
                signal.SourceEventId,
                signal.LarkMessageId,
                signal.CardId,
                ct);
        }
    }

    private Task PersistDeliveryProducedAsync(
        DeliveryKind kind,
        DeliveryStatus status,
        string? requestId,
        string? sourceEventId,
        string? larkMessageId,
        string? cardId,
        CancellationToken ct) =>
        PersistDomainEventAsync(new DeliveryProducedEvent
        {
            RunId = Id,
            TurnId = NormalizeOptional(requestId) ?? string.Empty,
            DeliveryKind = kind,
            Target = BuildDeliveryTarget(),
            Status = status,
            LarkMessageId = NormalizeOptional(larkMessageId) ?? string.Empty,
            CardId = NormalizeOptional(cardId) ?? string.Empty,
            RequestId = NormalizeOptional(requestId) ?? string.Empty,
            SourceEventId = NormalizeOptional(sourceEventId) ?? string.Empty,
            ProducedAtVersion = NextCommittedVersion(),
        }, ct);

    private DeliveryTarget BuildDeliveryTarget()
    {
        var outbound = State.OutboundConfig;
        var target = LarkConversationTargets.Resolve(
            outbound?.LarkReceiveId,
            outbound?.LarkReceiveIdType,
            outbound?.ConversationId);
        return new DeliveryTarget
        {
            Channel = ChannelId.From("lark"),
            ConversationKey = outbound?.ConversationId ?? string.Empty,
            Platform = ResolveOutboundPlatform(outbound),
            ReceiveId = target.ReceiveId ?? string.Empty,
            ReceiveIdType = target.ReceiveIdType ?? string.Empty,
            ConversationId = outbound?.ConversationId ?? string.Empty,
        };
    }

    private static bool HasSkillReference(SkillRunnerSkillReference? skillRef) =>
        skillRef is not null &&
        (!string.IsNullOrWhiteSpace(skillRef.Name) ||
         !string.IsNullOrWhiteSpace(skillRef.Version) ||
         !string.IsNullOrWhiteSpace(skillRef.WorkflowId) ||
         skillRef.Source != SkillRunnerSkillSource.Unspecified ||
         skillRef.AllowInlineFallback);

    private AgentToolExecutionContext BuildExecutionToolContext(
        string requestId,
        IReadOnlyDictionary<string, string>? metadata) =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(requestId, null),
            Caller = new AgentToolCallerContext(State.ScopeId, State.ScopeId, requestId),
            Channel = new AgentToolChannelContext(
                null,
                null,
                State.ScopeId,
                null,
                null,
                Id),
            ExternalMetadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(metadata),
        };

    private async Task<string?> TryCreateLongOutputDocumentReplyAsync(
        string output,
        string requestId,
        LLMControlContext llmControl,
        AgentToolExecutionContext toolContext,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct)
    {
        var outputFormat = State.OutboundConfig?.OutputFormat ?? SkillRunnerOutputFormat.Auto;
        if (outputFormat == SkillRunnerOutputFormat.Text)
            return null;
        if (outputFormat == SkillRunnerOutputFormat.FeishuDoc)
            return await CreateRequiredFeishuDocumentReplyAsync(output, requestId, toolContext, ct);
        if (output.Length <= SkillRunnerStreamingReplySink.MaxLarkTextLength)
            return null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(LongOutputDocumentDecisionTimeout);
        try
        {
            var decisionText = new StringBuilder();
            await foreach (var chunk in ChatStreamAsync(
                               [ContentPart.TextPart(BuildLongOutputDocumentDecisionPrompt(output))],
                               $"{requestId}:lark-docx",
                               llmControl with { MaxToolRoundsOverride = 2 },
                               toolContext with
                               {
                                   Request = toolContext.Request with { RequestId = $"{requestId}:lark-docx", CallId = null },
                               },
                               metadata,
                               timeoutCts.Token))
            {
                if (!string.IsNullOrEmpty(chunk.DeltaContent))
                    decisionText.Append(chunk.DeltaContent);
            }

            var reply = decisionText.ToString().Trim();
            if (TryAcceptLongOutputDocumentReply(reply, out var accepted))
                return accepted;

            Logger.LogWarning(
                "Skill runner {ActorId} long-output document decision did not produce an accepted doc link; falling back to chunked delivery.",
                Id);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Logger.LogWarning(
                "Skill runner {ActorId} long-output document decision timed out; falling back to chunked delivery.",
                Id);
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Skill runner {ActorId} long-output document decision failed; falling back to chunked delivery.",
                Id);
            return null;
        }
    }

    private static string BuildLongOutputDocumentDecisionPrompt(string output) =>
        $"""
        The scheduled skill output below is too long for one Lark message.

        Decide whether the full content should be delivered as a Lark cloud document.
        If yes, call the {LarkDocxCreateToolName} tool exactly once with:
        - title: a concise title for this report
        - markdown_text: the complete output exactly as provided
        - visibility: readable

        If the tool result reports success=true with document_url, answer with one short user-facing Lark message that includes the document URL.
        If you do not call the tool, if the tool fails, or if there is no document_url, answer with DOCX_FALLBACK.
        Do not summarize, omit, or rewrite the report body in the final message.

        Output:
        {output}
        """;

    private async Task<IReadOnlyList<string>> BuildOutputChunksAsync(
        string output,
        string requestId,
        LLMControlContext llmControl,
        AgentToolExecutionContext toolContext,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct)
    {
        var docReply = await TryCreateLongOutputDocumentReplyAsync(
            output,
            requestId,
            llmControl,
            toolContext,
            metadata,
            ct);
        if (docReply is not null)
            return [docReply];

        return SkillRunnerOutputChunker.Split(output);
    }

    private async Task<string> CreateRequiredFeishuDocumentReplyAsync(
        string output,
        string requestId,
        AgentToolExecutionContext toolContext,
        CancellationToken ct)
    {
        var title = string.IsNullOrWhiteSpace(State.TemplateName)
            ? "Scheduled run output"
            : State.TemplateName.Trim();
        var arguments = JsonSerializer.Serialize(new
        {
            title,
            markdown_text = output,
            visibility = "readable",
        });
        var scopedToolContext = toolContext with
        {
            Request = toolContext.Request with { RequestId = $"{requestId}:lark-docx", CallId = "required-lark-docx" },
        };

        using var _ = AgentToolContextScope.Push(scopedToolContext);
        var result = await Tools.ExecuteToolCallAsync(
            new ToolCall
            {
                Id = "required-lark-docx",
                Name = LarkDocxCreateToolName,
                ArgumentsJson = arguments,
            },
            ct);

        if (!TryExtractSuccessfulDocumentUrl(result.Content, out var documentUrl))
        {
            throw new InvalidOperationException(
                "Feishu document output was requested, but document creation did not return a usable link.");
        }

        return $"Full output moved to {documentUrl}";
    }

    private static bool TryExtractSuccessfulDocumentUrl(string? resultJson, out string documentUrl)
    {
        documentUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(resultJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success) ||
                success.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            if (!root.TryGetProperty("document_url", out var urlElement) ||
                urlElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var url = urlElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(url) || !ContainsDocumentLink(url))
                return false;

            documentUrl = url;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryAcceptLongOutputDocumentReply(string reply, out string accepted)
    {
        accepted = string.Empty;
        if (string.IsNullOrWhiteSpace(reply))
            return false;
        if (reply.Contains("DOCX_FALLBACK", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!ContainsDocumentLink(reply))
            return false;
        if (reply.Length > SkillRunnerStreamingReplySink.MaxLarkTextLength)
            return false;

        accepted = reply;
        return true;
    }

    private static bool ContainsDocumentLink(string reply) =>
        reply.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
        reply.Contains("https://", StringComparison.OrdinalIgnoreCase);

    private async Task SendOutputAsync(string output, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var metadata = await BuildExecutionMetadataAsync(ct);
        var llmControl = await BuildExecutionLlmControlAsync(ct);
        var toolContext = llmControl.ToToolContext(BuildExecutionToolContext(requestId, metadata));
        var chunks = await BuildOutputChunksAsync(output, requestId, llmControl, toolContext, metadata, ct);
        await DispatchOutputChunksAsync(
            streamingState: null,
            chunks,
            requestId,
            preferCardKit: false,
            ct);
    }

    private Task SendTextOutputAsync(string output, CancellationToken ct) =>
        SendTextOutputAsync(output, providerSlugOverride: null, requestId: null, ct);

    /// <summary>
    /// Posts <paramref name="output"/> as a Lark text message. <paramref name="providerSlugOverride"/>
    /// is non-null only on the failure-notification fallback path (#423 §C); when set, the proxy
    /// routing slug temporarily replaces the agent's primary <c>NyxProviderSlug</c> so a message
    /// can still reach the user via the inbound channel-bot after the primary outbound has been
    /// rejected (e.g. cross-tenant <c>99992364</c>). All other call sites — main report send,
    /// chunked overflow continuations — pass <c>null</c> and stay on the primary slug.
    /// </summary>
    private async Task SendTextOutputAsync(
        string output,
        string? providerSlugOverride,
        string? requestId,
        CancellationToken ct)
    {
        var client = _nyxIdApiClient ?? Services.GetService<NyxIdApiClient>();
        if (client is null)
        {
            Logger.LogWarning("Skill runner {ActorId} has no NyxIdApiClient registered; skipping outbound delivery", Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(State.OutboundConfig?.NyxProviderSlug) ||
            string.IsNullOrWhiteSpace(State.OutboundConfig?.ConversationId))
        {
            Logger.LogWarning("Skill runner {ActorId} has incomplete outbound config; skipping outbound delivery", Id);
            return;
        }

        var nyxApiKey = await ResolveNyxApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(nyxApiKey))
        {
            Logger.LogWarning("Skill runner {ActorId} could not resolve Nyx API key; skipping outbound delivery", Id);
            return;
        }

        var slug = string.IsNullOrWhiteSpace(providerSlugOverride)
            ? State.OutboundConfig.NyxProviderSlug
            : providerSlugOverride!;
        var deliveryRequestId = ResolveDeliveryRequestId(requestId);

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

        var outcome = await ResolveLarkOutboundDispatcher(client).SendNewMessageAsync(
            new LarkSendNewMessageRequest(
                NyxApiKey: nyxApiKey,
                slug,
                MessageType: "text",
                ContentJson: JsonSerializer.Serialize(new { text = output }),
                PrimaryTarget: deliveryTarget,
                FallbackTarget: ResolveFallbackTarget()),
            ct);

        if (!outcome.Succeeded)
        {
            await PersistDeliveryProducedAsync(
                DeliveryKind.TextMessage,
                DeliveryStatus.FailedPreSend,
                requestId: deliveryRequestId,
                sourceEventId: string.Empty,
                larkMessageId: string.Empty,
                cardId: string.Empty,
                ct);
            // Surface downstream rejection so HandleTriggerAsync sees a real failure instead of
            // persisting SkillRunnerExecutionCompletedEvent on a silently-dropped Lark response.
            // The Error field on SkillRunnerExecutionFailedEvent ends up in `/agent-status`'s
            // `last_error`, so for known recurring stale-state codes we expand the bare Lark
            // message into actionable recovery guidance — otherwise the user sees a cryptic
            // `99992361 open_id cross app` and has no way to know they need to rebuild the
            // agent.
            throw new InvalidOperationException(BuildLarkRejectionMessage(outcome.LarkCode, outcome.Detail));
        }

        await PersistDeliveryProducedAsync(
            string.IsNullOrWhiteSpace(providerSlugOverride) ? DeliveryKind.TextMessage : DeliveryKind.FailureNotification,
            DeliveryStatus.Succeeded,
            requestId: deliveryRequestId,
            sourceEventId: string.Empty,
            larkMessageId: outcome.MessageId,
            cardId: string.Empty,
            ct);
    }

    private static string ResolveDeliveryRequestId(string? requestId) =>
        string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId.Trim();

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static string ResolveOutboundPlatform(SkillRunnerOutboundConfig? outbound)
    {
        if (!string.IsNullOrWhiteSpace(outbound?.OwnerScope?.Platform))
            return outbound.OwnerScope.Platform.Trim();

        return ResolvePlatform(outbound?.Platform);
    }

    private LarkReceiveTarget? ResolveFallbackTarget()
    {
        var fallbackId = State.OutboundConfig.LarkReceiveIdFallback?.Trim();
        var fallbackType = State.OutboundConfig.LarkReceiveIdTypeFallback?.Trim();
        return string.IsNullOrEmpty(fallbackId) || string.IsNullOrEmpty(fallbackType)
            ? null
            : new LarkReceiveTarget(fallbackId, fallbackType, FellBackToPrefixInference: false);
    }

    private ILarkOutboundDispatcher ResolveLarkOutboundDispatcher(NyxIdApiClient client) =>
        _larkOutboundDispatcher ?? Services.GetService<ILarkOutboundDispatcher>() ?? new LarkOutboundDispatcher(client, Logger);

    /// <summary>
    /// Resolves the CardKit client for a scheduled run. Prefers an injected/DI instance; falls
    /// back to a per-agent <see cref="LarkCardKitClient"/> bound to this agent's own Nyx provider
    /// slug so the CardKit wire protocol stays the single shared implementation used by both the
    /// scheduled and direct-chat paths.
    /// </summary>
    private ILarkCardKitClient ResolveLarkCardKitClient(NyxIdApiClient client, string providerSlug)
    {
        if (_larkCardKitClient is { } injected)
            return injected;

        if (Services.GetService<ILarkCardKitClient>() is { } fromDi)
            return fromDi;

        var effectiveSlug = string.IsNullOrWhiteSpace(providerSlug) ? "api-lark-bot" : providerSlug;
        return new LarkCardKitClient(new LarkToolOptions { ProviderSlug = effectiveSlug }, client);
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
                "delete and recreate it (`/agents` → Delete → recreate) to pick up the cross-app safe target.";
        }

        if (larkCode == LarkBotErrorCodes.UserIdCrossTenant)
        {
            // Even union_id is rejected — the relay-side ingress and outbound apps are in
            // different Lark tenants. No user-id-based identifier survives that boundary;
            // recreating the agent makes the new chat_id-preferred path take effect (chat_id
            // bypasses user-id translation entirely as long as the same app is on both ends).
            return
                $"Lark message delivery rejected (code={larkCode}): {detail}. " +
                "The outbound Lark app is in a different tenant than the inbound app, so " +
                "user-id translation is impossible. Delete and recreate the agent " +
                "(`/agents` → Delete → recreate) so the new chat_id-preferred outbound path " +
                "takes effect, or align the NyxID `s/api-lark-bot` proxy with the channel-bot that " +
                "received the inbound event.";
        }

        return larkCode is { } code
            ? $"Lark message delivery rejected (code={code}): {detail}"
            : $"Lark message delivery rejected: {detail}";
    }

    /// <summary>
    /// Best-effort delivery of a failure-notification message after the run has already failed.
    /// Issue #423 §C: when the primary outbound proxy was just rejected with a structural code
    /// (e.g. cross-tenant <c>99992364</c>), retrying through the same proxy obviously also
    /// fails. If a failure-notification slug was captured at agent-create time (the inbound
    /// channel-bot the user just successfully messaged), try IT first so the user actually
    /// sees that the run failed; on its failure, fall back to the primary slug as a last
    /// resort so single-tenant deployments (no separate failure slug) still get the same
    /// single-attempt behavior they had before this fix.
    /// </summary>
    /// <remarks>
    /// All exceptions are swallowed — by definition we are already in the failure path, and a
    /// failed notification must not raise above HandleTriggerAsync's own
    /// <c>SkillRunnerExecutionFailedEvent</c> persist (which is what surfaces
    /// <c>last_error</c> in <c>/agent-status</c> regardless of whether the user got a Lark
    /// message). Logs a warning for each unsuccessful attempt so the regression is observable.
    /// </remarks>
    private async Task TrySendFailureAsync(string error, CancellationToken ct)
    {
        var message = $"Skill runner failed: {error}";
        var failureSlug = State.OutboundConfig?.FailureNotificationProviderSlug?.Trim();
        var primarySlug = State.OutboundConfig?.NyxProviderSlug?.Trim();

        // Try the captured failure-notification slug first when it is set AND distinct from
        // the primary slug. Equal values would just hit the same proxy again, so we skip the
        // duplicate POST and go straight to the primary path.
        if (!string.IsNullOrEmpty(failureSlug) &&
            !string.Equals(failureSlug, primarySlug, StringComparison.Ordinal))
        {
            try
            {
                await SendTextOutputAsync(message, providerSlugOverride: failureSlug, requestId: null, ct);
                return;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Skill runner {ActorId} failed-notification via failure-notification slug rejected; falling back to primary slug",
                    Id);
            }
        }

        try
        {
            await SendTextOutputAsync(message, providerSlugOverride: null, requestId: null, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Skill runner {ActorId} failed to send failure notification", Id);
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildExecutionMetadataAsync(CancellationToken ct)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ChannelMetadataKeys.ConversationId] = State.OutboundConfig?.ConversationId ?? string.Empty,
        };
        AddIfNotEmpty(metadata, ChannelMetadataKeys.LarkReceiveId, State.OutboundConfig?.LarkReceiveId);
        AddIfNotEmpty(metadata, ChannelMetadataKeys.LarkReceiveIdType, State.OutboundConfig?.LarkReceiveIdType);
        AddIfNotEmpty(metadata, ChannelMetadataKeys.LarkOutboundProxySlug, State.OutboundConfig?.NyxProviderSlug);

        return metadata;
    }

    private static void AddIfNotEmpty(IDictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            metadata[key] = value.Trim();
    }

    private async Task<LLMControlContext> BuildExecutionLlmControlAsync(CancellationToken ct)
    {
        return await BuildExecutionLlmControlAsync(await ResolveNyxApiKeyAsync(ct), ct);
    }

    private async Task<LLMControlContext> BuildExecutionLlmControlAsync(string nyxApiKey, CancellationToken ct)
    {
        var control = new LLMControlContext(
            NyxIdAccessToken: nyxApiKey,
            NyxIdOrgToken: nyxApiKey,
            SenderNyxIdAccessToken: null,
            ModelOverride: null,
            NyxIdRoutePreference: null,
            MaxToolRoundsOverride: null,
            UserMemoryPrompt: null);

        // Pin the bot owner's pre-configured model + NyxID route + tool-round cap onto the
        // outbound typed LLM control, the same pattern AgentRunGAgent applies for
        // nyxid-chat. Without this, scheduled runs fall through to NyxIdLLMProvider's
        // compile-time defaults (`gpt-5.4` against `/api/v1/llm/gateway/v1/`), which the
        // gateway routes to the OpenAI provider — failing for bot owners who pre-configured
        // a custom NyxID service like `chrono-llm` at `/api/v1/proxy/s/chrono-llm`. The
        // source is bound once via constructor injection (LocalActorRuntime activates agents
        // through ActivatorUtilities so DI fills the optional ctor param at activation
        // time); a per-execution `Services.GetService<>` lookup would be redundant and was
        // dropped per codex's PR #509 partial dissent on r3159047120.
        return await OwnerLlmConfigApplier.ApplyAsync(
            control,
            State.ScopeId,
            _ownerLlmConfigSource,
            Logger,
            actorLabel: "Skill runner",
            actorId: Id,
            ct);
    }

    private async Task<string> ResolveNyxApiKeyAsync(CancellationToken ct)
    {
        var reference = State.OutboundConfig?.NyxApiKeyReference;
        if (!string.IsNullOrWhiteSpace(reference?.Ref))
        {
            var secretVault = Services.GetService<ISecretVault>()
                ?? throw new InvalidOperationException("Scheduled Nyx API key secret vault is unavailable.");
            var resolved = await secretVault.ResolveAsync(new ResolveSecretRequest(
                reference.Ref,
                ResolveScheduledAgentKeyPurpose(reference),
                reference.OwnerScopeKey,
                State.OutboundConfig?.ApiKeyId ?? string.Empty,
                "scheduled-skill-runner"),
                ct);
            return resolved.Secret?.Trim() ?? string.Empty;
        }

        return State.OutboundConfig?.NyxApiKey?.Trim() ?? string.Empty;
    }

    private static string ResolveScheduledAgentKeyPurpose(SecretReference reference) =>
        string.IsNullOrWhiteSpace(reference.Purpose)
            ? CredentialSecretPurposes.ScheduledNyxApiKey
            : reference.Purpose.Trim();

    private string BuildExecutionPrompt(DateTimeOffset now, string? reason)
    {
        var prompt = string.IsNullOrWhiteSpace(State.ExecutionPrompt)
            ? "Execute the configured skill now and return plain text only."
            : State.ExecutionPrompt;
        return $"{prompt}\nCurrent UTC time: {now:O}\nTrigger reason: {(string.IsNullOrWhiteSpace(reason) ? "manual" : reason)}";
    }

    private async Task UpsertRegistryAsync(CancellationToken ct)
    {
        var ownerScope = State.OutboundConfig?.OwnerScope;

        var command = new UserAgentCatalogUpsertCommand
        {
            AgentId = Id,
            ConversationId = State.OutboundConfig?.ConversationId ?? string.Empty,
            NyxProviderSlug = State.OutboundConfig?.NyxProviderSlug ?? string.Empty,
            NyxApiKey = string.Empty,
            NyxApiKeyReference = State.OutboundConfig?.NyxApiKeyReference?.Clone(),
            AgentType = SkillRunnerDefaults.AgentType,
            TemplateName = State.TemplateName ?? string.Empty,
            ScopeId = State.ScopeId ?? string.Empty,
            ApiKeyId = State.OutboundConfig?.ApiKeyId ?? string.Empty,
            ScheduleCron = State.ScheduleCron ?? string.Empty,
            ScheduleTimezone = State.ScheduleTimezone ?? string.Empty,
            LarkReceiveId = State.OutboundConfig?.LarkReceiveId ?? string.Empty,
            LarkReceiveIdType = State.OutboundConfig?.LarkReceiveIdType ?? string.Empty,
            LarkReceiveIdFallback = State.OutboundConfig?.LarkReceiveIdFallback ?? string.Empty,
            LarkReceiveIdTypeFallback = State.OutboundConfig?.LarkReceiveIdTypeFallback ?? string.Empty,
            OutputFormat = State.OutboundConfig?.OutputFormat ?? SkillRunnerOutputFormat.Auto,
        };

        // Refactor (iter92/cluster-092):
        //   Old: write path simultaneously emitted deprecated `Platform`/`OwnerNyxUserId`.
        //   New: write path emits only `OwnerScope`; legacy fields are retained only in
        //   the no-`OwnerScope` fallback branch for backwards compatibility.
        if (ownerScope is not null)
        {
            command.OwnerScope = ownerScope.Clone();
        }
        else
        {
#pragma warning disable CS0612 // legacy field write only for pre-owner_scope state
            var legacyOwnerNyxUserId = State.OutboundConfig?.OwnerNyxUserId ?? string.Empty;
            var legacyPlatform = ResolvePlatform(State.OutboundConfig?.Platform);
            command.Platform = legacyPlatform;
            command.OwnerNyxUserId = legacyOwnerNyxUserId;
            var legacyScope = OwnerScope.FromLegacyFields(legacyOwnerNyxUserId, legacyPlatform);
#pragma warning restore CS0612
            if (legacyScope is not null)
                command.OwnerScope = legacyScope;
        }

        await UserAgentCatalogStoreCommands.DispatchUpsertAsync(Services, Id, command, ct);
    }

    private static SkillRunnerState ApplyInitialized(SkillRunnerState current, SkillRunnerInitializedEvent evt)
    {
        var next = current.Clone();
        next.SkillName = evt.SkillName ?? string.Empty;
        next.TemplateName = evt.TemplateName ?? string.Empty;
        next.SkillContent = evt.SkillContent ?? string.Empty;
        next.SkillRef = evt.SkillRef?.Clone();
        next.ExecutionPrompt = evt.ExecutionPrompt ?? string.Empty;
        next.ScheduleCron = evt.ScheduleCron ?? string.Empty;
        next.ScheduleTimezone = NormalizeTimezone(evt.ScheduleTimezone);
        next.ScheduleMode = NormalizeScheduleMode(evt.ScheduleMode);
        next.OneShotRunAt = evt.OneShotRunAt;
        next.OneShotMessage = evt.OneShotMessage ?? string.Empty;
        next.RetiredAt = null;
        next.RetirementReason = string.Empty;
        next.OutboundConfig = evt.OutboundConfig?.Clone() ?? new SkillRunnerOutboundConfig();
        next.Enabled = evt.Enabled;
        next.ScopeId = evt.ScopeId ?? string.Empty;
        next.ProviderName = NormalizeProviderName(evt.ProviderName);
        next.Model = evt.Model ?? string.Empty;
        // Legacy actors created before proto field 16 existed replay an init event whose
        // RequiresNyxidProxySuccess deserializes as false, which would let them keep the
        // pre-#439 zero-tool-call fake-success path — making post-fix behavior depend on
        // creation time rather than template semantics. Derive the effective flag from
        // the template name so known fetch-and-summarize skills get the safety net on
        // replay regardless of when the actor was created. New templates that need this
        // protection should be added to RequiresProxySuccessByTemplate.
        next.RequiresNyxidProxySuccess = evt.RequiresNyxidProxySuccess
            || RequiresProxySuccessByTemplate(evt.TemplateName);

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
        next.ExternalTriggerSources.Clear();
        next.ExternalTriggerSources.AddRange(evt.ExternalTriggerSources
            .Select(NormalizeExternalTriggerSource)
            .Where(static source => !string.IsNullOrWhiteSpace(source.SourceId)));
        next.RecentExternalTriggerDeliveries.Clear();
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
        if (IsValidExternalTriggerIdentity(evt.ExternalTriggerIdentity))
        {
            next.UpsertExternalTriggerDelivery(
                evt.ExternalTriggerIdentity,
                SkillRunnerExternalTriggerDeliveryStatus.Completed,
                evt.CompletedAt ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow));
            next.TrimExternalTriggerDeliveries(ToDateTimeOffset(evt.CompletedAt));
        }

        MarkCronOccurrenceTerminal(
            next,
            evt.CronOccurrenceKey,
            evt.CompletedAt ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow));
        return next;
    }

    private static SkillRunnerState ApplyOneShotRetired(SkillRunnerState current, SkillRunnerOneShotRetiredEvent evt)
    {
        var next = current.Clone();
        next.Enabled = false;
        next.NextRunAt = null;
        next.RetiredAt = evt.RetiredAt;
        next.RetirementReason = evt.Reason ?? string.Empty;
        return next;
    }

    private static SkillRunnerState ApplyFailed(SkillRunnerState current, SkillRunnerExecutionFailedEvent evt)
    {
        var next = current.Clone();
        next.LastRunAt = evt.FailedAt;
        next.LastError = evt.Error ?? string.Empty;
        next.ErrorCount += 1;
        if (IsValidExternalTriggerIdentity(evt.ExternalTriggerIdentity))
        {
            next.UpsertExternalTriggerDelivery(
                evt.ExternalTriggerIdentity,
                SkillRunnerExternalTriggerDeliveryStatus.Failed,
                evt.FailedAt ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                evt.Error ?? string.Empty);
            next.TrimExternalTriggerDeliveries(ToDateTimeOffset(evt.FailedAt));
        }

        MarkCronOccurrenceTerminal(
            next,
            evt.CronOccurrenceKey,
            evt.FailedAt ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow));
        return next;
    }

    private static SkillRunnerState ApplyRejected(SkillRunnerState current, SkillRunnerExecutionRejectedEvent evt)
    {
        var next = current.Clone();
        next.LastRunAt = evt.RejectedAt;
        next.LastError = evt.Reason ?? string.Empty;
        next.ErrorCount += 1;
        if (IsValidExternalTriggerIdentity(evt.ExternalTriggerIdentity))
        {
            next.UpsertExternalTriggerDelivery(
                evt.ExternalTriggerIdentity,
                SkillRunnerExternalTriggerDeliveryStatus.Rejected,
                evt.RejectedAt ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                evt.Reason ?? string.Empty);
            next.TrimExternalTriggerDeliveries(ToDateTimeOffset(evt.RejectedAt));
        }

        MarkCronOccurrenceTerminal(
            next,
            evt.CronOccurrenceKey,
            evt.RejectedAt ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow));
        return next;
    }

    private static SkillRunnerState ApplyExternalTriggerAdmitted(
        SkillRunnerState current,
        SkillRunnerExternalTriggerAdmittedEvent evt)
    {
        var next = current.Clone();
        if (IsValidExternalTriggerIdentity(evt.Identity))
        {
            next.UpsertExternalTriggerDelivery(
                evt.Identity,
                SkillRunnerExternalTriggerDeliveryStatus.Admitted,
                evt.AdmittedAt ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow));
        }

        return next;
    }

    private static SkillRunnerState ApplyExternalTriggerDispatchRequested(
        SkillRunnerState current,
        SkillRunnerExternalTriggerDispatchRequestedEvent evt)
    {
        var next = current.Clone();
        if (IsValidExternalTriggerIdentity(evt.Identity))
        {
            next.UpsertExternalTriggerDelivery(
                evt.Identity,
                SkillRunnerExternalTriggerDeliveryStatus.DispatchRequested,
                evt.RequestedAt ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                dispatchAttempt: evt.DispatchAttempt);
        }

        return next;
    }

    private static SkillRunnerState ApplyExternalTriggerRejected(
        SkillRunnerState current,
        SkillRunnerExternalTriggerRejectedEvent evt)
    {
        var next = current.Clone();
        if (IsValidExternalTriggerIdentity(evt.Identity))
        {
            next.UpsertExternalTriggerDelivery(
                evt.Identity,
                SkillRunnerExternalTriggerDeliveryStatus.Rejected,
                evt.RejectedAt ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                evt.Reason ?? string.Empty);
            next.TrimExternalTriggerDeliveries(ToDateTimeOffset(evt.RejectedAt));
        }

        return next;
    }

    private static SkillRunnerState ApplyExternalTriggerDuplicateIgnored(
        SkillRunnerState current,
        SkillRunnerExternalTriggerDuplicateIgnoredEvent evt)
    {
        var next = current.Clone();
        if (IsValidExternalTriggerIdentity(evt.Identity))
        {
            if (next.FindExternalTriggerDelivery(evt.Identity) is null)
            {
                next.UpsertExternalTriggerDelivery(
                    evt.Identity,
                    SkillRunnerExternalTriggerDeliveryStatus.DuplicateIgnored,
                    evt.IgnoredAt ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    evt.Reason ?? string.Empty);
                next.TrimExternalTriggerDeliveries(ToDateTimeOffset(evt.IgnoredAt));
            }
        }

        return next;
    }

    private static SkillRunnerState ApplyCronOccurrenceDuplicateIgnored(
        SkillRunnerState current,
        SkillRunnerCronOccurrenceDuplicateIgnoredEvent evt)
    {
        var next = current.Clone();
        MarkCronOccurrenceTerminal(
            next,
            evt.CronOccurrenceKey,
            evt.IgnoredAt ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow));
        return next;
    }

    private static SkillRunnerState ApplyDeliveryProduced(SkillRunnerState current, DeliveryProducedEvent evt)
    {
        var next = current.Clone();
        next.AppendDelivery(evt);
        return next;
    }

    private static void MarkCronOccurrenceTerminal(
        SkillRunnerState state,
        string? cronOccurrenceKey,
        Timestamp terminalAt)
    {
        if (string.IsNullOrWhiteSpace(cronOccurrenceKey))
            return;

        state.UpsertCronOccurrenceTerminal(cronOccurrenceKey, terminalAt);
        state.TrimCronOccurrenceTerminals(ToDateTimeOffset(terminalAt));
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

    /// <summary>
    /// Templates whose runs MUST observe at least one successful nyxid_proxy call to be
    /// considered successful. Used by <see cref="ApplyInitialized"/> as the legacy-actor
    /// default when the persisted init event predates proto field 16. Add new templates
    /// here when they're fetch-and-summarize style (the LLM bypassing tools and producing
    /// text from prior context is a fake-success failure mode for them).
    /// </summary>
    internal static bool RequiresProxySuccessByTemplate(string? templateName) =>
        // Reserved for future fetch-and-summarize templates that need the runner-layer
        // safety net (issue #439). Currently empty: no in-tree template needs the
        // legacy proto-field-16-default backfill. Keep the method so tests + the apply
        // path don't need to special-case "no templates" — just add new entries here.
        templateName is not null && false;

    private static string NormalizeProviderName(string? providerName) =>
        string.IsNullOrWhiteSpace(providerName) ? SkillRunnerDefaults.DefaultProviderName : providerName.Trim();

    private static string NormalizeTimezone(string? scheduleTimezone) =>
        string.IsNullOrWhiteSpace(scheduleTimezone) ? SkillRunnerDefaults.DefaultTimezone : scheduleTimezone.Trim();

    private static SkillRunnerScheduleMode NormalizeScheduleMode(SkillRunnerScheduleMode scheduleMode) =>
        scheduleMode == SkillRunnerScheduleMode.OneShot
            ? SkillRunnerScheduleMode.OneShot
            : SkillRunnerScheduleMode.Cron;

    private static string ResolvePlatform(string? platform) =>
        string.IsNullOrWhiteSpace(platform) ? SkillRunnerDefaults.DefaultPlatform : platform.Trim();

    private static ExternalTriggerSource NormalizeExternalTriggerSource(ExternalTriggerSource source)
    {
        var normalized = source.Clone();
        normalized.SourceId = normalized.SourceId?.Trim() ?? string.Empty;
        normalized.DisplayName = normalized.DisplayName?.Trim() ?? string.Empty;
        if (normalized.Kind == ExternalTriggerSourceKind.Unspecified)
            normalized.Kind = ExternalTriggerSourceKind.Webhook;
        return normalized;
    }

    private static SkillRunnerExternalTriggerIdentity NormalizeExternalTriggerIdentity(
        SkillRunnerExternalTriggerIdentity? identity,
        DateTimeOffset now)
    {
        var normalized = identity?.Clone() ?? new SkillRunnerExternalTriggerIdentity();
        normalized.SourceId = normalized.SourceId?.Trim() ?? string.Empty;
        normalized.DeliveryId = normalized.DeliveryId?.Trim() ?? string.Empty;
        normalized.AdmissionId = string.IsNullOrWhiteSpace(normalized.AdmissionId)
            ? Guid.NewGuid().ToString("N")
            : normalized.AdmissionId.Trim();
        normalized.PayloadSummary = normalized.PayloadSummary?.Trim() ?? string.Empty;
        normalized.PayloadRef = normalized.PayloadRef?.Trim() ?? string.Empty;
        if (normalized.Kind == ExternalTriggerSourceKind.Unspecified)
            normalized.Kind = ExternalTriggerSourceKind.Webhook;
        normalized.ReceivedAt ??= Timestamp.FromDateTimeOffset(now);
        return normalized;
    }

    private static SkillRunnerExternalTriggerIdentity NormalizeExternalTriggerIdentity(
        SkillRunnerExternalTriggerIdentity identity,
        ExternalTriggerSource source,
        DateTimeOffset now)
    {
        var normalized = NormalizeExternalTriggerIdentity(identity, now);
        normalized.Kind = source.Kind == ExternalTriggerSourceKind.Unspecified
            ? ExternalTriggerSourceKind.Webhook
            : source.Kind;
        return normalized;
    }

    private static bool IsValidExternalTriggerIdentity(SkillRunnerExternalTriggerIdentity? identity) =>
        identity is not null &&
        !string.IsNullOrWhiteSpace(identity.SourceId) &&
        !string.IsNullOrWhiteSpace(identity.DeliveryId);

    private static DateTimeOffset ToDateTimeOffset(Timestamp? timestamp) =>
        timestamp?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;

    private long NextCommittedVersion() =>
        (EventSourcing ?? throw new InvalidOperationException("Event sourcing must be configured before computing the next committed version."))
        .CurrentVersion + 1;
}
