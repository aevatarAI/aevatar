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
[GAgent(NyxIdChatServiceDefaults.GAgentKind)]
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
        AgentProfileTurnCatalogMaterializer? turnCatalogMaterializer = null)
        : base(llmProviderFactory, additionalHooks, agentMiddlewares, llmMiddlewares, toolSources,
               remoteToolApprovalPort: remoteToolApprovalPort,
               remoteToolApprovalNotificationPort: remoteToolApprovalNotificationPort)
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

    // Refactor (iter47/issue-877-chat-endpoints-own-lifecycle-and-compensation):
    //   Old pattern: Chat endpoints owned actor lifecycle, registry compensation, participant orchestration, terminal-state recovery, and chat history command-port side effects.
    //   New principle: Endpoint is adapter-only (HTTP/SSE); typed command facade owns lifecycle; existing chat actors own compensation events and terminal-state publication.
    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleCreationCompensationAsync(
        NyxIdChatConversationCreationCompensationRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var registryCommandPort = Services.GetRequiredService<IGAgentActorRegistryCommandPort>();
        try
        {
            await registryCommandPort.UnregisterActorAsync(
                new GAgentActorRegistration(
                    command.ScopeId,
                    NyxIdChatServiceDefaults.GAgentKind,
                    command.ActorId),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to unregister NyxID chat conversation during actor-owned compensation: scope={ScopeId}, actor={ActorId}",
                command.ScopeId,
                command.ActorId);
            return;
        }

        if (!command.DestroyActor)
            return;

        try
        {
            await Services.GetRequiredService<IActorRuntime>()
                .DestroyAsync(command.ActorId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to destroy NyxID chat actor during actor-owned compensation: actor={ActorId}",
                command.ActorId);
        }
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleCreateConversationAsync(
        NyxIdChatConversationCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Refactor (iter77/cluster-077-cqrs-command-outcome-stream-rpc):
        //   Old pattern: NyxIdChat create awaited actor outcome via stream-RPC primitive (DispatchAndAwaitOutcomeAsync)
        //   New principle (narrow scope): NyxIdChat create returns honest accepted ACK; terminal facts via committed events
        var commandId = ActiveInboundEnvelope?.Id ?? string.Empty;
        var correlationId = ActiveInboundEnvelope?.Propagation?.CorrelationId ?? commandId;
        var registryCommandPort = Services.GetRequiredService<IGAgentActorRegistryCommandPort>();
        var createdLocally = command.CreatedLocally;

        await BindAgentProfileAsync(command.AgentProfile);

        await PersistDomainEventAsync(new NyxIdChatConversationCreationStartedEvent
        {
            ScopeId = command.ScopeId,
            ActorId = Id,
            CreatedLocally = createdLocally,
            CommandId = commandId,
            CorrelationId = correlationId,
        });

        try
        {
            var receipt = await registryCommandPort.RegisterActorAsync(
                new GAgentActorRegistration(command.ScopeId, NyxIdChatServiceDefaults.GAgentKind, Id),
                CancellationToken.None);
            if (receipt.IsAdmissionVisible)
            {
                await PersistDomainEventAsync(new NyxIdChatConversationRegistrationAcceptedEvent
                {
                    ScopeId = command.ScopeId,
                    ActorId = Id,
                    CommandId = commandId,
                    CorrelationId = correlationId,
                });
                return;
            }

            await PersistRegistrationUnavailableAndCompensateAsync(
                command.ScopeId,
                Id,
                createdLocally,
                "registration_not_admission_visible",
                commandId,
                correlationId);
        }
        catch
        {
            await PersistRegistrationUnavailableAndCompensateAsync(
                command.ScopeId,
                Id,
                createdLocally,
                "registration_failed",
                commandId,
                correlationId);
        }
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleDeleteConversationAsync(
        NyxIdChatConversationDeleteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!string.Equals(Id, command.ActorId, StringComparison.Ordinal))
            return;

        var commandId = ActiveInboundEnvelope?.Id ?? string.Empty;
        var correlationId = ActiveInboundEnvelope?.Propagation?.CorrelationId ?? commandId;
        var registryCommandPort = Services.GetRequiredService<IGAgentActorRegistryCommandPort>();
        var chatHistoryCommandPort = Services.GetRequiredService<IChatHistoryCommandPort>();

        await PersistDomainEventAsync(new NyxIdChatConversationDeletionStartedEvent
        {
            ScopeId = command.ScopeId,
            ActorId = command.ActorId,
            CommandId = commandId,
            CorrelationId = correlationId,
        });

        await registryCommandPort.UnregisterActorAsync(
            new GAgentActorRegistration(command.ScopeId, NyxIdChatServiceDefaults.GAgentKind, command.ActorId),
            CancellationToken.None);
        await PersistDomainEventAsync(new NyxIdChatConversationUnregisteredEvent
        {
            ScopeId = command.ScopeId,
            ActorId = command.ActorId,
            CommandId = commandId,
            CorrelationId = correlationId,
        });

        try
        {
            await chatHistoryCommandPort.DeleteConversationAsync(command.ScopeId, command.ActorId, CancellationToken.None);
            await PersistDomainEventAsync(new NyxIdChatConversationHistoryDeletedEvent
            {
                ScopeId = command.ScopeId,
                ActorId = command.ActorId,
                CommandId = commandId,
                CorrelationId = correlationId,
            });
        }
        catch
        {
            await PersistDomainEventAsync(new NyxIdChatConversationDeletionCompensationStartedEvent
            {
                ScopeId = command.ScopeId,
                ActorId = command.ActorId,
                Reason = "history_delete_failed",
                CommandId = commandId,
                CorrelationId = correlationId,
            });
            await HandleDeletionCompensationAsync(new NyxIdChatConversationDeletionCompensationRequested
            {
                ScopeId = command.ScopeId,
                ActorId = command.ActorId,
                Reason = "history_delete_failed",
            });
            throw;
        }
    }

    // Refactor (iter47/issue-877-chat-endpoints-own-lifecycle-and-compensation):
    //   Old pattern: Chat endpoints owned actor lifecycle, registry compensation, participant orchestration, terminal-state recovery, and chat history command-port side effects.
    //   New principle: Endpoint is adapter-only (HTTP/SSE); typed command facade owns lifecycle; existing chat actors own compensation events and terminal-state publication.
    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleDeletionCompensationAsync(
        NyxIdChatConversationDeletionCompensationRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            await Services.GetRequiredService<IGAgentActorRegistryCommandPort>()
                .RegisterActorAsync(
                    new GAgentActorRegistration(
                        command.ScopeId,
                        NyxIdChatServiceDefaults.GAgentKind,
                        command.ActorId),
                    CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Failed to restore NyxID chat conversation registration during actor-owned compensation: scope={ScopeId}, actor={ActorId}",
                command.ScopeId,
                command.ActorId);
        }
    }

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
            await SaveDirectChatCompletionAsync(request, CancellationToken.None);
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

    private async Task BindAgentProfileAsync(AgentProfileSnapshot? profile)
    {
        var boundProfile = State.AgentProfile;
        if (profile is null)
        {
            if (boundProfile is not null)
                throw new InvalidOperationException("A bound agent profile cannot be removed from a conversation.");
            return;
        }

        if (!AgentProfileSnapshotCodec.Verify(profile))
            throw new InvalidOperationException("The agent profile snapshot digest is invalid.");

        if (boundProfile is null)
        {
            await PersistDomainEventAsync(new AgentProfileBoundEvent { Profile = profile.Clone() });
            return;
        }

        if (!AgentProfileSnapshotCodec.ByteEquivalent(boundProfile, profile))
            throw new InvalidOperationException("A conversation cannot replace its bound agent profile.");
    }

    private async Task PersistRegistrationUnavailableAndCompensateAsync(
        string scopeId,
        string actorId,
        bool destroyActor,
        string reason,
        string commandId,
        string correlationId)
    {
        await PersistDomainEventAsync(new NyxIdChatConversationRegistrationUnavailableEvent
        {
            ScopeId = scopeId,
            ActorId = actorId,
            DestroyActor = destroyActor,
            Reason = reason,
            CommandId = commandId,
            CorrelationId = correlationId,
        });
        await HandleCreationCompensationAsync(new NyxIdChatConversationCreationCompensationRequested
        {
            ScopeId = scopeId,
            ActorId = actorId,
            DestroyActor = destroyActor,
            Reason = reason,
        });
    }

    private async Task SaveDirectChatCompletionAsync(ChatRequestEvent request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ScopeId) ||
            string.IsNullOrWhiteSpace(request.SessionId) ||
            !State.Sessions.TryGetValue(request.SessionId, out var completedSession) ||
            !completedSession.Completed)
        {
            return;
        }

        var prompt = request.Prompt ?? completedSession.Prompt ?? string.Empty;
        var completion = completedSession.FinalContent ?? string.Empty;
        var completedAt = _timeProvider.GetUtcNow();
        var timestamp = completedAt.ToUnixTimeMilliseconds();
        var messages = new[]
        {
            new StoredChatMessage(
                Id: $"{request.SessionId}-user",
                Role: "user",
                Content: prompt,
                Timestamp: timestamp,
                Status: "completed"),
            new StoredChatMessage(
                Id: $"{request.SessionId}-assistant",
                Role: "assistant",
                Content: completion,
                Timestamp: timestamp,
                Status: "completed",
                Thinking: string.IsNullOrWhiteSpace(completedSession.FinalReasoningContent)
                    ? null
                    : completedSession.FinalReasoningContent),
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

        await Services.GetRequiredService<IChatHistoryCommandPort>()
            .SaveMessagesAsync(request.ScopeId, Id, meta, messages, ct)
            .ConfigureAwait(false);
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
        if (!StateTransitionMatcher.TryExtract<AgentProfileBoundEvent>(evt, out var profileBound))
            return base.TransitionState(current, evt);

        if (profileBound.Profile is null)
            throw new InvalidOperationException("Agent profile binding events require a complete snapshot.");

        if (!AgentProfileSnapshotCodec.Verify(profileBound.Profile))
            throw new InvalidOperationException("Agent profile binding events require a valid digest.");

        if (current.AgentProfile is not null)
        {
            if (!AgentProfileSnapshotCodec.ByteEquivalent(current.AgentProfile, profileBound.Profile))
                throw new InvalidOperationException("Committed agent profile bindings cannot be replaced.");
            return current;
        }

        var next = current.Clone();
        next.AgentProfile = profileBound.Profile.Clone();
        return next;
    }
}
