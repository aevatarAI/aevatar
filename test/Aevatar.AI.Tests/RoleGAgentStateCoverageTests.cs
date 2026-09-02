using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.VoicePresence.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Tests;

public sealed partial class RoleGAgentStateCoverageTests
{
    private static readonly MethodInfo ApplyClearPendingApprovalMethod = typeof(RoleGAgent)
        .GetMethod("ApplyClearPendingApproval", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ApplyClearPendingApproval not found.");
    private static readonly MethodInfo ApplyChatSessionStartedMethod = typeof(RoleGAgent)
        .GetMethod("ApplyChatSessionStarted", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ApplyChatSessionStarted not found.");
    private static readonly MethodInfo ApplyChatSessionCompletedMethod = typeof(RoleGAgent)
        .GetMethod("ApplyChatSessionCompleted", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ApplyChatSessionCompleted not found.");
    private static readonly MethodInfo ResolveRequestInputPartsMethod = typeof(RoleGAgent)
        .GetMethod("ResolveRequestInputParts", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ResolveRequestInputParts not found.");
    private static readonly MethodInfo BuildRequestLogSummaryMethod = typeof(RoleGAgent)
        .GetMethod("BuildRequestLogSummary", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildRequestLogSummary not found.");
    private static readonly MethodInfo BuildContinuationPromptMethod = typeof(RoleGAgent)
        .GetMethod("BuildContinuationPrompt", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildContinuationPrompt not found.");
    private static readonly MethodInfo ApplyPendingApprovalMethod = typeof(RoleGAgent)
        .GetMethod("ApplyPendingApproval", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ApplyPendingApproval not found.");
    private static readonly MethodInfo DetectPendingApprovalMethod = typeof(RoleGAgent)
        .GetMethod("DetectPendingApproval", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("DetectPendingApproval not found.");
    private static readonly MethodInfo ApplyRemoteApprovalSubmittedMethod = typeof(RoleGAgent)
        .GetMethod("ApplyRemoteApprovalSubmitted", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ApplyRemoteApprovalSubmitted not found.");
    private static readonly MethodInfo ApplyVoicePresenceRuntimeStateChangedMethod = typeof(RoleGAgent)
        .GetMethod("ApplyVoicePresenceRuntimeStateChanged", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ApplyVoicePresenceRuntimeStateChanged not found.");
    private static readonly MethodInfo SanitizeFailureMessageMethod = typeof(RoleGAgent)
        .GetMethod("SanitizeFailureMessage", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SanitizeFailureMessage not found.");
    private static readonly MethodInfo ResolveTrackedSessionMethod = typeof(RoleGAgent)
        .GetMethod("ResolveTrackedSession", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("ResolveTrackedSession not found.");
    private static readonly MethodInfo ExtractStateConfigOverridesMethod = typeof(RoleGAgent)
        .GetMethod("ExtractStateConfigOverrides", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("ExtractStateConfigOverrides not found.");
    private static readonly MethodInfo ApplyAgentProfileTurnAuthorityCommittedMethod = typeof(RoleGAgent).GetMethod(
        "ApplyAgentProfileTurnAuthorityCommitted", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ApplyAgentProfileTurnAuthorityCommitted not found.");
    [Fact]
    public void ApplyTurnAuthorityInitial_ShouldReplaceOnlyForNewActiveSession()
    {
        var current = StateWithIncompleteAuthority(TurnAuthority("session-old", 1, "intent-a", "skill-a"));
        current.MessageCount = 2;
        current.Sessions.Add("session-older", new RoleChatSessionState { Sequence = 0 });
        current.Sessions.Add("session-same", new RoleChatSessionState { Sequence = 1 });
        current.Sessions.Add("session-new", new RoleChatSessionState { Sequence = 2 });
        void AssertRejected(AgentProfileTurnAuthorityState authority) =>
            ApplyAuthority(current, AgentProfileTurnAuthorityCommitKind.Initial, authority).Should().BeSameAs(current);
        AssertRejected(current.AgentProfileTurnAuthority.Clone());
        AssertRejected(MutateAuthority(current, authority => authority.CandidateRoute.IntentId = "intent-mutated"));
        foreach (var staleSessionId in new[] { "session-older", "session-same" })
            AssertRejected(TurnAuthority(staleSessionId, 1, "intent-a", "skill-a"));
        var initial = TurnAuthority("session-new", 1, "intent-a", "skill-a");
        initial.AuthorityCeilingToolNames.Clear();
        initial.AuthorityCeilingToolNames.Add([" task ", "Search", "TASK"]);
        initial.DegradationReasons.Add([AgentProfileTurnDegradationReason.ExactSkillFetchFailed, AgentProfileTurnDegradationReason.ClassifierFailed, AgentProfileTurnDegradationReason.ExactSkillFetchFailed]);
        var next = ApplyAuthority(current, AgentProfileTurnAuthorityCommitKind.Initial, initial);
        next.Should().NotBeSameAs(current);
        next.AgentProfileTurnAuthority.ReconciliationKey.SessionId.Should().Be("session-new");
        next.AgentProfileTurnAuthority.AuthorityCeilingToolNames.Should().Equal("Search", "task");
        next.AgentProfileTurnAuthority.DegradationReasons.Should().Equal(
            AgentProfileTurnDegradationReason.ClassifierFailed,
            AgentProfileTurnDegradationReason.ExactSkillFetchFailed);
        ApplyAuthority(next, AgentProfileTurnAuthorityCommitKind.Initial, MutateAuthority(next, authority => authority.ReconciliationKey.Attempt = 2)).Should().BeSameAs(next);
    }
    [Fact]
    public void ApplyTurnAuthorityRetryStarted_ShouldAdvanceExactlyOneAttemptAndFreezeCandidate()
    {
        var current = StateWithIncompleteAuthority(TurnAuthority("session-a", 1, "intent-a", "skill-a"));
        var retry = current.AgentProfileTurnAuthority.Clone();
        retry.ReconciliationKey.Attempt = 2;
        var next = ApplyAuthority(current, AgentProfileTurnAuthorityCommitKind.RetryStarted, retry);
        next.AgentProfileTurnAuthority.ReconciliationKey.Attempt.Should().Be(2);
        next.AgentProfileTurnAuthority.CandidateRoute.Should()
            .BeEquivalentTo(current.AgentProfileTurnAuthority.CandidateRoute);
        next.AgentProfileTurnAuthority.SelectedExactSkillRef.Should()
            .BeEquivalentTo(current.AgentProfileTurnAuthority.SelectedExactSkillRef);
        next.AgentProfileTurnAuthority.AuthorityKind.Should().Be(current.AgentProfileTurnAuthority.AuthorityKind);
        Action<AgentProfileTurnAuthorityState>[] mutations =
        [
            authority => authority.CandidateRoute.IntentId = "intent-mutated",
            authority => authority.SelectedExactSkillRef.LiteralVersion = "9.9.9",
            authority => authority.AuthorityKind = AgentProfileTurnAuthorityKind.Recovery,
            authority => authority.AuthorityCeilingToolNames.Add("hidden"),
            authority => authority.DegradationReasons.Add(AgentProfileTurnDegradationReason.ClassifierFailed),
        ];
        foreach (var mutate in mutations)
        {
            var mutatedRetry = retry.Clone();
            mutate(mutatedRetry);
            ApplyAuthority(current, AgentProfileTurnAuthorityCommitKind.RetryStarted, mutatedRetry)
                .Should().BeSameAs(current);
        }
        var gap = retry.Clone();
        gap.ReconciliationKey.Attempt = 4;
        ApplyAuthority(current, AgentProfileTurnAuthorityCommitKind.RetryStarted, gap).Should().BeSameAs(current);
    }
    [Fact]
    public void ApplyTurnAuthority_ShouldRejectWrongSessionAttemptLateAndMutatingEvents()
    {
        var current = StateWithIncompleteAuthority(TurnAuthority("session-a", 2, "intent-a", "skill-a"));
        var invalidAuthorities = new[] {
            MutateAuthority(current, authority => authority.ReconciliationKey.SessionId = "session-b"),
            MutateAuthority(current, authority => authority.ReconciliationKey.Attempt = 1),
            MutateAuthority(current, authority => authority.CandidateRoute.IntentId = "intent-b"),
            MutateAuthority(current, authority => authority.SelectedExactSkillRef.LiteralVersion = "9.9.9") };
        foreach (var invalidAuthority in invalidAuthorities)
            ApplyAuthority(current, AgentProfileTurnAuthorityCommitKind.Reconcile, invalidAuthority)
                .Should().BeSameAs(current);
        var completed = current.Clone();
        completed.Sessions["session-a"].Completed = true;
        ApplyAuthority(completed, AgentProfileTurnAuthorityCommitKind.Reconcile, completed.AgentProfileTurnAuthority.Clone())
            .Should().BeSameAs(completed);
    }
    [Fact]
    public void ApplyTurnAuthorityReconcile_ShouldBeIdempotentForDuplicateAndReplay()
    {
        var current = StateWithIncompleteAuthority(TurnAuthority("session-a", 1, "intent-a", "skill-a"));
        var reconcile = current.AgentProfileTurnAuthority.Clone();
        reconcile.AuthorityCeilingToolNames.Add("TASK");
        reconcile.DegradationReasons.Add(AgentProfileTurnDegradationReason.ClassifierFailed);
        var first = ApplyAuthority(current, AgentProfileTurnAuthorityCommitKind.Reconcile, reconcile);
        var second = ApplyAuthority(first, AgentProfileTurnAuthorityCommitKind.Reconcile, reconcile);
        second.Should().BeEquivalentTo(first);
        second.AgentProfileTurnAuthority.AuthorityCeilingToolNames.Should().Equal("recovery", "task");
        second.AgentProfileTurnAuthority.DegradationReasons.Should().Equal(
            AgentProfileTurnDegradationReason.ClassifierFailed);
    }
    [Fact]
    public void ApplyTurnAuthorityReconcile_ShouldOnlyAttenuateAndUnionReasons()
    {
        var authority = TurnAuthority("session-a", 1, "intent-a", "skill-a");
        authority.AuthorityCeilingToolNames.Clear();
        authority.AuthorityCeilingToolNames.Add(["a", "B", "task"]);
        authority.DegradationReasons.Add(AgentProfileTurnDegradationReason.ClassifierFailed);
        var current = StateWithIncompleteAuthority(authority);
        var attenuated = authority.Clone();
        attenuated.AuthorityKind = AgentProfileTurnAuthorityKind.Recovery;
        attenuated.AuthorityCeilingToolNames.Clear();
        attenuated.AuthorityCeilingToolNames.Add(["task", "A"]);
        attenuated.DegradationReasons.Clear();
        attenuated.DegradationReasons.Add(AgentProfileTurnDegradationReason.ExactSkillFetchFailed);
        var next = ApplyAuthority(current, AgentProfileTurnAuthorityCommitKind.Reconcile, attenuated);
        next.AgentProfileTurnAuthority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        next.AgentProfileTurnAuthority.AuthorityCeilingToolNames.Should().Equal("A", "task");
        next.AgentProfileTurnAuthority.DegradationReasons.Should().Equal(
            AgentProfileTurnDegradationReason.ClassifierFailed,
            AgentProfileTurnDegradationReason.ExactSkillFetchFailed);
        var wideningKind = MutateAuthority(next, value => value.AuthorityKind = AgentProfileTurnAuthorityKind.Selected);
        var wideningCeiling = MutateAuthority(next, value => value.AuthorityCeilingToolNames.Add("hidden"));
        var reasonRemoval = MutateAuthority(next, value => value.DegradationReasons.Clear());
        ApplyAuthority(next, AgentProfileTurnAuthorityCommitKind.Reconcile, wideningKind).Should().BeSameAs(next);
        ApplyAuthority(next, AgentProfileTurnAuthorityCommitKind.Reconcile, wideningCeiling).Should().BeSameAs(next);
        ApplyAuthority(next, AgentProfileTurnAuthorityCommitKind.Reconcile, reasonRemoval)
            .AgentProfileTurnAuthority.DegradationReasons.Should().Equal(
                AgentProfileTurnDegradationReason.ClassifierFailed,
                AgentProfileTurnDegradationReason.ExactSkillFetchFailed);
    }
    [Fact]
    public void ApplyClearPendingApproval_ShouldHandleMissingMismatchAndMatchBranches()
    {
        var empty = new RoleGAgentState();
        InvokePrivateStatic<RoleGAgentState>(
            ApplyClearPendingApprovalMethod,
            empty,
            new ClearPendingApprovalEvent { RequestId = "req-1" })
            .Should()
            .BeSameAs(empty);
        var state = new RoleGAgentState
        {
            PendingApproval = new PendingToolApprovalState
            {
                RequestId = "req-1",
            },
        };
        var mismatched = InvokePrivateStatic<RoleGAgentState>(
            ApplyClearPendingApprovalMethod,
            state,
            new ClearPendingApprovalEvent { RequestId = "req-2" });
        mismatched.PendingApproval.Should().NotBeNull();
        var cleared = InvokePrivateStatic<RoleGAgentState>(
            ApplyClearPendingApprovalMethod,
            state,
            new ClearPendingApprovalEvent());
        cleared.PendingApproval.Should().BeNull();
    }
    [Fact]
    public void ApplyPendingApproval_ShouldStorePendingState()
    {
        var pending = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "turn-original",
            ScopeId = "scope-a",
            ToolName = "dangerous_tool",
        };
        var next = InvokePrivateStatic<RoleGAgentState>(
            ApplyPendingApprovalMethod,
            new RoleGAgentState(),
            new PendingToolApprovalPersistedEvent
            {
                Pending = pending,
            });
        next.PendingApproval.Should().NotBeNull();
        next.PendingApproval!.RequestId.Should().Be("req-1");
        next.PendingApproval.ToolName.Should().Be("dangerous_tool");
    }
    [Fact]
    public void DetectPendingApproval_ShouldKeepApprovalAndOriginalRequestIdentitiesSeparate()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-detect-pending");
        var toolCalls = new[]
        {
            new ToolCall { Id = "call-1", Name = "dangerous_tool", ArgumentsJson = "{}" },
        };
        var toolReceipts = new[]
        {
            new AgentToolReceipt
            {
                CallId = "call-1",
                ToolName = "dangerous_tool",
                Status = AgentToolReceiptStatus.ApprovalRequired,
                ApprovalRequestId = "approval-1",
            },
        };

        var pending = InvokePrivateInstance<PendingToolApprovalState?>(
            DetectPendingApprovalMethod,
            agent,
            toolReceipts,
            toolCalls,
            new ChatRequestEvent { SessionId = "request-1" });

        pending.Should().NotBeNull();
        pending!.RequestId.Should().Be("approval-1");
        AgentToolExecutionContextMapper.FromPayload(pending.ToolContext)
            .Request.RequestId.Should().Be("request-1");
    }
    [Fact]
    public void ApplyVoicePresenceRuntimeStateChanged_ShouldStoreClonedModuleState()
    {
        var runtimeState = new VoicePresenceRuntimeState
        {
            Status = VoicePresenceRuntimeStatus.ResponseInProgress,
            CurrentResponseId = 3,
            NextResponseId = 4,
            ActiveProviderResponseId = "provider-response-1",
            Initialized = true,
            TransportAttached = true,
            PcmSampleRateHz = 24000,
            ActiveSessionId = "lease-1",
            RemoteAudioSupport = VoiceRemoteAudioSupport.LocalOnly,
        };
        var next = InvokePrivateStatic<RoleGAgentState>(
            ApplyVoicePresenceRuntimeStateChangedMethod,
            new RoleGAgentState(),
            new VoicePresenceRuntimeStateChangedEvent
            {
                ModuleName = "voice_presence",
                State = runtimeState,
            });
        runtimeState.CurrentResponseId = 99;
        next.VoicePresence.Should().ContainKey("voice_presence");
        next.VoicePresence["voice_presence"].CurrentResponseId.Should().Be(3);
        next.VoicePresence["voice_presence"].ActiveProviderResponseId.Should().Be("provider-response-1");
        next.VoicePresence["voice_presence"].ActiveSessionId.Should().Be("lease-1");
        next.VoicePresence["voice_presence"].Initialized.Should().BeTrue();
        next.VoicePresence["voice_presence"].TransportAttached.Should().BeTrue();
        next.VoicePresence["voice_presence"].PcmSampleRateHz.Should().Be(24000);
        next.VoicePresence["voice_presence"].RemoteAudioSupport.Should().Be(VoiceRemoteAudioSupport.LocalOnly);
    }
    [Fact]
    public void ApplyVoicePresenceRuntimeStateChanged_ShouldIgnoreBlankModuleName()
    {
        var current = new RoleGAgentState();
        var next = InvokePrivateStatic<RoleGAgentState>(
            ApplyVoicePresenceRuntimeStateChangedMethod,
            current,
            new VoicePresenceRuntimeStateChangedEvent
            {
                ModuleName = " ",
                State = new VoicePresenceRuntimeState
                {
                    Status = VoicePresenceRuntimeStatus.UserSpeaking,
                },
            });
        next.Should().BeSameAs(current);
        next.VoicePresence.Should().BeEmpty();
    }
    [Fact]
    public async Task VoicePresenceRuntimeStateOwner_ShouldPersistAndReturnClonedState()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-voice-presence");
        await agent.ActivateAsync();
        var runtimeState = new VoicePresenceRuntimeState
        {
            Status = VoicePresenceRuntimeStatus.AudioDraining,
            CurrentResponseId = 5,
            LastDrainAckResponseId = 4,
            LastDrainAckPlayoutSequence = 1200,
            NextResponseId = 6,
        };
        await agent.PersistVoicePresenceRuntimeStateAsync("voice_presence", runtimeState);
        runtimeState.CurrentResponseId = 99;
        agent.State.VoicePresence["voice_presence"].CurrentResponseId.Should().Be(5);
        agent.TryGetVoicePresenceRuntimeState("voice_presence", out var stored).Should().BeTrue();
        stored.CurrentResponseId.Should().Be(5);
        stored.CurrentResponseId = 77;
        agent.State.VoicePresence["voice_presence"].CurrentResponseId.Should().Be(5);
    }
    [Fact]
    public async Task VoicePresenceRuntimeStateOwner_ShouldReturnFalseForMissingModule()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-voice-presence-missing");
        await agent.ActivateAsync();
        agent.TryGetVoicePresenceRuntimeState("voice_presence", out var stored).Should().BeFalse();
        stored.Should().NotBeNull();
        stored.Status.Should().Be(VoicePresenceRuntimeStatus.Unspecified);
    }
    [Fact]
    public async Task VoicePresenceRuntimeStateOwner_ShouldReturnClonedSessionDefaults()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-voice-defaults");
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "voice role",
            VoiceSessionDefaults =
            {
                ["voice_presence"] = new VoiceSessionDefaults
                {
                    Voice = "verse",
                    Instructions = "be brief",
                    SampleRateHz = 16000,
                    TurnDetectionMode = VoiceTurnDetectionMode.ServerVad,
                },
            },
        });
        agent.TryGetVoiceSessionDefaults("voice_presence", out var defaults).Should().BeTrue();
        defaults.Voice.Should().Be("verse");
        defaults.SampleRateHz.Should().Be(16000);
        defaults.Voice = "mutated";
        agent.State.VoiceSessionDefaults["voice_presence"].Voice.Should().Be("verse");
        agent.TryGetVoiceSessionDefaults("missing", out var missing).Should().BeFalse();
        missing.Should().NotBeNull();
    }
    [Fact]
    public async Task HandleVoicePresenceEnableRequested_ShouldPersistDefaultsRuntimeStateAndCommittedEvents()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-voice-enable");
        await agent.ActivateAsync();
        await agent.HandleVoicePresenceEnableRequested(new VoicePresenceEnableRequested
        {
            ModuleName = "  voice_presence  ",
            VoiceSessionDefaults = new VoiceSessionDefaults
            {
                Voice = "alloy",
                Instructions = "keep replies short",
                SampleRateHz = 16000,
                TurnDetectionMode = VoiceTurnDetectionMode.ServerVad,
            },
        });
        agent.State.VoiceSessionDefaults.Should().ContainKey("voice_presence");
        var defaults = agent.State.VoiceSessionDefaults["voice_presence"];
        defaults.Voice.Should().Be("alloy");
        defaults.Instructions.Should().Be("keep replies short");
        defaults.SampleRateHz.Should().Be(16000);
        defaults.TurnDetectionMode.Should().Be(VoiceTurnDetectionMode.ServerVad);
        agent.State.VoicePresence.Should().ContainKey("voice_presence");
        var runtimeState = agent.State.VoicePresence["voice_presence"];
        runtimeState.Initialized.Should().BeTrue();
        runtimeState.Status.Should().Be(VoicePresenceRuntimeStatus.Idle);
        runtimeState.RemoteAudioSupport.Should().Be(VoiceRemoteAudioSupport.Supported);
        runtimeState.PcmSampleRateHz.Should().Be(16000);
        var persisted = await provider.GetRequiredService<IEventStore>().GetEventsAsync("role-voice-enable");
        persisted.Should().HaveCount(2);
        persisted.Select(x => x.EventType).Should().Equal(
            VoicePresenceEnabledEvent.Descriptor.FullName,
            VoicePresenceRuntimeStateChangedEvent.Descriptor.FullName);
        var enabled = persisted[0].EventData.Unpack<VoicePresenceEnabledEvent>();
        enabled.ModuleName.Should().Be("voice_presence");
        enabled.VoiceSessionDefaults.Voice.Should().Be("alloy");
        enabled.VoiceSessionDefaults.SampleRateHz.Should().Be(16000);
        enabled.RuntimeState.Initialized.Should().BeTrue();
        enabled.RuntimeState.RemoteAudioSupport.Should().Be(VoiceRemoteAudioSupport.Supported);
        enabled.RuntimeState.PcmSampleRateHz.Should().Be(16000);
        var changed = persisted[1].EventData.Unpack<VoicePresenceRuntimeStateChangedEvent>();
        changed.ModuleName.Should().Be("voice_presence");
        changed.State.Initialized.Should().BeTrue();
        changed.State.RemoteAudioSupport.Should().Be(VoiceRemoteAudioSupport.Supported);
        changed.State.PcmSampleRateHz.Should().Be(16000);
    }
    [Fact]
    public async Task HandleToolApprovalDecision_ShouldIgnoreMissingOrMismatchedPendingApproval()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-approval-ignore");
        await agent.ActivateAsync();
        await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = "req-1",
            Approved = false,
        });
        agent.State.PendingApproval.Should().BeNull();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "turn-original",
            ScopeId = "scope-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
        };
        await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = "req-2",
            Approved = false,
        });
        agent.State.PendingApproval.Should().NotBeNull();
        agent.State.PendingApproval!.RequestId.Should().Be("req-1");
    }

