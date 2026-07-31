using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed partial class RoleGAgentStateCoverageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleToolApprovalDecision_WhenRequestIsNotPending_ShouldCommitContinuationFailure(
        bool hasDifferentPendingRequest)
    {
        using var provider = BuildServiceProvider();
        var actorId = $"role-approval-stale-{hasDifferentPendingRequest}";
        var agent = CreateRoleAgent(provider, actorId);
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleId = "approval-role",
            RoleName = "approval worker",
        });
        if (hasDifferentPendingRequest)
        {
            agent.State.PendingApproval = new PendingToolApprovalState
            {
                RequestId = "req-current",
                SessionId = "turn-original",
                ScopeId = "scope-a",
                ToolName = "dangerous_tool",
                ToolCallId = "call-1",
                ArgumentsJson = "{}",
            };
        }

        await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = "req-stale",
            ContinuationTurnId = "turn-stale-decision",
            Approved = true,
        });

        if (hasDifferentPendingRequest)
            agent.State.PendingApproval!.RequestId.Should().Be("req-current");
        else
            agent.State.PendingApproval.Should().BeNull();

        var store = provider.GetRequiredService<IEventStore>();
        var completed = (await store.GetEventsAsync(actorId))
            .Where(x => x.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should()
            .ContainSingle()
            .Which;
        completed.SessionId.Should().Be("turn-stale-decision");
        completed.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
        completed.FailureCode.Should().Be("APPROVAL_REQUEST_NOT_PENDING");
        completed.SafeMessage.Should().Be("This approval request is no longer pending.");
        completed.ToString().Should().NotContain("req-current").And.NotContain("req-stale");
    }

    [Fact]
    public async Task HandleToolApprovalDecision_ShouldClearPending_WhenDenied()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-approval-denied");
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleId = "approval-role",
            RoleName = "approval worker",
        });
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
            RequestId = "req-1",
            ContinuationTurnId = "turn-denial",
            Approved = false,
            Reason = "user denied",
        });

        agent.State.PendingApproval.Should().BeNull();
        agent.State.Sessions.Should().NotContainKey("turn-original");
        agent.State.Sessions["turn-denial"].Completed.Should().BeTrue();
        agent.State.Sessions["turn-denial"].FinalContent.Should().Contain("approval_denied: user denied");

        var persistedCompletion = provider.GetRequiredService<IEventStore>() as InMemoryEventStoreForTests;
        persistedCompletion.Should().NotBeNull();
        var completed = (await persistedCompletion!.GetEventsAsync("role-approval-denied"))
            .Single(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal))
            .EventData
            .Unpack<RoleChatSessionCompletedEvent>();
        completed.RoleId.Should().Be("approval-role");
        completed.Content.Should().Contain("approval_denied: user denied");
    }

    [Fact]
    public async Task HandleToolApprovalDecision_WhenAdmissionStoreIsRetryable_ShouldRecoverExactContinuationAcrossReactivation()
    {
        var timeline = new List<string>();
        var eventStore = new TimelineEventStore(new InMemoryEventStoreForTests(), timeline);
        var auditTrail = new ScriptedAuditTrail(timeline: timeline);
        var admissionLedger = new ScriptedAdmissionLedger(
            [AgentToolAdmissionStatus.StoreUnavailable, AgentToolAdmissionStatus.Started],
            timeline);
        var recordingPort = new RecordingExecutionPort(
            new AdmittedAgentToolExecutor(
                admissionLedger,
                auditTrail,
                new StableIdentityHasher()));
        using var provider = BuildServiceProvider(
            auditTrail,
            eventStore,
            recordingPort,
            admissionLedger);
        var terminalCalls = 0;
        var tool = new DelegateTool("dangerous_tool", argumentsJson =>
        {
            terminalCalls++;
            return $"RESULT:{argumentsJson}";
        });
        var actorId = "role-approval-running-audit-retry";
        var pending = await CreatePendingApprovalAsync(
            provider,
            tool,
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-retry", "call-retry"),
                Caller = new AgentToolCallerContext("scope-retry", "owner-retry", "response-retry"),
                ExecutionOwner = AgentToolExecutionOwners.Actor(actorId),
            },
            "{\"value\":1}");
        await PersistPendingApprovalAsync(eventStore, actorId, pending);
        recordingPort.Requests.Clear();
        auditTrail.Records.Clear();
        timeline.Clear();

        var agent = CreateRoleAgent(provider, actorId, toolSources: [new StaticToolSource([tool])]);
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        var exactPending = agent.State.PendingApproval.Clone();
        var decision = new ToolApprovalDecisionEvent
        {
            RequestId = exactPending.RequestId,
            ContinuationTurnId = "turn-approval-retry",
            Approved = true,
        };

        await FluentActions.Invoking(() => agent.HandleToolApprovalDecision(decision))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("The durable tool admission ledger is unavailable.");

        terminalCalls.Should().Be(0);
        admissionLedger.Attempts.Should().Be(1);
        auditTrail.RunningAttempts.Should().Be(0);
        auditTrail.TerminalAttempts.Should().Be(0);
        auditTrail.Records.Should().BeEmpty();
        agent.State.PendingApproval.Should().BeEquivalentTo(exactPending);
        agent.State.Sessions.Should().NotContainKey("turn-approval-retry");
        AssertExactApprovalGrant(recordingPort.Requests.Should().ContainSingle().Which, exactPending);
        var afterRetryableFailure = await eventStore.GetEventsAsync(actorId);
        afterRetryableFailure.Count(x => x.EventData.Is(RoleChatSessionCompletedEvent.Descriptor)).Should().Be(0);
        afterRetryableFailure.Count(x => x.EventData.Is(ClearPendingApprovalEvent.Descriptor)).Should().Be(0);

        var reactivated = CreateRoleAgent(provider, actorId, toolSources: [new StaticToolSource([tool])]);
        reactivated.EventPublisher = publisher;
        await reactivated.ActivateAsync();
        reactivated.State.PendingApproval.Should().NotBeNull();
        reactivated.State.PendingApproval!.RequestId.Should().Be(exactPending.RequestId);
        reactivated.State.PendingApproval.ToolName.Should().Be("dangerous_tool");
        reactivated.State.PendingApproval.ToolCallId.Should().Be("call-retry");
        var recoveredContext = AgentToolExecutionContextMapper.FromPayload(
            reactivated.State.PendingApproval.ToolContext);
        recoveredContext.Request.RequestId.Should().Be("request-retry");
        AgentToolArgumentsDigest.ComputeSha256(reactivated.State.PendingApproval.ArgumentsJson).Should()
            .Be(AgentToolArgumentsDigest.ComputeSha256(exactPending.ArgumentsJson));
        recordingPort.Requests.Clear();
        timeline.Clear();

        await reactivated.HandleToolApprovalDecision(decision);

        terminalCalls.Should().Be(1);
        admissionLedger.Attempts.Should().Be(2);
        auditTrail.RunningAttempts.Should().Be(1);
        auditTrail.TerminalAttempts.Should().Be(1);
        reactivated.State.PendingApproval.Should().BeNull();
        AssertExactApprovalGrant(recordingPort.Requests.Should().ContainSingle().Which, exactPending);
        publisher.Published.OfType<ChatRequestEvent>().Should().ContainSingle(x =>
            x.SessionId == "turn-approval-retry" &&
            x.Prompt.Contains("RESULT:{\"value\":1}"));
        (await eventStore.GetEventsAsync(actorId)).Count(x =>
            x.EventData.Is(ClearPendingApprovalEvent.Descriptor)).Should().Be(1);
        timeline.Should().Equal(
            "admission:Started",
            "audit:running:Appended",
            "audit:terminal:Appended",
            "event:ClearPendingApprovalEvent");

        var finalActivation = CreateRoleAgent(provider, actorId, toolSources: [new StaticToolSource([tool])]);
        finalActivation.EventPublisher = new RecordingEventPublisher();
        await finalActivation.ActivateAsync();
        finalActivation.State.PendingApproval.Should().BeNull();
        await finalActivation.HandleToolApprovalDecision(decision);
        terminalCalls.Should().Be(1);
        admissionLedger.Attempts.Should().Be(2);
        auditTrail.RunningAttempts.Should().Be(1);
    }

    [Theory]
    [InlineData(AgentToolAdmissionStatus.Duplicate)]
    [InlineData(AgentToolAdmissionStatus.Conflict)]
    public async Task HandleToolApprovalDecision_WhenAdmissionForbidsReplay_ShouldPersistFailureThenClearOnce(
        AgentToolAdmissionStatus admissionStatus)
    {
        var auditTrail = new ScriptedAuditTrail();
        var admissionLedger = new ScriptedAdmissionLedger([admissionStatus]);
        using var provider = BuildServiceProvider(auditTrail, admissionLedger: admissionLedger);
        var terminalCalls = 0;
        var tool = new DelegateTool("dangerous_tool", _ =>
        {
            terminalCalls++;
            return "{\"ok\":true}";
        });
        var actorId = $"role-approval-admission-{admissionStatus}";
        var pending = await CreatePendingApprovalAsync(
            provider,
            tool,
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-no-replay", "call-no-replay"),
                ExecutionOwner = AgentToolExecutionOwners.Actor(actorId),
            });
        var eventStore = provider.GetRequiredService<IEventStore>();
        await PersistPendingApprovalAsync(eventStore, actorId, pending);
        auditTrail.Records.Clear();
        var agent = CreateRoleAgent(provider, actorId, toolSources: [new StaticToolSource([tool])]);
        await agent.ActivateAsync();
        var decision = new ToolApprovalDecisionEvent
        {
            RequestId = agent.State.PendingApproval.RequestId,
            ContinuationTurnId = $"turn-no-replay-{admissionStatus}",
            Approved = true,
        };

        await FluentActions.Invoking(() => agent.HandleToolApprovalDecision(decision))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        agent.State.PendingApproval.Should().BeNull();
        terminalCalls.Should().Be(0);
        admissionLedger.Attempts.Should().Be(1);
        auditTrail.RunningAttempts.Should().Be(0);
        auditTrail.TerminalAttempts.Should().Be(0);
        auditTrail.Records.Should().BeEmpty();
        await AssertFailureThenSingleClearAsync(eventStore, actorId);

        var reactivated = CreateRoleAgent(provider, actorId, toolSources: [new StaticToolSource([tool])]);
        await reactivated.ActivateAsync();
        reactivated.State.PendingApproval.Should().BeNull();
        await reactivated.HandleToolApprovalDecision(decision);
        terminalCalls.Should().Be(0);
        admissionLedger.Attempts.Should().Be(1);
        auditTrail.RunningAttempts.Should().Be(0);
    }

    [Fact]
    public async Task HandleToolApprovalDecision_WhenFailureIsNonRetryable_ShouldPersistFailureThenClearOnce()
    {
        var auditTrail = new ScriptedAuditTrail();
        using var provider = BuildServiceProvider(auditTrail);
        var tool = new ClassificationFailingTool("dangerous_tool");
        var actorId = "role-approval-non-retryable";
        var pending = await CreatePendingApprovalAsync(
            provider,
            tool,
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-non-retryable", "call-non-retryable"),
                ExecutionOwner = AgentToolExecutionOwners.Actor(actorId),
            });
        var eventStore = provider.GetRequiredService<IEventStore>();
        await PersistPendingApprovalAsync(eventStore, actorId, pending);
        tool.FailClassification = true;
        var agent = CreateRoleAgent(provider, actorId, toolSources: [new StaticToolSource([tool])]);
        await agent.ActivateAsync();

        await FluentActions.Invoking(() => agent.HandleToolApprovalDecision(Approved(pending)))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        tool.ExecutionCalls.Should().Be(0);
        auditTrail.RunningAttempts.Should().Be(0);
        auditTrail.TerminalAttempts.Should().Be(1);
        await AssertFailureThenSingleClearAsync(eventStore, actorId);
        await AssertConsumedAfterReactivationAsync(provider, actorId, tool, Approved(pending));
        tool.ExecutionCalls.Should().Be(0);
    }

    [Fact]
    public async Task HandleToolApprovalDecision_WhenTerminalExecutionFails_ShouldPersistFailureThenClearOnce()
    {
        var auditTrail = new ScriptedAuditTrail();
        using var provider = BuildServiceProvider(auditTrail);
        var terminalCalls = 0;
        var tool = new DelegateTool("dangerous_tool", _ =>
        {
            terminalCalls++;
            throw new InvalidOperationException("provider-secret-terminal-failure");
        });
        var actorId = "role-approval-terminal-failure";
        var pending = await CreatePendingApprovalAsync(
            provider,
            tool,
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-terminal-failure", "call-terminal-failure"),
                ExecutionOwner = AgentToolExecutionOwners.Actor(actorId),
            });
        var eventStore = provider.GetRequiredService<IEventStore>();
        await PersistPendingApprovalAsync(eventStore, actorId, pending);
        var agent = CreateRoleAgent(provider, actorId, toolSources: [new StaticToolSource([tool])]);
        await agent.ActivateAsync();

        await FluentActions.Invoking(() => agent.HandleToolApprovalDecision(Approved(pending)))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(nameof(InvalidOperationException));

        terminalCalls.Should().Be(1);
        auditTrail.RunningAttempts.Should().Be(1);
        auditTrail.TerminalAttempts.Should().Be(1);
        await AssertFailureThenSingleClearAsync(eventStore, actorId);
        await AssertConsumedAfterReactivationAsync(provider, actorId, tool, Approved(pending));
        terminalCalls.Should().Be(1);
    }

    [Theory]
    [InlineData(AuditTrailAppendStatus.Appended, "continuation-dispatch")]
    [InlineData(AuditTrailAppendStatus.StoreUnavailable, "audit-incomplete-follow-up")]
    public async Task HandleToolApprovalDecision_WhenPostTerminalDispatchFails_ShouldPersistFailureThenClearOnce(
        AuditTrailAppendStatus terminalStatus,
        string scenario)
    {
        _ = scenario;
        var timeline = new List<string>();
        var eventStore = new TimelineEventStore(new InMemoryEventStoreForTests(), timeline);
        var auditTrail = new ScriptedAuditTrail([], terminalStatus, timeline);
        using var provider = BuildServiceProvider(auditTrail, eventStore);
        var terminalCalls = 0;
        var tool = new DelegateTool("dangerous_tool", _ =>
        {
            terminalCalls++;
            return "{\"ok\":true}";
        });
        var actorId = $"role-approval-{scenario}";
        var pending = await CreatePendingApprovalAsync(
            provider,
            tool,
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity($"request-{scenario}", $"call-{scenario}"),
                ExecutionOwner = AgentToolExecutionOwners.Actor(actorId),
            });
        await PersistPendingApprovalAsync(eventStore, actorId, pending);
        timeline.Clear();
        var agent = CreateRoleAgent(provider, actorId, toolSources: [new StaticToolSource([tool])]);
        agent.EventPublisher = new ThrowingEventPublisher();
        await agent.ActivateAsync();

        await FluentActions.Invoking(() => agent.HandleToolApprovalDecision(Approved(pending)))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch failed with bearer-secret credential");

        terminalCalls.Should().Be(1);
        auditTrail.RunningAttempts.Should().Be(1);
        auditTrail.TerminalAttempts.Should().Be(1);
        await AssertFailureThenSingleClearAsync(eventStore, actorId);
        timeline.Should().Equal(
            "audit:running:Appended",
            $"audit:terminal:{terminalStatus}",
            "event:RoleChatSessionCompletedEvent",
            "event:ClearPendingApprovalEvent");
        await AssertConsumedAfterReactivationAsync(provider, actorId, tool, Approved(pending));
        terminalCalls.Should().Be(1);
    }

    [Fact]
    public async Task HandleToolApprovalDecision_WhenTerminalStartedAndDispatchSucceeds_ShouldCommitTerminalBeforeSingleClear()
    {
        var timeline = new List<string>();
        var eventStore = new TimelineEventStore(new InMemoryEventStoreForTests(), timeline);
        var auditTrail = new ScriptedAuditTrail([], AuditTrailAppendStatus.Appended, timeline);
        using var provider = BuildServiceProvider(auditTrail, eventStore);
        var terminalCalls = 0;
        var tool = new DelegateTool("dangerous_tool", _ =>
        {
            terminalCalls++;
            return "{\"ok\":true}";
        });
        var actorId = "role-approval-terminal-started";
        var pending = await CreatePendingApprovalAsync(
            provider,
            tool,
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-terminal-started", "call-terminal-started"),
                ExecutionOwner = AgentToolExecutionOwners.Actor(actorId),
            });
        await PersistPendingApprovalAsync(eventStore, actorId, pending);
        timeline.Clear();
        var agent = CreateRoleAgent(provider, actorId, toolSources: [new StaticToolSource([tool])]);
        agent.EventPublisher = new RecordingEventPublisher();
        await agent.ActivateAsync();

        await agent.HandleToolApprovalDecision(Approved(pending));

        terminalCalls.Should().Be(1);
        auditTrail.TerminalAttempts.Should().Be(1);
        (await eventStore.GetEventsAsync(actorId)).Count(x =>
            x.EventData.Is(ClearPendingApprovalEvent.Descriptor)).Should().Be(1);
        timeline.Should().Equal(
            "audit:running:Appended",
            "audit:terminal:Appended",
            "event:ClearPendingApprovalEvent");
        await AssertConsumedAfterReactivationAsync(provider, actorId, tool, Approved(pending));
        terminalCalls.Should().Be(1);
    }

    private static ToolApprovalDecisionEvent Approved(PendingToolApprovalState pending) => new()
    {
        RequestId = pending.RequestId,
        ContinuationTurnId = $"turn-{pending.ToolCallId}",
        Approved = true,
    };

    private static async Task PersistPendingApprovalAsync(
        IEventStore eventStore,
        string actorId,
        PendingToolApprovalState pending)
    {
        var persisted = new PendingToolApprovalPersistedEvent { Pending = pending.Clone() };
        await eventStore.AppendAsync(
            actorId,
            [
                new StateEvent
                {
                    EventId = $"pending-{pending.RequestId}",
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Version = 1,
                    EventType = PendingToolApprovalPersistedEvent.Descriptor.FullName,
                    EventData = Any.Pack(persisted),
                    AgentId = actorId,
                },
            ],
            expectedVersion: 0);
    }

    private static void AssertExactApprovalGrant(
        AgentToolExecutionRequest request,
        PendingToolApprovalState pending)
    {
        var pendingContext = AgentToolExecutionContextMapper.FromPayload(pending.ToolContext);
        request.ArgumentsJson.Should().Be(pending.ArgumentsJson);
        request.ApprovalContinuationMode.Should().Be(AgentToolApprovalContinuationMode.ActorOwned);
        request.ApprovalGrant.Should().NotBeNull();
        request.ApprovalGrant!.ExecutionOwner.Should().BeEquivalentTo(pendingContext.ExecutionOwner);
        request.ApprovalGrant!.ApprovalRequestId.Should().Be(pending.RequestId);
        request.ApprovalGrant.RequestId.Should().Be(pendingContext.Request.RequestId);
        request.ApprovalGrant.ToolName.Should().Be(pending.ToolName);
        request.ApprovalGrant.ToolCallId.Should().Be(pending.ToolCallId);
        request.ApprovalGrant.ArgumentsSha256.Should().Be(
            AgentToolArgumentsDigest.ComputeSha256(pending.ArgumentsJson));
    }

    private static async Task AssertFailureThenSingleClearAsync(IEventStore eventStore, string actorId)
    {
        var events = await eventStore.GetEventsAsync(actorId);
        events.Count(x => x.EventData.Is(RoleChatSessionCompletedEvent.Descriptor)).Should().Be(1);
        events.Count(x => x.EventData.Is(ClearPendingApprovalEvent.Descriptor)).Should().Be(1);
        events.Select(x => x.EventData.TypeUrl).Should().Equal(
            Any.Pack(new PendingToolApprovalPersistedEvent()).TypeUrl,
            Any.Pack(new RoleChatSessionCompletedEvent()).TypeUrl,
            Any.Pack(new ClearPendingApprovalEvent()).TypeUrl);
    }

    private static async Task AssertConsumedAfterReactivationAsync(
        IServiceProvider provider,
        string actorId,
        IAgentTool tool,
        ToolApprovalDecisionEvent decision)
    {
        var reactivated = CreateRoleAgent(provider, actorId, toolSources: [new StaticToolSource([tool])]);
        reactivated.EventPublisher = new RecordingEventPublisher();
        await reactivated.ActivateAsync();
        reactivated.State.PendingApproval.Should().BeNull();
        await reactivated.HandleToolApprovalDecision(decision);
    }

    private sealed class ScriptedAuditTrail(
        AuditTrailAppendStatus[]? runningStatuses = null,
        AuditTrailAppendStatus terminalStatus = AuditTrailAppendStatus.Appended,
        List<string>? timeline = null) : IAuditTrailAppender
    {
        private readonly AuditTrailAppendStatus[] _runningStatuses = runningStatuses ?? [];
        private int _runningAttempts;
        private int _terminalAttempts;

        public int RunningAttempts => _runningAttempts;
        public int TerminalAttempts => _terminalAttempts;
        public List<AuditRecord> Records { get; } = [];

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add(record);
            var phase = record.ToolExecution.ExecutionPhase switch
            {
                AuditToolExecutionPhase.Running => "running",
                AuditToolExecutionPhase.Terminal => "terminal",
                AuditToolExecutionPhase.WaitingApproval => "waiting_approval",
                _ => "unspecified",
            };
            var status = record.ToolExecution.ExecutionPhase switch
            {
                AuditToolExecutionPhase.Running => NextRunningStatus(),
                AuditToolExecutionPhase.Terminal => CountTerminalAttempt(),
                _ => AuditTrailAppendStatus.Appended,
            };
            timeline?.Add($"audit:{phase}:{status}");
            return Task.FromResult(status switch
            {
                AuditTrailAppendStatus.Appended => AuditTrailAppendResult.Appended(record.AuditId),
                AuditTrailAppendStatus.Duplicate => AuditTrailAppendResult.Duplicate(record.AuditId),
                AuditTrailAppendStatus.Conflict => AuditTrailAppendResult.Conflict(record.AuditId, "conflict"),
                _ => AuditTrailAppendResult.StoreUnavailable(record.AuditId, "offline"),
            });
        }

        private AuditTrailAppendStatus NextRunningStatus()
        {
            var index = _runningAttempts++;
            return _runningStatuses.Length == 0
                ? AuditTrailAppendStatus.Appended
                : index < _runningStatuses.Length
                    ? _runningStatuses[index]
                    : _runningStatuses[^1];
        }

        private AuditTrailAppendStatus CountTerminalAttempt()
        {
            _terminalAttempts++;
            return terminalStatus;
        }
    }

    private sealed class RecordingExecutionPort(IAgentToolExecutionPort inner) : IAgentToolExecutionPort
    {
        public List<AgentToolExecutionRequest> Requests { get; } = [];

        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return inner.ExecuteAsync(request, ct);
        }
    }

    private sealed class ScriptedAdmissionLedger(
        AgentToolAdmissionStatus[] statuses,
        List<string>? timeline = null) : IAgentToolAdmissionLedger
    {
        private int _attempts;

        public int Attempts => _attempts;

        public Task<AgentToolAdmissionResult> TryStartAsync(
            AgentToolAdmissionFact fact,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(fact);
            ct.ThrowIfCancellationRequested();
            var index = _attempts++;
            var admissionStatus = index < statuses.Length ? statuses[index] : statuses[^1];
            timeline?.Add($"admission:{admissionStatus}");
            return Task.FromResult(new AgentToolAdmissionResult(admissionStatus));
        }
    }

    private sealed class ClassificationFailingTool(string name) : IAgentTool
    {
        public bool FailClassification { get; set; }
        public int ExecutionCalls { get; private set; }
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

        public AgentToolCallSafety GetCallSafety(string argumentsJson) =>
            FailClassification
                ? throw new InvalidOperationException("classification failed")
                : new AgentToolCallSafety(true, false, true);

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecutionCalls++;
            return Task.FromResult("{\"ok\":true}");
        }
    }

    private sealed class TimelineEventStore(IEventStore inner, List<string> timeline) : IEventStore
    {
        public async Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.ToArray();
            var result = await inner.AppendAsync(agentId, batch, expectedVersion, ct);
            timeline.AddRange(batch.Select(x => $"event:{x.EventType.Split('.').Last()}"));
            return result;
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }
}
