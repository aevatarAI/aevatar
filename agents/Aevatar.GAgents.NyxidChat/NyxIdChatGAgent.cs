using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.Prompting;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Observability;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// NyxID chat GAgent. Extends RoleGAgent with a chat system prompt.
/// On first activation (empty state), self-initializes with the system prompt
/// so callers never need to dispatch InitializeRoleAgentEvent manually.
/// Always pins the NyxID-backed provider so requests are routed using the
/// authenticated NyxID account instead of drifting with the app default.
/// The NyxID provider itself decides whether to use a user-configured
/// chrono-llm service or fall back to the NyxID LLM gateway.
/// </summary>
// Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
//   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
//   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
[GAgent(NyxIdChatServiceDefaults.LegacyGAgentKind)]
public sealed class NyxIdChatGAgent : RoleGAgent
{
    private const int SystemSkillOverlayPromptLogSampleRate = 64;

    private readonly IBuiltInPromptFloorProvider _builtInPromptFloorProvider;
    private readonly ISystemSkillOverlayProvider? _systemSkillOverlayProvider;
    private readonly LocalSkillCatalog? _localSkillCatalog;
    private readonly NyxIdRelayOptions? _relayOptions;
    private readonly TimeProvider _timeProvider;
    private readonly AgentProfileTurnCatalogMaterializer? _turnCatalogMaterializer;
    private AgentProfileTelemetryContext? _activeAgentProfileTelemetryContext;
    private int _systemSkillOverlayPromptLogCounter;

    public NyxIdChatGAgent(
        IBuiltInPromptFloorProvider builtInPromptFloorProvider,
        IAgentToolExecutionPort toolExecutionPort,
        ISystemSkillOverlayProvider? systemSkillOverlayProvider = null,
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        LocalSkillCatalog? localSkillCatalog = null,
        IRemoteToolApprovalPort? remoteToolApprovalPort = null,
        IRemoteToolApprovalNotificationPort? remoteToolApprovalNotificationPort = null,
        NyxIdRelayOptions? relayOptions = null,
        TimeProvider? timeProvider = null,
        AgentProfileTurnCatalogMaterializer? turnCatalogMaterializer = null,
        RoleChatExecutionOptions? chatExecutionOptions = null,
        ISecretVault? chatToolRecoverySecretVault = null)
        : base(toolExecutionPort, llmProviderFactory, additionalHooks, agentMiddlewares, llmMiddlewares, toolSources,
               remoteToolApprovalPort: remoteToolApprovalPort,
               remoteToolApprovalNotificationPort: remoteToolApprovalNotificationPort,
               timeProvider: timeProvider,
               chatExecutionOptions: chatExecutionOptions,
               chatToolRecoverySecretVault: chatToolRecoverySecretVault)
    {
        _builtInPromptFloorProvider = builtInPromptFloorProvider ??
                                      throw new ArgumentNullException(nameof(builtInPromptFloorProvider));
        _systemSkillOverlayProvider = systemSkillOverlayProvider;
        _localSkillCatalog = localSkillCatalog;
        _relayOptions = relayOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _turnCatalogMaterializer = turnCatalogMaterializer;
    }

    protected override TimeProvider ChatRequestTimeProvider => _timeProvider;