    [Fact]
    public async Task HandleToolApprovalDecision_ShouldExecuteToolAndDispatchContinuation_WhenApproved()
    {
        using var provider = BuildServiceProvider();
        AgentToolExecutionContext? observedToolContext = null;
        var executionCalls = 0;
        var pendingWasPresentDuringExecution = false;
        RoleGAgent? agent = null;
        var tool = new DelegateTool("dangerous_tool", argumentsJson =>
        {
            executionCalls++;
            pendingWasPresentDuringExecution = agent!.State.PendingApproval is not null;
            observedToolContext = AgentToolRequestContext.Current;
            return $"RESULT:{argumentsJson}";
        });
        agent = CreateRoleAgent(
            provider,
            "role-approval-approved",
            toolSources: [new StaticToolSource([tool])]);
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        var toolContext = new AgentToolExecutionContext(
                new AgentToolRequestIdentity("req-1", "call-1"),
                new AgentToolCredentials("token-should-not-be-used", null, null),
                new AgentToolCallerContext("scope-a", "owner-a", "response-a"),
                AgentToolChannelContext.Empty,
                AgentToolSenderBindingContext.Empty,
                new LLMRequestRoutingContext("model-a", "route-a", 3, "remember-a"),
                new AgentToolConnectedServicesContext("""{"service":"lark"}"""),
                AgentSkillRecoveryContext.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["trace-id"] = "trace-1",
                })
            {
                ExecutionOwner = AgentToolExecutionOwners.Actor("role-approval-approved"),
            };
        var pending = await CreatePendingApprovalAsync(
            provider, tool, toolContext, "{\"value\":1}");
        await AttachPendingApprovalCheckpointAsync(agent, provider, pending);
        var approvalRequestId = agent.State.PendingApproval.RequestId;
        await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = approvalRequestId,
            ContinuationTurnId = "turn-approval-continuation",
            Approved = true,
            Reason = "approved",
        });
        agent.State.PendingApproval.Should().BeNull();
        pendingWasPresentDuringExecution.Should().BeTrue();
        executionCalls.Should().Be(1);
        await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = approvalRequestId,
            Approved = true,
        });
        executionCalls.Should().Be(1, "the durable approval grant was consumed after terminal execution began");
        AgentToolRequestContext.Current.Should().BeNull();
        observedToolContext.Should().NotBeNull();
        observedToolContext!.Caller.ScopeId.Should().Be("scope-a");
        observedToolContext.Caller.OwnerSubject.Should().Be("owner-a");
        observedToolContext.Routing.ModelOverride.Should().Be("model-a");
        observedToolContext.Credentials.Should().Be(AgentToolCredentials.Empty);
        publisher.Published
            .OfType<RoleChatRecoveryContinuationRequested>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == pending.SessionId &&
                x.OperationId == pending.OperationId);
        var checkpoint = agent.State.Sessions[pending.SessionId].RecoveryCheckpoint;
        checkpoint.Stage.Should().Be(RoleChatRecoveryCheckpointStage.ContinuationPrepared);
        checkpoint.ContinuationSessionId.Should().Be("turn-approval-continuation");
    }

    [Fact]
    public async Task HandleToolApprovalDecision_ShouldClearPendingAndRethrow_WhenContinuationDispatchFails()
    {
        using var provider = BuildServiceProvider();
        var tool = new DelegateTool("dangerous_tool", _ => "{\"ok\":true}");
        var agent = CreateRoleAgent(
            provider,
            "role-approval-dispatch-fails",
            toolSources: [new StaticToolSource([tool])]);
        agent.EventPublisher = new ThrowingEventPublisher();
        await agent.ActivateAsync();
        var pending = await CreatePendingApprovalAsync(
            provider,
            tool,
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("req-1", "call-1"),
                ExecutionOwner = AgentToolExecutionOwners.Actor("role-approval-dispatch-fails"),
            });
        await AttachPendingApprovalCheckpointAsync(agent, provider, pending);
        var approvalRequestId = agent.State.PendingApproval.RequestId;
        await FluentActions.Invoking(() => agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
            {
                RequestId = approvalRequestId,
                ContinuationTurnId = "turn-approval-failed",
                Approved = true,
            }))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch failed with bearer-secret credential");

        agent.State.PendingApproval.Should().BeNull();
        agent.State.Sessions.Should().NotContainKey("turn-original");
        agent.State.Sessions["turn-approval-failed"].Completed.Should().BeTrue();
        agent.State.Sessions["turn-approval-failed"].FinalContent.Should()
            .Contain("approval_continuation_failed: The approval continuation failed. Please try again.");
        agent.State.Sessions["turn-approval-failed"].SafeMessage.Should()
            .Be("The approval continuation failed. Please try again.");
        agent.State.Sessions["turn-approval-failed"].ToString().Should()
            .NotContain("bearer-secret").And.NotContain("credential");
        AgentToolRequestContext.Current.Should().BeNull();
    }
    [Fact]
    public async Task HandleToolApprovalTimeout_ShouldIgnoreMissingOrMismatchedPendingApproval()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-timeout-ignore");
        await agent.ActivateAsync();
        await agent.HandleToolApprovalTimeout(new ToolApprovalTimeoutFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
        });
        agent.State.PendingApproval.Should().BeNull();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
        };
        await agent.HandleToolApprovalTimeout(new ToolApprovalTimeoutFiredEvent
        {
            RequestId = "req-2",
            SessionId = "session-a",
        });
        agent.State.PendingApproval.Should().NotBeNull();
        agent.State.PendingApproval!.RequestId.Should().Be("req-1");
    }
    [Fact]
    public async Task HandleToolApprovalTimeout_ShouldPersistTerminalFailure_WhenRemotePortMissing()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-timeout-no-remote");
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
        };
        await agent.HandleToolApprovalTimeout(new ToolApprovalTimeoutFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
        });
        agent.State.PendingApproval.Should().BeNull();
        agent.State.Sessions["session-a"].Completed.Should().BeTrue();
        agent.State.Sessions["session-a"].FinalContent.Should().Contain("approval_timeout: Tool approval timed out and no remote approval port is configured.");
    }
    [Fact]
    public async Task HandleToolApprovalTimeout_ShouldSubmitRemoteApprovalAndPersistRemoteBinding()
    {
        using var provider = BuildServiceProvider();
        var remotePort = new StubRemoteApprovalPort(
            submit: request => Task.FromResult(new RemoteToolApprovalSubmission(
                "remote-1",
                DateTimeOffset.FromUnixTimeSeconds(1_800))),
            status: _ => throw new InvalidOperationException("status should not be called"));
        var agent = CreateRoleAgent(
            provider,
            "role-timeout-submit",
            remotePort);
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
        };
        await agent.HandleToolApprovalTimeout(new ToolApprovalTimeoutFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
        });
        agent.State.PendingApproval.Should().NotBeNull();
        agent.State.PendingApproval!.RemoteApprovalId.Should().Be("remote-1");
        agent.State.PendingApproval.RemoteStatusCheckAttempt.Should().Be(1);
        agent.State.PendingApproval.RemoteApprovalExpiresAtUnixMs.Should()
            .Be(DateTimeOffset.FromUnixTimeSeconds(1_800).ToUnixTimeMilliseconds());
        remotePort.Submitted.Should().ContainSingle()
            .Which.RequestId.Should().Be("req-1");
        remotePort.StatusQueries.Should().BeEmpty();
        ((RecordingRuntimeCallbackScheduler)provider.GetRequiredService<IActorRuntimeCallbackScheduler>())
            .TimeoutRequests.Should().ContainSingle(x =>
                x.CallbackId == "tool-approval-remote-status-req-1-remote-1-1" &&
                x.ActorId == "role-timeout-submit");
    }
    [Fact]
    public async Task HandleToolApprovalTimeout_ShouldKeepPendingAndScheduleStatus_WhenNotificationFails()
    {
        using var provider = BuildServiceProvider();
        var remotePort = new StubRemoteApprovalPort(
            submit: request => Task.FromResult(new RemoteToolApprovalSubmission(
                "remote-1",
                DateTimeOffset.FromUnixTimeSeconds(1_800))),
            status: _ => throw new InvalidOperationException("status should not be called"));
        var notificationPort = new StubRemoteApprovalNotificationPort(
            _ => throw new InvalidOperationException("notification failed"));
        var agent = CreateRoleAgent(
            provider,
            "role-timeout-notify-fails",
            remotePort,
            remoteToolApprovalNotificationPort: notificationPort);
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = """{"path":"/prod"}""",
            ToolContext = (AgentToolExecutionContext.Empty with
            {
                Channel = new AgentToolChannelContext(
                    "lark",
                    "sender-1",
                    "scope-1",
                    "msg-1",
                    "om_1",
                    "agent-delivery-1"),
            }).ToPayload(),
        };
        await agent.HandleToolApprovalTimeout(new ToolApprovalTimeoutFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
        });
        agent.State.PendingApproval.Should().NotBeNull();
        agent.State.PendingApproval!.RequestId.Should().Be("req-1");
        agent.State.PendingApproval.RemoteApprovalId.Should().Be("remote-1");
        notificationPort.Notifications.Should().ContainSingle();
        notificationPort.Notifications[0].Request.RequestId.Should().Be("req-1");
        notificationPort.Notifications[0].Submission.RemoteApprovalId.Should().Be("remote-1");
        notificationPort.Notifications[0].ToolContext.Channel.DeliveryTargetId.Should().Be("agent-delivery-1");
        ((RecordingRuntimeCallbackScheduler)provider.GetRequiredService<IActorRuntimeCallbackScheduler>())
            .TimeoutRequests.Should().ContainSingle(x =>
                x.CallbackId == "tool-approval-remote-status-req-1-remote-1-1" &&
                x.ActorId == "role-timeout-notify-fails");
    }
    [Fact]
    public async Task HandleToolApprovalTimeout_ShouldPersistTerminalFailure_WhenRemoteSubmitThrows()
    {
        using var provider = BuildServiceProvider();
        var remotePort = new StubRemoteApprovalPort(
            submit: _ => throw new InvalidOperationException("submit failed"),
            status: _ => throw new InvalidOperationException("status should not be called"));
        var agent = CreateRoleAgent(provider, "role-timeout-submit-throws", remotePort);
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
        };
        await agent.HandleToolApprovalTimeout(new ToolApprovalTimeoutFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
        });
        agent.State.PendingApproval.Should().BeNull();
        agent.State.Sessions["session-a"].Completed.Should().BeTrue();
        agent.State.Sessions["session-a"].FinalContent.Should()
            .Contain("approval_timeout: Remote approval submission failed. Please try again.");
        remotePort.StatusQueries.Should().BeEmpty();
    }
    [Fact]
    public async Task HandleRemoteApprovalStatusCheck_WhenPending_ShouldScheduleNextCheckOnly()
    {
        using var provider = BuildServiceProvider();
        var remotePort = new StubRemoteApprovalPort(
            submit: _ => throw new InvalidOperationException("submit should not be called"),
            status: _ => Task.FromResult(new RemoteToolApprovalStatusSnapshot(
                RemoteToolApprovalStatus.Pending,
                ExpiresAt: DateTimeOffset.FromUnixTimeSeconds(2_000))));
        var agent = CreateRoleAgent(
            provider,
            "role-status-pending",
            remotePort);
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
            RemoteApprovalId = "remote-1",
            RemoteStatusCheckAttempt = 1,
        };
        await agent.HandleRemoteApprovalStatusCheck(new ToolApprovalRemoteStatusCheckFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
            RemoteApprovalId = "remote-1",
            Attempt = 1,
        });
        agent.State.PendingApproval.Should().NotBeNull();
        agent.State.PendingApproval!.RemoteStatusCheckAttempt.Should().Be(2);
        agent.State.PendingApproval.RemoteApprovalExpiresAtUnixMs.Should()
            .Be(DateTimeOffset.FromUnixTimeSeconds(2_000).ToUnixTimeMilliseconds());
        ((RecordingRuntimeCallbackScheduler)provider.GetRequiredService<IActorRuntimeCallbackScheduler>())
            .TimeoutRequests.Should().ContainSingle(x =>
                x.CallbackId == "tool-approval-remote-status-req-1-remote-1-2" &&
                x.ActorId == "role-status-pending");
    }
    [Fact]
    public async Task HandleRemoteApprovalStatusCheck_WhenPortMissing_ShouldPersistTerminalFailure()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-status-no-remote");
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
            RemoteApprovalId = "remote-1",
            RemoteStatusCheckAttempt = 1,
        };
        await agent.HandleRemoteApprovalStatusCheck(new ToolApprovalRemoteStatusCheckFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
            RemoteApprovalId = "remote-1",
            Attempt = 1,
        });
        agent.State.PendingApproval.Should().BeNull();
        agent.State.Sessions["session-a"].Completed.Should().BeTrue();
        agent.State.Sessions["session-a"].FinalContent.Should()
            .Contain("approval_timeout: Tool approval timed out and no remote approval port is configured.");
    }
    [Fact]
    public async Task HandleRemoteApprovalStatusCheck_WhenStatusThrows_ShouldAdvanceAttemptAndKeepPending()
    {
        using var provider = BuildServiceProvider();
        var remotePort = new StubRemoteApprovalPort(
            submit: _ => throw new InvalidOperationException("submit should not be called"),
            status: _ => throw new InvalidOperationException("status failed"));
        var agent = CreateRoleAgent(provider, "role-status-throws", remotePort);
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
            RemoteApprovalId = "remote-1",
            RemoteStatusCheckAttempt = 1,
            RemoteApprovalExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        };
        await agent.HandleRemoteApprovalStatusCheck(new ToolApprovalRemoteStatusCheckFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
            RemoteApprovalId = "remote-1",
            Attempt = 1,
        });
        agent.State.PendingApproval.Should().NotBeNull();
        agent.State.PendingApproval!.RemoteStatusCheckAttempt.Should().Be(2);
        agent.State.PendingApproval.RemoteApprovalId.Should().Be("remote-1");
        agent.State.Sessions.ContainsKey("session-a").Should().BeFalse();
        remotePort.StatusQueries.Should().ContainSingle();
        ((RecordingRuntimeCallbackScheduler)provider.GetRequiredService<IActorRuntimeCallbackScheduler>())
            .TimeoutRequests.Should().ContainSingle(x =>
                x.CallbackId == "tool-approval-remote-status-req-1-remote-1-2" &&
                x.ActorId == "role-status-throws");
    }
    [Fact]
    public async Task HandleRemoteApprovalStatusCheck_ShouldIssueStatusQueryWithoutPortLevelCarrier()
    {
        using var provider = BuildServiceProvider();
        var remotePort = new StubRemoteApprovalPort(
            submit: _ => throw new InvalidOperationException("submit should not be called"),
            status: _ => Task.FromResult(new RemoteToolApprovalStatusSnapshot(
                RemoteToolApprovalStatus.Unknown,
                "still pending")));
        var agent = CreateRoleAgent(provider, "role-status-scrub-metadata", remotePort);
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
            RemoteApprovalId = "remote-1",
            RemoteStatusCheckAttempt = 1,
            RemoteApprovalExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        };
        await agent.HandleRemoteApprovalStatusCheck(new ToolApprovalRemoteStatusCheckFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
            RemoteApprovalId = "remote-1",
            Attempt = 1,
        });
        var query = remotePort.StatusQueries.Should().ContainSingle().Which;
        query.RequestId.Should().Be("req-1");
        query.RemoteApprovalId.Should().Be("remote-1");
    }
    [Fact]
    public async Task HandleRemoteApprovalStatusCheck_WhenUnknownReachesMaxAttempts_ShouldPersistTerminalFailure()
    {
        using var provider = BuildServiceProvider();
        var remotePort = new StubRemoteApprovalPort(
            submit: _ => throw new InvalidOperationException("submit should not be called"),
            status: _ => Task.FromResult(new RemoteToolApprovalStatusSnapshot(
                RemoteToolApprovalStatus.Unknown,
                "still unknown")));
        var agent = CreateRoleAgent(provider, "role-status-max-attempts", remotePort);
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
            RemoteApprovalId = "remote-1",
            RemoteStatusCheckAttempt = 23,
            RemoteApprovalExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        };
        await agent.HandleRemoteApprovalStatusCheck(new ToolApprovalRemoteStatusCheckFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
            RemoteApprovalId = "remote-1",
            Attempt = 23,
        });
        agent.State.PendingApproval.Should().BeNull();
        agent.State.Sessions["session-a"].Completed.Should().BeTrue();
        agent.State.Sessions["session-a"].FinalContent.Should()
            .Contain("approval_timeout: still unknown");
        remotePort.StatusQueries.Should().ContainSingle();
        ((RecordingRuntimeCallbackScheduler)provider.GetRequiredService<IActorRuntimeCallbackScheduler>())
            .TimeoutRequests.Should().BeEmpty();
    }
    [Fact]
    public async Task HandleRemoteApprovalStatusCheck_WhenApproved_ShouldResumeThroughToolApprovalDecision()
    {
        using var provider = BuildServiceProvider();
        var remotePort = new StubRemoteApprovalPort(
            submit: _ => throw new InvalidOperationException("submit should not be called"),
            status: _ => Task.FromResult(new RemoteToolApprovalStatusSnapshot(
                RemoteToolApprovalStatus.Approved,
                "approved remotely")));
        var tool = new DelegateTool("dangerous_tool", _ => "remote-result");
        var agent = CreateRoleAgent(
            provider,
            "role-status-approved",
            remotePort,
            toolSources: [new StaticToolSource([tool])]);
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        var pending = await CreatePendingApprovalAsync(
            provider,
            tool,
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("req-1", "call-1"),
                ExecutionOwner = AgentToolExecutionOwners.Actor("role-status-approved"),
            });
        pending.RemoteApprovalId = "remote-1";
        pending.RemoteStatusCheckAttempt = 1;
        await AttachPendingApprovalCheckpointAsync(agent, provider, pending);
        var approvalRequestId = agent.State.PendingApproval.RequestId;
        await agent.HandleRemoteApprovalStatusCheck(new ToolApprovalRemoteStatusCheckFiredEvent
        {
            RequestId = approvalRequestId,
            SessionId = pending.SessionId,
            RemoteApprovalId = "remote-1",
            Attempt = 1,
        });
        agent.State.PendingApproval.Should().BeNull();
        publisher.Published.OfType<RoleChatRecoveryContinuationRequested>().Should()
            .ContainSingle(x =>
                x.SessionId == pending.SessionId &&
                x.OperationId == pending.OperationId);
    }
    [Theory]
    [InlineData(RemoteToolApprovalStatus.Rejected, "approval_denied")]
    [InlineData(RemoteToolApprovalStatus.Expired, "approval_timeout")]
    public async Task HandleRemoteApprovalStatusCheck_WhenTerminal_ShouldPersistTerminalFailureAndClearPending(
        RemoteToolApprovalStatus status,
        string reasonCode)
    {
        using var provider = BuildServiceProvider();
        var remotePort = new StubRemoteApprovalPort(
            submit: _ => throw new InvalidOperationException("submit should not be called"),
            status: _ => Task.FromResult(new RemoteToolApprovalStatusSnapshot(status, "terminal")));
        var agent = CreateRoleAgent(provider, $"role-status-{status}", remotePort);
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
            RemoteApprovalId = "remote-1",
            RemoteStatusCheckAttempt = 1,
        };
        await agent.HandleRemoteApprovalStatusCheck(new ToolApprovalRemoteStatusCheckFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
            RemoteApprovalId = "remote-1",
            Attempt = 1,
        });
        agent.State.PendingApproval.Should().BeNull();
        agent.State.Sessions["session-a"].Completed.Should().BeTrue();
        agent.State.Sessions["session-a"].FinalContent.Should().Contain($"{reasonCode}: terminal");
    }
    [Fact]
    public async Task HandleRemoteApprovalStatusCheck_ShouldIgnoreStaleRequestOrRemoteId()
    {
        using var provider = BuildServiceProvider();
        var remotePort = new StubRemoteApprovalPort(
            submit: _ => throw new InvalidOperationException("submit should not be called"),
            status: _ => throw new InvalidOperationException("stale event should not read status"));
        var agent = CreateRoleAgent(provider, "role-status-stale", remotePort);
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
            RemoteApprovalId = "remote-1",
            RemoteStatusCheckAttempt = 2,
        };
        await agent.HandleRemoteApprovalStatusCheck(new ToolApprovalRemoteStatusCheckFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
            RemoteApprovalId = "remote-2",
            Attempt = 2,
        });
        await agent.HandleRemoteApprovalStatusCheck(new ToolApprovalRemoteStatusCheckFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
            RemoteApprovalId = "remote-1",
            Attempt = 1,
        });
        agent.State.PendingApproval.Should().NotBeNull();
        agent.State.PendingApproval!.RemoteApprovalId.Should().Be("remote-1");
        remotePort.StatusQueries.Should().BeEmpty();
    }
    [Fact]
    public void ApplyChatSessionStateTransitions_ShouldAssignSequence_AndPreserveCompletedOutputs()
    {
        var started = InvokePrivateStatic<RoleGAgentState>(
            ApplyChatSessionStartedMethod,
            new RoleGAgentState(),
            new RoleChatSessionStartedEvent
            {
                SessionId = "session-a",
                Prompt = "hello",
                InputParts =
                {
                    new ChatContentPart
                    {
                        Kind = ChatContentPartKind.Image,
                        Name = "photo.png",
                    },
                },
            });
        started.MessageCount.Should().Be(1);
        started.Sessions["session-a"].Sequence.Should().Be(1);
        started.Sessions["session-a"].Prompt.Should().Be("hello");
        started.Sessions["session-a"].InputParts.Should().ContainSingle();
        started.Sessions["session-a"].Sequence = 0;
        var completed = InvokePrivateStatic<RoleGAgentState>(
            ApplyChatSessionCompletedMethod,
            started,
            new RoleChatSessionCompletedEvent
            {
                SessionId = "session-a",
                Prompt = "hello",
                Content = "done",
                ReasoningContent = "because",
                ContentEmitted = true,
                ToolCalls =
                {
                    new ToolCallEvent
                    {
                        CallId = "call-1",
                        ToolName = "search",
                        ArgumentsJson = "{}",
                    },
                },
                ToolReceipts =
                {
                    new AgentToolReceipt
                    {
                        CallId = "call-1",
                        ToolName = "ornn_publish_skill",
                        Status = AgentToolReceiptStatus.Success,
                        ApprovalMode = AgentToolReceiptApprovalMode.AlwaysRequire,
                        SideEffectKind = "ornn.publish.skill",
                        SubjectKind = "ornn.skill",
                        SubjectId = "skill-1",
                        SubjectVersion = "1.0",
                        SubjectHash = "hash-1",
                        ResultJson = """{"guid":"skill-1","version":"1.0","skillHash":"hash-1"}""",
                    },
                },
                OutputParts =
                {
                    new ChatContentPart
                    {
                        Kind = ChatContentPartKind.Text,
                        Text = "done",
                    },
                },
            });
        completed.MessageCount.Should().Be(2);
        completed.Sessions["session-a"].Completed.Should().BeTrue();
        completed.Sessions["session-a"].FinalContent.Should().Be("done");
        completed.Sessions["session-a"].FinalReasoningContent.Should().Be("because");
        completed.Sessions["session-a"].ToolCalls.Should().ContainSingle(x => x.CallId == "call-1");
        completed.Sessions["session-a"].ToolReceipts.Should().ContainSingle(x =>
            x.CallId == "call-1" &&
            x.Status == AgentToolReceiptStatus.Success &&
            x.SubjectId == "skill-1" &&
            x.SubjectHash == "hash-1");
        completed.Sessions["session-a"].OutputParts.Should().ContainSingle(x => x.Text == "done");
    }
    [Fact]
    public void ApplyRemoteApprovalSubmitted_ShouldStoreRemoteBinding()
    {
        var state = new RoleGAgentState
        {
            PendingApproval = new PendingToolApprovalState
            {
                RequestId = "req-1",
            },
        };
        var next = InvokePrivateStatic<RoleGAgentState>(
            ApplyRemoteApprovalSubmittedMethod,
            state,
            new RemoteToolApprovalSubmittedEvent
            {
                RequestId = "req-1",
                RemoteApprovalId = "remote-1",
                StatusCheckAttempt = 3,
                ExpiresAtUnixMs = 1234,
            });
        next.PendingApproval.Should().NotBeNull();
        next.PendingApproval!.RemoteApprovalId.Should().Be("remote-1");
        next.PendingApproval.RemoteStatusCheckAttempt.Should().Be(3);
        next.PendingApproval.RemoteApprovalExpiresAtUnixMs.Should().Be(1234);
    }

    [Fact]
    public void BuildContinuationPrompt_AndSanitizeFailureMessage_ShouldHandleFallbackBranches()
    {
        var prompt = InvokePrivateStatic<string>(
            BuildContinuationPromptMethod,
            new PendingToolApprovalState
            {
                ToolName = "dangerous_tool",
            },
            (string?)null);
        prompt.Should().Contain("dangerous_tool");
        prompt.Should().Contain("(no output)");
        InvokePrivateStatic<string>(SanitizeFailureMessageMethod, "  boom  ").Should().Be("boom");
        InvokePrivateStatic<string>(SanitizeFailureMessageMethod, " ").Should().Be("LLM request failed.");
        InvokePrivateStatic<string>(SanitizeFailureMessageMethod, (object?)null).Should().Be("LLM request failed.");
    }

    [Fact]
    public void ResolveRequestInputParts_AndBuildRequestLogSummary_ShouldRespectPromptAndMediaBranches()
    {
        const string sensitivePrompt = "secret prompt body";
        var multimodalRequest = new ChatRequestEvent
        {
            Prompt = sensitivePrompt,
        };
        multimodalRequest.InputParts.Add(new ChatContentPart
        {
            Kind = ChatContentPartKind.Image,
            Name = "photo.png",
        });
        var parts = InvokePrivateStatic<IReadOnlyList<ContentPart>>(
            ResolveRequestInputPartsMethod,
            multimodalRequest);
        parts.Should().HaveCount(2);
        parts[0].Kind.Should().Be(ContentPartKind.Text);
        parts[1].Kind.Should().Be(ContentPartKind.Image);
        var multimodalSummary = InvokePrivateStatic<object>(BuildRequestLogSummaryMethod, multimodalRequest);
        GetProperty<int>(multimodalSummary, "PromptLength").Should().Be(sensitivePrompt.Length);
        GetProperty<int>(multimodalSummary, "InputPartCount").Should().Be(2);
        multimodalSummary.ToString().Should().NotContain(sensitivePrompt);
        var promptlessRequest = new ChatRequestEvent();
        promptlessRequest.InputParts.Add(new ChatContentPart
        {
            Kind = ChatContentPartKind.Video,
            Name = "clip.mp4",
        });
        var promptlessSummary = InvokePrivateStatic<object>(BuildRequestLogSummaryMethod, promptlessRequest);
        GetProperty<int>(promptlessSummary, "PromptLength").Should().Be(0);
        GetProperty<int>(promptlessSummary, "InputPartCount").Should().Be(1);
        promptlessSummary.ToString().Should().NotContain("video");
        InvokePrivateStatic<IReadOnlyList<ContentPart>>(
                ResolveRequestInputPartsMethod,
                new ChatRequestEvent())
            .Should()
            .ContainSingle(x => x.Kind == ContentPartKind.Text && x.Text == string.Empty);
    }

    [Fact]
    public async Task HandleChatRequest_ShouldRedactPromptAndResponseContentInInformationLogs()
    {
        const string sensitivePrompt = "customer secret prompt";
        const string sensitiveResponse = "customer secret response";
        var logger = new RecordingLogger();
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(
            provider,
            "role-log-redaction",
            llmProviderFactory: new StubChatProviderFactory((_, _) =>
                Task.FromResult(new LLMResponse { Content = sensitiveResponse })));
        agent.Logger = logger;
        agent.EventPublisher = new TestRecordingEventPublisher();
        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = sensitivePrompt,
            SessionId = "session-log-redaction",
        });
        var messages = logger.Messages.Should().NotBeEmpty().And.Subject;
        messages.Should().Contain(message =>
            message.Contains("input_redacted=true", StringComparison.Ordinal) &&
            message.Contains($"prompt_len={sensitivePrompt.Length}", StringComparison.Ordinal));
        messages.Should().Contain(message =>
            message.Contains("output_redacted=true", StringComparison.Ordinal) &&
            message.Contains($"output_len={sensitiveResponse.Length}", StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains(sensitivePrompt, StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains(sensitiveResponse, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetDescriptionAsync_ShouldIncludeRoleNameAndActorId()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-description");
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "helper",
        });
        (await agent.GetDescriptionAsync()).Should().Be("RoleGAgent[helper]:role-description");
    }

    [Fact]
    public void ResolveTrackedSession_ShouldReturnMatch_AndRejectPromptOrInputMismatch()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-session-state");
        agent.State.Sessions["session-a"] = new RoleChatSessionState
        {
            Prompt = "hello",
            Sequence = 1,
        };
        agent.State.Sessions["session-a"].InputParts.Add(new ChatContentPart
        {
            Kind = ChatContentPartKind.Image,
            Name = "photo.png",
        });
        InvokePrivateInstance<RoleChatSessionState?>(
            ResolveTrackedSessionMethod,
            agent,
            new ChatRequestEvent
            {
                SessionId = "session-a",
                Prompt = "hello",
                InputParts =
                {
                    new ChatContentPart
                    {
                        Kind = ChatContentPartKind.Image,
                        Name = "photo.png",
                    },
                },
            })
            .Should()
            .NotBeNull();
        FluentActions.Invoking(() => InvokePrivateInstance<RoleChatSessionState?>(
                ResolveTrackedSessionMethod,
                agent,
                new ChatRequestEvent
                {
                    SessionId = "session-a",
                    Prompt = "bye",
                }))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*different prompt*");
        FluentActions.Invoking(() => InvokePrivateInstance<RoleChatSessionState?>(
                ResolveTrackedSessionMethod,
                agent,
                new ChatRequestEvent
                {
                    SessionId = "session-a",
                    Prompt = "hello",
                    InputParts =
                    {
                        new ChatContentPart
                        {
                            Kind = ChatContentPartKind.Audio,
                            Name = "voice.wav",
                        },
                    },
                }))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*different multimodal input*");
        InvokePrivateInstance<RoleChatSessionState?>(
                ResolveTrackedSessionMethod,
                agent,
                new ChatRequestEvent())
            .Should()
            .BeNull();
    }

    [Fact]
    public void ExtractStateConfigOverrides_ShouldReturnEmpty_WhenStateHasNoOverrides()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-config-empty");
        var overrides = InvokePrivateInstance<object>(
            ExtractStateConfigOverridesMethod,
            agent,
            new RoleGAgentState());
        GetProperty<string?>(overrides, "ProviderName").Should().BeNull();
        GetProperty<string?>(overrides, "Model").Should().BeNull();
        GetProperty<string?>(overrides, "SystemPrompt").Should().BeNull();
        GetProperty<double?>(overrides, "Temperature").Should().BeNull();
        GetProperty<int?>(overrides, "MaxTokens").Should().BeNull();
        GetProperty<bool?>(overrides, "EnableSummarization").Should().BeNull();
    }

    [Fact]
    public async Task HandleInitializeRoleAgent_ShouldNormalizeExtensions_AndExposeAdditionalOverrides()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-config-full");
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "worker",
            ProviderName = "mock",
            Model = "model-a",
            SystemPrompt = "be helpful",
            EventModules = "  module-a  ",
            EventRoutes = "  route-a  ",
            MaxPromptTokenBudget = 2048,
            CompressionThreshold = 512,
            EnableSummarization = true,
        });
        agent.State.EventModules.Should().Be("module-a");
        agent.State.EventRoutes.Should().Be("route-a");
        agent.EffectiveConfig.MaxPromptTokenBudget.Should().Be(2048);
        agent.EffectiveConfig.CompressionThreshold.Should().Be(0.99);
        agent.EffectiveConfig.EnableSummarization.Should().BeTrue();
        var overrides = InvokePrivateInstance<object>(
            ExtractStateConfigOverridesMethod,
            agent,
            agent.State);
        GetProperty<string?>(overrides, "ProviderName").Should().Be("mock");
        GetProperty<string?>(overrides, "Model").Should().Be("model-a");
        GetProperty<string?>(overrides, "SystemPrompt").Should().Be("be helpful");
        GetProperty<int?>(overrides, "MaxPromptTokenBudget").Should().Be(2048);
        GetProperty<double?>(overrides, "CompressionThreshold").Should().Be(512);
        GetProperty<bool?>(overrides, "EnableSummarization").Should().BeTrue();
    }
    private static AgentProfileTurnAuthorityState TurnAuthority(
        string sessionId,
        int attempt,
        string intentId,
        string exactSkillGuid) =>
        new()
        {
            ReconciliationKey = new AgentProfileTurnReconciliationKey { SessionId = sessionId, Attempt = attempt },
            CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
                { ProfileId = "profile-a", ProfileVersion = "v1", PolicyRevision = "policy-a", IntentId = intentId },
            SelectedExactSkillRef = new ExactRemoteSkillRef { Guid = exactSkillGuid, LiteralVersion = "1.0.0" },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
            AuthorityCeilingToolNames = { "recovery", "task" },
        };
    private static RoleGAgentState StateWithIncompleteAuthority(AgentProfileTurnAuthorityState authority) =>
        new()
        {
            AgentProfileTurnAuthority = authority,
            Sessions = { [authority.ReconciliationKey.SessionId] = new RoleChatSessionState { Sequence = 1 } },
        };
    private static RoleGAgentState ApplyAuthority(
        RoleGAgentState current,
        AgentProfileTurnAuthorityCommitKind commitKind,
        AgentProfileTurnAuthorityState authority) =>
        InvokePrivateStatic<RoleGAgentState>(
            ApplyAgentProfileTurnAuthorityCommittedMethod,
            current,
            new AgentProfileTurnAuthorityCommittedEvent
                { CommitKind = commitKind, Authority = authority });
    private static AgentProfileTurnAuthorityState MutateAuthority(
        RoleGAgentState current,
        Action<AgentProfileTurnAuthorityState> mutate)
    {
        var authority = current.AgentProfileTurnAuthority.Clone();
        mutate(authority);
        return authority;
    }
    private static ServiceProvider BuildServiceProvider(
        IAuditTrailAppender? auditTrailAppender = null,
        IEventStore? eventStore = null,
        IAgentToolExecutionPort? executionPort = null,
        IAgentToolAdmissionLedger? admissionLedger = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore ?? new InMemoryEventStoreForTests())
            .AddSingleton<ISecretVault, InMemorySecretVault>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, RecordingRuntimeCallbackScheduler>()
            .AddSingleton<IAuditTrailAppender>(auditTrailAppender ?? new AppendedAuditTrail())
            .AddSingleton<IAuditActorIdentityHasher, StableIdentityHasher>()
            .AddSingleton<IAgentToolAdmissionLedger>(
                admissionLedger ?? AlwaysStartingAgentToolAdmissionLedger.Instance)
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        if (executionPort is null)
            services.AddSingleton<IAgentToolExecutionPort, AdmittedAgentToolExecutor>();
        else
            services.AddSingleton(executionPort);
        return services.BuildServiceProvider();
    }
    private static RoleGAgent CreateRoleAgent(
        IServiceProvider provider,
        string actorId,
        IRemoteToolApprovalPort? remoteToolApprovalPort = null,
        IRemoteToolApprovalNotificationPort? remoteToolApprovalNotificationPort = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        ILLMProviderFactory? llmProviderFactory = null)
    {
        var agent = new TestRoleGAgent(
            provider.GetRequiredService<IAgentToolExecutionPort>(),
            llmProviderFactory,
            remoteToolApprovalPort,
            remoteToolApprovalNotificationPort,
            toolSources ?? Enumerable.Empty<IAgentToolSource>(),
            provider.GetRequiredService<ISecretVault>())
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        var setId = typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setId.Invoke(agent, [actorId]);
        return agent;
    }
    private static ChatHistory GetHistory(RoleGAgent agent)
    {
        return (ChatHistory)typeof(AIGAgentBase<RoleGAgentState>)
            .GetProperty("History", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent)!;
    }
    private static T InvokePrivateStatic<T>(MethodInfo method, params object?[] args)
    {
        try
        {
            return (T)method.Invoke(null, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }
    private static async Task<PendingToolApprovalState> CreatePendingApprovalAsync(
        IServiceProvider provider,
        IAgentTool tool,
        AgentToolExecutionContext context,
        string argumentsJson = "{}")
    {
        var sessionId = string.IsNullOrWhiteSpace(context.Request.RequestId)
            ? "session-a"
            : context.Request.RequestId!;
        var operationId = string.IsNullOrWhiteSpace(context.Request.OperationId)
            ? $"tool:test:operation:{sessionId}:{context.Request.CallId}"
            : context.Request.OperationId!;
        var preparedContext = context with
        {
            Request = context.Request with
            {
                RequestId = sessionId,
                OperationId = operationId,
                IdempotencyKey = operationId,
            },
        };
        var outcome = await provider.GetRequiredService<IAgentToolExecutionPort>().ExecuteAsync(
            new AgentToolExecutionRequest(
                tool,
                argumentsJson,
                preparedContext,
                AgentToolApprovalContinuationMode.ActorOwned,
                null));
        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.ApprovalRequired);
        return new PendingToolApprovalState
        {
            RequestId = outcome.Receipt.ApprovalRequestId,
            SessionId = sessionId,
            ScopeId = preparedContext.Caller.ScopeId ?? string.Empty,
            ToolName = tool.Name,
            ToolCallId = preparedContext.Request.CallId,
            ArgumentsJson = argumentsJson,
            IsDestructive = outcome.Receipt.IsDestructive,
            ToolContext = preparedContext.ToPayload(),
            OperationId = operationId,
        };
    }

    private static T InvokePrivateInstance<T>(MethodInfo method, object instance, params object?[] args)
    {
        try
        {
            return (T)method.Invoke(instance, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }
    private static T? GetProperty<T>(object instance, string propertyName)
    {
        return (T?)instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(instance);
    }
    private sealed class TestRoleGAgent(
        IAgentToolExecutionPort toolExecutionPort,
        ILLMProviderFactory? llmProviderFactory,
        IRemoteToolApprovalPort? remoteToolApprovalPort,
        IRemoteToolApprovalNotificationPort? remoteToolApprovalNotificationPort,
        IEnumerable<IAgentToolSource> toolSources,
        ISecretVault chatToolRecoverySecretVault)
        : RoleGAgent(
            toolExecutionPort: toolExecutionPort,
            llmProviderFactory: llmProviderFactory,
            toolSources: toolSources,
            remoteToolApprovalPort: remoteToolApprovalPort,
            remoteToolApprovalNotificationPort: remoteToolApprovalNotificationPort,
            chatToolRecoverySecretVault: chatToolRecoverySecretVault)
    {
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information)
                Messages.Add(formatter(state, exception));
        }
    }
    private sealed class StubRemoteApprovalPort(
        Func<RemoteToolApprovalRequest, Task<RemoteToolApprovalSubmission>> submit,
        Func<RemoteToolApprovalStatusQuery, Task<RemoteToolApprovalStatusSnapshot>> status)
        : IRemoteToolApprovalPort
    {
        public List<RemoteToolApprovalRequest> Submitted { get; } = [];
        public List<RemoteToolApprovalStatusQuery> StatusQueries { get; } = [];
        public List<RemoteToolApprovalDecision> Decisions { get; } = [];
        public Task<RemoteToolApprovalSubmission> SubmitAsync(RemoteToolApprovalRequest request, CancellationToken ct)
        {
            Submitted.Add(request);
            return submit(request);
        }
        public Task<RemoteToolApprovalStatusSnapshot> GetStatusAsync(RemoteToolApprovalStatusQuery query, CancellationToken ct)
        {
            StatusQueries.Add(query);
            return status(query);
        }
        public Task<RemoteToolApprovalDecisionResult> DecideAsync(RemoteToolApprovalDecision decision, CancellationToken ct)
        {
            Decisions.Add(decision);
            return Task.FromResult(new RemoteToolApprovalDecisionResult(true));
        }
    }
    private sealed class StubRemoteApprovalNotificationPort(
        Func<RemoteToolApprovalNotification, Task> notify)
        : IRemoteToolApprovalNotificationPort
    {
        public List<RemoteToolApprovalNotification> Notifications { get; } = [];
        public Task NotifyAsync(RemoteToolApprovalNotification notification, CancellationToken ct)
        {
            Notifications.Add(notification);
            return notify(notification);
        }
    }
    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            TimeoutRequests.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                TimeoutRequests.Count,
                RuntimeCallbackBackend.InMemory));
        }
        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));
        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }
    private sealed class DelegateTool(string name, Func<string, string> execute) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;
        public AgentToolReceipt? CreateSuccessReceipt(
            string callId,
            string toolName,
            string resultJson) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = resultJson,
            };

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(execute(argumentsJson));
        }
    }
    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<IMessage> Published { get; } = [];
        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = direction;
            _ = ct;
            _ = sourceEnvelope;
            _ = options;
            Published.Add(evt);
            return Task.CompletedTask;
        }
        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            return PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
        }
        public Task PublishCommittedStateEventAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
        {
            _ = audience;
            return PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
        }
    }
    private sealed class ThrowingEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            if (evt is RoleChatRecoveryContinuationRequested)
                throw new InvalidOperationException("dispatch failed with bearer-secret credential");
            _ = direction;
            _ = ct;
            _ = sourceEnvelope;
            _ = options;
            return Task.CompletedTask;
        }
        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            _ = evt;
            _ = ct;
            _ = sourceEnvelope;
            _ = options;
            throw new InvalidOperationException("dispatch failed with bearer-secret credential");
        }
        public Task PublishCommittedStateEventAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
        {
            _ = evt;
            _ = audience;
            _ = ct;
            _ = sourceEnvelope;
            _ = options;
            return Task.CompletedTask;
        }
    }
}