    // Refactor (iter23/cluster-001-nyxid-tool-approval-polling):
    //   Old pattern: NyxID chat passed remote approval as a blocking local IToolApprovalHandler.
    //   New principle: local handler yields; remote port submit/status is owned by RoleGAgent continuation.
    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(State.RoleName))
        {
            await PersistDomainEventAsync(BuildInitializeRoleAgentEvent(NyxIdChatServiceDefaults.DisplayName));
        }
        else if (RequiresNyxIdProviderMigration())
        {
            await PersistDomainEventAsync(BuildInitializeRoleAgentEvent(State.RoleName));
        }

        await base.OnActivateAsync(ct);
        await RequestPendingDirectChatHistoryDeliveryAsync(ct);
    }

    protected override string DecorateSystemPrompt(
        string basePrompt,
        AgentProfileTurnCatalog? turnCatalog)
    {
        var runtimeFacts = new System.Text.StringBuilder();
        AppendRuntimeFact(
            runtimeFacts,
            NyxIdRelayPromptConfiguration.BuildChannelRuntimeConfigurationSection(_relayOptions));

        // Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
        //   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
        //   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
        if (_localSkillCatalog != null && _localSkillCatalog.Count > 0)
        {
            var skillSection = _localSkillCatalog.BuildSystemPromptSection();
            if (!string.IsNullOrEmpty(skillSection))
                AppendRuntimeFact(runtimeFacts, skillSection);
        }

        var decoratedKernel = new KernelPromptLayer(
            base.DecorateSystemPrompt(basePrompt, turnCatalog),
            NyxIdChatSystemPrompt.Value.Provenance);
        var builtInFloor = _builtInPromptFloorProvider.GetFloor();
        var global = _systemSkillOverlayProvider
            ?.GetCurrent(SystemSkillOverlayRequest.DirectChat(CurrentTurnNyxIdAccessToken));
        var runtime = runtimeFacts.Length == 0
            ? null
            : new RuntimeFactsPromptLayer(
                runtimeFacts.ToString(),
                new RuntimeFactsPromptProvenance("nyxid-direct-runtime"));
        var result = SystemPromptLayerComposer.Compose(
            decoratedKernel,
            builtInFloor,
            global,
            turnCatalog?.ProfilePromptLayer,
            turnCatalog?.SelectedSkillPromptLayer,
            runtime,
            conversation: null);

        if (global is not null && _systemSkillOverlayPromptLogCounter++ % SystemSkillOverlayPromptLogSampleRate == 0)
        {
            Logger.LogInformation(
                "[{Role}] System prompt layers: global_watermark={GlobalWatermark}, kernel_tokens_estimate={KernelTokensEstimate}, floor_tokens_estimate={FloorTokensEstimate}, global_tokens_estimate={GlobalTokensEstimate}",
                RoleName,
                global.Provenance.SourceWatermark,
                result.Kernel.EstimatedTokens,
                result.BuiltInFloor.EstimatedTokens,
                result.Global.EstimatedTokens);
        }

        return result.Prompt;
    }

    protected override async Task<AgentProfileTurnAuthorityPreparation?> PrepareAgentProfileTurnAuthorityAsync(
        ChatRequestEvent request,
        AgentToolExecutionContext toolContext,
        CancellationToken ct)
    {
        var profile = State.AgentProfile;
        if (profile is null)
            return null;

        if (_turnCatalogMaterializer is null)
        {
            var unavailable = CreateFailClosedPreparation(
                request.SessionId,
                AgentProfileTurnDegradationReason.MaterializerUnavailable);
            RecordRouteDecision(unavailable, "materializer_unavailable", 0);
            return profile.ActivationMode == AgentProfileActivationMode.Shadow
                ? null
                : unavailable;
        }

        var startedTimestamp = _timeProvider.GetTimestamp();
        AgentProfileTurnAuthorityPreparation preparation;
        try
        {
            preparation = await _turnCatalogMaterializer.PrepareAsync(
                profile,
                request.SessionId,
                request.Prompt ?? string.Empty,
                Tools.GetAll(),
                toolContext,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Agent profile turn authority preparation failed closed.");
            preparation = CreateFailClosedPreparation(
                request.SessionId,
                AgentProfileTurnDegradationReason.MaterializationFailed);
        }

        RecordRouteDecision(
            preparation,
            "observed",
            _timeProvider.GetElapsedTime(startedTimestamp).TotalMilliseconds);
        return profile.ActivationMode == AgentProfileActivationMode.Shadow
            ? null
            : preparation;
    }

    protected override async Task<AgentProfileTurnCatalogMaterialization?> MaterializeCommittedAgentProfileTurnCatalogAsync(
        ChatRequestEvent request,
        AgentToolExecutionContext toolContext,
        AgentProfileTurnAuthorityState committedAuthority,
        CancellationToken ct)
    {
        var profile = State.AgentProfile;
        if (profile is null)
            return null;

        if (_turnCatalogMaterializer is null)
        {
            return CreateFailClosedMaterialization(
                committedAuthority,
                AgentProfileTurnDegradationReason.MaterializerUnavailable);
        }

        var startedTimestamp = _timeProvider.GetTimestamp();
        AgentProfileTurnCatalogMaterialization materialization;
        try
        {
            materialization = await _turnCatalogMaterializer.MaterializeCommittedAsync(
                profile,
                committedAuthority,
                toolContext.Credentials.NyxIdAccessToken,
                Tools.GetAll(),
                toolContext,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Committed agent profile turn materialization failed closed.");
            materialization = CreateFailClosedMaterialization(
                committedAuthority,
                AgentProfileTurnDegradationReason.MaterializationFailed);
        }

        RecordMaterialization(
            committedAuthority,
            materialization,
            _timeProvider.GetElapsedTime(startedTimestamp).TotalMilliseconds);
        return materialization;
    }

    protected override void OnPlanOrHandoffObserved(bool handoffPending)
    {
        if (_activeAgentProfileTelemetryContext is not { } context)
            return;

        AgentProfileTelemetry.RecordPlanOrHandoff(
            context,
            handoffPending ? "handoff_pending" : "completed",
            planStep: 0,
            ordinaryRecoveryCount: 0);
    }

    protected override void OnFirstStreamedOutputObserved(TimeSpan elapsed)
    {
        if (_activeAgentProfileTelemetryContext is not { } context)
            return;

        AgentProfileTelemetry.RecordFirstStreamedOutput(
            context,
            "ok",
            Math.Max(0, elapsed.TotalMilliseconds));
    }

    private void RecordRouteDecision(
        AgentProfileTurnAuthorityPreparation preparation,
        string outcome,
        double durationMs)
    {
        if (_activeAgentProfileTelemetryContext is not { } context)
            return;

        var diagnostics = preparation.Diagnostics;
        var routeDiagnostic = diagnostics.FirstOrDefault(static diagnostic => diagnostic.Code is
            AgentProfileTurnDiagnosticCode.AliasMatched or
            AgentProfileTurnDiagnosticCode.ClassifierMatched or
            AgentProfileTurnDiagnosticCode.ClassifierNoMatch or
            AgentProfileTurnDiagnosticCode.ClassifierFailed);
        var authority = preparation.Authority;
        var degradation = authority.DegradationReasons
            .FirstOrDefault(static reason => reason != AgentProfileTurnDegradationReason.Unspecified);
        var routingMode = routeDiagnostic?.Code switch
        {
            AgentProfileTurnDiagnosticCode.AliasMatched => "alias",
            AgentProfileTurnDiagnosticCode.ClassifierMatched or
                AgentProfileTurnDiagnosticCode.ClassifierNoMatch or
                AgentProfileTurnDiagnosticCode.ClassifierFailed => "classifier",
            _ => "none",
        };
        AgentProfileTelemetry.RecordRouteDecision(
            context,
            routingMode,
            authority.CandidateRoute?.IntentId ?? string.Empty,
            degradation == AgentProfileTurnDegradationReason.Unspecified
                ? outcome
                : degradation.ToString().ToLowerInvariant(),
            routeDiagnostic?.Code.ToString().ToLowerInvariant() ?? string.Empty,
            Math.Max(0, durationMs));
    }

    private void RecordMaterialization(
        AgentProfileTurnAuthorityState committedAuthority,
        AgentProfileTurnCatalogMaterialization materialization,
        double durationMs)
    {
        if (_activeAgentProfileTelemetryContext is not { } context)
            return;

        var catalog = materialization.Catalog;
        var selectedSkill = catalog.SelectedSkillPromptLayer;
        var outcome = selectedSkill is not null ? "ok" : "degraded";
        if (committedAuthority.SelectedExactSkillRef is { } selectedRef)
        {
            AgentProfileTelemetry.RecordExactFetch(
                context,
                selectedRef.Guid,
                selectedRef.LiteralVersion,
                outcome,
                Math.Max(0, durationMs));
        }

        AgentProfileTelemetry.RecordPromptAndToolMaterialization(
            context,
            selectedSkill is not null
                ? "selected_skill"
                : catalog.ProfilePromptLayer is not null ? "profile" : "recovery",
            selectedSkill?.ActualUtf8Bytes ?? 0,
            catalog.FinalAllowedToolNames.Count,
            outcome);
    }

    private static AgentProfileTurnAuthorityPreparation CreateFailClosedPreparation(
        string sessionId,
        AgentProfileTurnDegradationReason reason) =>
        AgentProfileTurnAuthorityPreparation.Create(new AgentProfileTurnAuthorityState
        {
            ReconciliationKey = new AgentProfileTurnReconciliationKey
            {
                SessionId = sessionId,
                Attempt = 1,
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.RestrictedEmpty,
            DegradationReasons = { reason },
        });

    private static AgentProfileTurnCatalogMaterialization CreateFailClosedMaterialization(
        AgentProfileTurnAuthorityState committedAuthority,
        AgentProfileTurnDegradationReason reason)
    {
        var proposal = committedAuthority.Clone();
        proposal.AuthorityKind = AgentProfileTurnAuthorityKind.RestrictedEmpty;
        proposal.AuthorityCeilingToolNames.Clear();
        proposal.DegradationReasons.Clear();
        proposal.DegradationReasons.Add(
            committedAuthority.DegradationReasons
                .Append(reason)
                .Where(static degradation => degradation != AgentProfileTurnDegradationReason.Unspecified)
                .Distinct()
                .OrderBy(static degradation => (int)degradation));
        return AgentProfileTurnCatalogMaterialization.Create(
            new AgentProfileTurnCatalog(
                [],
                profilePromptLayer: null,
                selectedSkillPromptLayer: null,
                selectedIntentId: null,
                candidateIntentId: committedAuthority.CandidateRoute?.IntentId),
            proposal);
    }

    public override async Task HandleChatRequest(ChatRequestEvent request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var telemetryContext = State.AgentProfile is { } profile
            ? CreateTelemetryContext(profile)
            : null;
        using var telemetryActivity = telemetryContext is null
            ? null
            : AgentProfileTelemetry.StartTurn(telemetryContext);
        _activeAgentProfileTelemetryContext = telemetryContext;
        try
        {
            await base.HandleChatRequest(request);
        }
        finally
        {
            _activeAgentProfileTelemetryContext = null;
        }
    }

    private static AgentProfileTelemetryContext CreateTelemetryContext(AgentProfileSnapshot profile) =>
        new(
            profile.ProfileId,
            profile.ProfileVersion,
            profile.PolicyRevision,
            Convert.ToHexString(profile.DeterministicPolicySha256.Span).ToLowerInvariant(),
            profile.ActivationMode.ToString().ToLowerInvariant(),
            profile.SkillsetProvenance?.Guid ?? string.Empty,
            profile.SkillsetProvenance?.LiteralVersion ?? string.Empty);

    private static void AppendRuntimeFact(System.Text.StringBuilder builder, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;
        if (builder.Length > 0)
            builder.Append("\n\n");
        builder.Append(content.Trim());
    }

    private bool RequiresNyxIdProviderMigration()
    {
        var overrides = State.ConfigOverrides;
        return overrides == null ||
               !overrides.HasProviderName ||
               string.IsNullOrWhiteSpace(overrides.ProviderName);
    }

    private InitializeRoleAgentEvent BuildInitializeRoleAgentEvent(string roleName)
    {
        // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
        //   Old pattern: role initialization copied StreamBufferCapacity overrides into the ChatRuntime config surface.
        //   New principle: stream buffering is not a role-level business option; the actor initializes only stable role semantics.
        var initializeEvent = new InitializeRoleAgentEvent
        {
            RoleName = string.IsNullOrWhiteSpace(roleName)
                ? NyxIdChatServiceDefaults.DisplayName
                : roleName.Trim(),
            ProviderName = NyxIdChatServiceDefaults.ProviderName,
            SystemPrompt = NyxIdChatSystemPrompt.Value.Content,
            MaxToolRounds = State.ConfigOverrides?.HasMaxToolRounds == true &&
                            State.ConfigOverrides.MaxToolRounds > 0
                ? State.ConfigOverrides.MaxToolRounds
                : 0,
            EventModules = State.EventModules ?? string.Empty,
            EventRoutes = State.EventRoutes ?? string.Empty,
        };

        var overrides = State.ConfigOverrides;
        if (overrides?.HasModel == true)
            initializeEvent.Model = overrides.Model;

        if (overrides?.HasTemperature == true)
            initializeEvent.Temperature = overrides.Temperature;

        if (overrides?.HasMaxTokens == true && overrides.MaxTokens > 0)
            initializeEvent.MaxTokens = overrides.MaxTokens;

        if (overrides?.HasMaxHistoryMessages == true && overrides.MaxHistoryMessages > 0)
            initializeEvent.MaxHistoryMessages = overrides.MaxHistoryMessages;
        return initializeEvent;
    }

    protected override async Task OnRoleChatSessionTerminalCommittedAsync(
        string sessionId,
        CancellationToken ct)
    {
        await TryRequestDirectChatHistoryDeliveryAsync(sessionId, ct);
    }

    private async Task RequestPendingDirectChatHistoryDeliveryAsync(CancellationToken ct)
    {
        var pendingSessionIds = State.Sessions
            .Where(static entry =>
                entry.Value.Completed &&
                entry.Value.HistoryDeliveryStatus == RoleChatHistoryDeliveryStatus.Prepared)
            .OrderBy(static entry => entry.Value.Sequence)
            .Select(static entry => entry.Key)
            .ToArray();

        foreach (var sessionId in pendingSessionIds)
            await TryRequestDirectChatHistoryDeliveryAsync(sessionId, ct);
    }

    private async Task TryRequestDirectChatHistoryDeliveryAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            await RequestDirectChatHistoryDeliveryAsync(sessionId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "NyxID direct-chat history delivery request remains pending. actor={ActorId} session={SessionId}",
                Id,
                sessionId);
        }
    }

    private Task RequestDirectChatHistoryDeliveryAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !State.Sessions.TryGetValue(sessionId, out var session) ||
            session.HistoryDeliveryStatus != RoleChatHistoryDeliveryStatus.Prepared)
        {
            return Task.CompletedTask;
        }

        return PublishAsync(new NyxIdDirectChatHistoryDeliveryRequested
        {
            SessionId = sessionId,
            DeliveryId = session.HistoryDeliveryId,
            ExpectedAttempt = session.HistoryDeliveryAttempt,
        }, TopologyAudience.Self, ct);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleDirectChatHistoryDeliveryRequestedAsync(
        NyxIdDirectChatHistoryDeliveryRequested request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SessionId) ||
            !State.Sessions.TryGetValue(request.SessionId, out var completedSession) ||
            !completedSession.Completed ||
            completedSession.HistoryDeliveryStatus != RoleChatHistoryDeliveryStatus.Prepared ||
            string.IsNullOrWhiteSpace(completedSession.ScopeId) ||
            string.IsNullOrWhiteSpace(completedSession.HistoryDeliveryId) ||
            !string.Equals(completedSession.HistoryDeliveryId, request.DeliveryId, StringComparison.Ordinal) ||
            completedSession.HistoryDeliveryAttempt != request.ExpectedAttempt)
        {
            return;
        }

        var sessionId = request.SessionId;
        var prompt = completedSession.Prompt ?? string.Empty;
        var completion = completedSession.FinalContent ?? string.Empty;
        var assistantStatus = completedSession.Outcome switch
        {
            RoleChatSessionOutcome.Blocked => "blocked",
            RoleChatSessionOutcome.Failed => "error",
            RoleChatSessionOutcome.OutcomeUncertain => "outcome_uncertain",
            _ => "completed",
        };
        var safeError = completedSession.Outcome switch
        {
            RoleChatSessionOutcome.Blocked => completedSession.AuthorizationRequired?.SafeMessage,
            RoleChatSessionOutcome.Failed or RoleChatSessionOutcome.OutcomeUncertain => completedSession.SafeMessage,
            _ => null,
        };
        var archivedCompletion = completedSession.Outcome is
            RoleChatSessionOutcome.Blocked or
            RoleChatSessionOutcome.Failed or
            RoleChatSessionOutcome.OutcomeUncertain
            ? string.IsNullOrWhiteSpace(safeError)
                ? "The chat request failed. Please try again."
                : safeError
            : completion;
        var completedAt = completedSession.TerminalTime?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch;
        var timestamp = completedAt.ToUnixTimeMilliseconds();
        var messages = new[]
        {
            new StoredChatMessage(
                Id: $"{sessionId}-user",
                Role: "user",
                Content: prompt,
                Timestamp: timestamp,
                Status: "completed",
                TurnId: sessionId),
            new StoredChatMessage(
                Id: $"{sessionId}-assistant",
                Role: "assistant",
                Content: archivedCompletion,
                Timestamp: timestamp,
                Status: assistantStatus,
                Error: string.IsNullOrWhiteSpace(safeError) ? null : safeError,
                Thinking: string.IsNullOrWhiteSpace(completedSession.FinalReasoningContent)
                    ? null
                    : completedSession.FinalReasoningContent,
                TurnId: sessionId),
        };
        var meta = new ConversationMeta(
            Id: Id,
            Title: BuildConversationTitle(prompt, completion, Id),
            ServiceId: Id,
            ServiceKind: NyxIdChatServiceDefaults.GAgentKind,
            CreatedAt: completedAt,
            UpdatedAt: completedAt,
            MessageCount: messages.Length,
            LlmRoute: NyxIdChatServiceDefaults.ProviderName,
            LlmModel: string.IsNullOrWhiteSpace(completedSession.Model) ? null : completedSession.Model);

        try
        {
            await Services.GetRequiredService<IChatHistoryCommandPort>()
                .SaveMessagesAsync(completedSession.ScopeId, Id, meta, messages, CancellationToken.None)
                .ConfigureAwait(false);

            await PersistDomainEventAsync(new NyxIdDirectChatHistoryDispatchedEvent
            {
                SessionId = sessionId,
                DeliveryId = completedSession.HistoryDeliveryId,
                Attempt = NextHistoryDeliveryAttempt(completedSession.HistoryDeliveryAttempt),
                DispatchedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            }, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "NyxID direct-chat history delivery remains pending. actor={ActorId} session={SessionId}",
                Id,
                sessionId);
        }
    }

    private static string BuildConversationTitle(string prompt, string completion, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(prompt) ? completion : prompt;
        source = source.Trim();
        if (string.IsNullOrWhiteSpace(source))
            return fallback;

        const int maxTitleLength = 80;
        return source.Length <= maxTitleLength
            ? source
            : source[..maxTitleLength].TrimEnd();
    }

    protected override RoleGAgentState TransitionState(RoleGAgentState current, IMessage evt)
    {
        var next = base.TransitionState(current, evt);
        if (StateTransitionMatcher.TryExtract<RoleChatSessionCompletedEvent>(evt, out var completed))
            next = PrepareDirectChatHistoryDelivery(current, next, completed);
        if (StateTransitionMatcher.TryExtract<NyxIdDirectChatHistoryDispatchedEvent>(evt, out var dispatched))
            next = ApplyDirectChatHistoryDispatched(next, dispatched);

        if (!StateTransitionMatcher.TryExtract<AgentProfileBoundEvent>(evt, out var profileBound))
            return next;

        if (profileBound.Profile is null)
            throw new InvalidOperationException("Agent profile binding events require a complete snapshot.");

        if (!AgentProfileSnapshotCodec.Verify(profileBound.Profile))
            throw new InvalidOperationException("Agent profile binding events require a valid digest.");

        if (next.AgentProfile is not null)
        {
            if (!AgentProfileSnapshotCodec.ByteEquivalent(next.AgentProfile, profileBound.Profile))
                throw new InvalidOperationException("Committed agent profile bindings cannot be replaced.");
            return next;
        }

        var profileNext = next.Clone();
        profileNext.AgentProfile = profileBound.Profile.Clone();
        return profileNext;
    }

    private RoleGAgentState PrepareDirectChatHistoryDelivery(
        RoleGAgentState current,
        RoleGAgentState next,
        RoleChatSessionCompletedEvent completed)
    {
        var sessionId = completed.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !next.Sessions.TryGetValue(sessionId, out var session) ||
            string.IsNullOrWhiteSpace(session.ScopeId))
        {
            return next;
        }

        var hasPrevious = current.Sessions.TryGetValue(sessionId, out var previous);
        var isInitialTerminal = previous is not { Completed: true };
        var isExplicitReconciliation = previous is
            {
                Completed: true,
                Outcome: RoleChatSessionOutcome.OutcomeUncertain,
                HistoryDeliveryStatus: RoleChatHistoryDeliveryStatus.Dispatched,
            } &&
            session.Outcome is RoleChatSessionOutcome.Completed or RoleChatSessionOutcome.Failed;
        if (!isInitialTerminal && !isExplicitReconciliation)
            return next;

        var prepared = next.Clone();
        var nextSession = prepared.Sessions[sessionId];
        nextSession.HistoryDeliveryStatus = RoleChatHistoryDeliveryStatus.Prepared;
        if (!hasPrevious || string.IsNullOrWhiteSpace(nextSession.HistoryDeliveryId))
            nextSession.HistoryDeliveryId = BuildDirectChatHistoryDeliveryId(sessionId);
        return prepared;
    }

    private static RoleGAgentState ApplyDirectChatHistoryDispatched(
        RoleGAgentState state,
        NyxIdDirectChatHistoryDispatchedEvent dispatched)
    {
        if (string.IsNullOrWhiteSpace(dispatched.SessionId) ||
            !state.Sessions.TryGetValue(dispatched.SessionId, out var session) ||
            session.HistoryDeliveryStatus != RoleChatHistoryDeliveryStatus.Prepared ||
            !string.Equals(session.HistoryDeliveryId, dispatched.DeliveryId, StringComparison.Ordinal) ||
            dispatched.Attempt != NextHistoryDeliveryAttempt(session.HistoryDeliveryAttempt))
        {
            return state;
        }

        var next = state.Clone();
        var nextSession = next.Sessions[dispatched.SessionId];
        nextSession.HistoryDeliveryStatus = RoleChatHistoryDeliveryStatus.Dispatched;
        nextSession.HistoryDeliveryAttempt = dispatched.Attempt;
        return next;
    }

    private string BuildDirectChatHistoryDeliveryId(string sessionId) =>
        $"nyxid-direct-chat-history:{Id}:{sessionId}";

    private static int NextHistoryDeliveryAttempt(int currentAttempt) =>
        currentAttempt == int.MaxValue ? int.MaxValue : currentAttempt + 1;
}
