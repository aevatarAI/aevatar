using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class RoleGAgentRemoteApprovalEscalationTests
{
    [Fact]
    public async Task HandleToolApprovalTimeout_WhenDeliveryTargetIsUnsupported_ShouldNotSubmitRemoteApprovalOrSchedulePolling()
    {
        using var provider = BuildServiceProvider();
        var remotePort = new StubRemoteApprovalPort(
            submit: _ => throw new InvalidOperationException("unsupported target should not submit remote approval"),
            status: _ => throw new InvalidOperationException("status should not be called"));
        var notificationPort = new StubRemoteApprovalNotificationPort(
            context =>
            {
                context.Channel.DeliveryTargetId.Should().Be("telegram-delivery-1");
                return Task.FromResult(RemoteToolApprovalNotificationSupport.Unsupported(
                    "Remote tool approval notification is currently supported only for Lark delivery targets; platform 'telegram' is not supported."));
            },
            _ => throw new InvalidOperationException("unsupported target should not notify"));
        var agent = CreateRoleAgent(
            provider,
            "role-timeout-unsupported-target",
            remotePort,
            notificationPort);
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = """{"path":"/prod"}""",
            ToolContext = ToolContext(
                "telegram",
                "telegram-msg-1",
                "telegram-delivery-1").ToPayload(),
        };

        await agent.HandleToolApprovalTimeout(new ToolApprovalTimeoutFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
        });

        agent.State.PendingApproval.Should().BeNull();
        agent.State.Sessions["session-a"].Completed.Should().BeTrue();
        agent.State.Sessions["session-a"].FinalContent.Should()
            .Contain("approval_unsupported_channel")
            .And.Contain("platform 'telegram' is not supported");
        remotePort.Submitted.Should().BeEmpty();
        notificationPort.SupportChecks.Should().ContainSingle();
        notificationPort.Notifications.Should().BeEmpty();
        provider.GetRequiredService<RecordingRuntimeCallbackScheduler>()
            .TimeoutRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleToolApprovalTimeout_WhenLarkNotificationFails_ShouldKeepRemoteApprovalPolling()
    {
        using var provider = BuildServiceProvider();
        var remotePort = new StubRemoteApprovalPort(
            submit: request => Task.FromResult(new RemoteToolApprovalSubmission(
                "remote-1",
                DateTimeOffset.FromUnixTimeSeconds(1_800))),
            status: _ => throw new InvalidOperationException("status should not be called"));
        var notificationPort = new StubRemoteApprovalNotificationPort(
            context =>
            {
                context.Channel.DeliveryTargetId.Should().Be("lark-delivery-1");
                return Task.FromResult(RemoteToolApprovalNotificationSupport.SupportedResult);
            },
            _ => throw new InvalidOperationException("notification failed"));
        var agent = CreateRoleAgent(
            provider,
            "role-timeout-lark-notify-fails",
            remotePort,
            notificationPort);
        await agent.ActivateAsync();
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "session-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = """{"path":"/prod"}""",
            ToolContext = ToolContext(
                "lark",
                "om_1",
                "lark-delivery-1").ToPayload(),
        };

        await agent.HandleToolApprovalTimeout(new ToolApprovalTimeoutFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
        });

        agent.State.PendingApproval.Should().NotBeNull();
        agent.State.PendingApproval!.RemoteApprovalId.Should().Be("remote-1");
        remotePort.Submitted.Should().ContainSingle();
        notificationPort.SupportChecks.Should().ContainSingle();
        notificationPort.Notifications.Should().ContainSingle();
        provider.GetRequiredService<RecordingRuntimeCallbackScheduler>()
            .TimeoutRequests.Should().ContainSingle(x =>
                x.CallbackId == "tool-approval-remote-status-req-1-remote-1-1" &&
                x.ActorId == "role-timeout-lark-notify-fails");
    }

    [Fact]
    public async Task HandleRemoteApprovalStatusCheck_WhenCancelled_ShouldPersistTerminalFailureAndClearPending()
    {
        using var provider = BuildServiceProvider();
        var remotePort = new StubRemoteApprovalPort(
            submit: _ => throw new InvalidOperationException("submit should not be called"),
            status: _ => Task.FromResult(new RemoteToolApprovalStatusSnapshot(
                RemoteToolApprovalStatus.Cancelled,
                "cancelled remotely")));
        var notificationPort = new StubRemoteApprovalNotificationPort(
            _ => throw new InvalidOperationException("support should not be checked"),
            _ => throw new InvalidOperationException("notification should not be sent"));
        var agent = CreateRoleAgent(
            provider,
            "role-status-cancelled",
            remotePort,
            notificationPort);
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
            .Contain("approval_cancelled: cancelled remotely");
    }

    [Fact]
    public async Task HandleRemoteApprovalStatusCheck_WhenCancelled_ShouldDeliverCommittedRunTerminalFact()
    {
        using var provider = BuildServiceProvider();
        var remotePort = new StubRemoteApprovalPort(
            submit: _ => throw new InvalidOperationException("submit should not be called"),
            status: _ => Task.FromResult(new RemoteToolApprovalStatusSnapshot(
                RemoteToolApprovalStatus.Cancelled,
                "cancelled remotely")));
        var notificationPort = new StubRemoteApprovalNotificationPort(
            _ => throw new InvalidOperationException("support should not be checked"),
            _ => throw new InvalidOperationException("notification should not be sent"));
        var publisher = new RecordingEventPublisher();
        var agent = CreateRoleAgent(
            provider,
            "role-status-cancelled-run",
            remotePort,
            notificationPort);
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        var runContext = new RoleChatRunContext
        {
            RunId = "run-1",
            CommandId = "command-1",
            CorrelationId = "correlation-1",
            CompletionNotificationActorId = "service-run:scope-1:service-1:run-1",
        };
        await agent.PersistForTestAsync(new RoleChatSessionStartedEvent
        {
            SessionId = "session-a",
            Prompt = "perform approved work",
            RunContext = runContext.Clone(),
        });
        await agent.PersistForTestAsync(new PendingToolApprovalPersistedEvent
        {
            Pending = new PendingToolApprovalState
            {
                RequestId = "req-1",
                SessionId = "session-a",
                ToolName = "dangerous_tool",
                ToolCallId = "call-1",
                ArgumentsJson = "{}",
                RemoteApprovalId = "remote-1",
                RemoteStatusCheckAttempt = 1,
            },
        });

        await agent.HandleRemoteApprovalStatusCheck(new ToolApprovalRemoteStatusCheckFiredEvent
        {
            RequestId = "req-1",
            SessionId = "session-a",
            RemoteApprovalId = "remote-1",
            Attempt = 1,
        });

        agent.State.PendingApproval.Should().BeNull();
        agent.State.Sessions["session-a"].RunContext.Should().BeEquivalentTo(runContext);
        var sent = publisher.Sends.Should().ContainSingle().Subject;
        sent.TargetActorId.Should().Be(runContext.CompletionNotificationActorId);
        sent.Options!.Delivery!.OperationId.Should()
            .Be("role-chat-terminal:run-1:command-1:outcome:2");
        var terminal = sent.Event.Should().BeOfType<RoleChatSessionCompletedEvent>().Subject;
        terminal.ActorId.Should().Be("role-status-cancelled-run");
        terminal.RunContext.Should().BeEquivalentTo(runContext);
        terminal.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
        terminal.FailureCode.Should().Be("APPROVAL_CANCELLED");
        terminal.SafeMessage.Should().Be("cancelled remotely");
        terminal.TerminalTime.Should().NotBeNull();
    }

    private static ServiceProvider BuildServiceProvider()
    {
        return new ServiceCollection()
            .AddSingleton<IEventStore, InMemoryEventStoreForTests>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<RecordingRuntimeCallbackScheduler>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(sp => sp.GetRequiredService<RecordingRuntimeCallbackScheduler>())
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
    }

    private static TestRoleGAgent CreateRoleAgent(
        IServiceProvider provider,
        string actorId,
        IRemoteToolApprovalPort remoteToolApprovalPort,
        IRemoteToolApprovalNotificationPort remoteToolApprovalNotificationPort)
    {
        var agent = new TestRoleGAgent(remoteToolApprovalPort, remoteToolApprovalNotificationPort)
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };

        var setId = typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setId.Invoke(agent, [actorId]);
        return agent;
    }

    private static AgentToolExecutionContext ToolContext(
        string platform,
        string platformMessageId,
        string deliveryTargetId) =>
        AgentToolExecutionContext.Empty with
        {
            Channel = new AgentToolChannelContext(
                platform,
                "sender-1",
                "scope-1",
                "msg-1",
                platformMessageId,
                deliveryTargetId),
        };

    private sealed class TestRoleGAgent(
        IRemoteToolApprovalPort remoteToolApprovalPort,
        IRemoteToolApprovalNotificationPort remoteToolApprovalNotificationPort)
        : RoleGAgent(
            toolExecutionPort: TestAgentToolExecutionPort.Instance,
            llmProviderFactory: null,
            toolSources: [],
            remoteToolApprovalPort: remoteToolApprovalPort,
            remoteToolApprovalNotificationPort: remoteToolApprovalNotificationPort)
    {
        public Task PersistForTestAsync(IMessage evt) => PersistDomainEventAsync(evt);
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<(string TargetActorId, IMessage Event, EventEnvelopePublishOptions? Options)> Sends { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
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
            ct.ThrowIfCancellationRequested();
            Sends.Add((targetActorId, evt, options));
            return Task.CompletedTask;
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
        Func<AgentToolExecutionContext, Task<RemoteToolApprovalNotificationSupport>> checkSupport,
        Func<RemoteToolApprovalNotification, Task> notify)
        : IRemoteToolApprovalNotificationPort
    {
        public List<AgentToolExecutionContext> SupportChecks { get; } = [];
        public List<RemoteToolApprovalNotification> Notifications { get; } = [];

        public Task<RemoteToolApprovalNotificationSupport> CheckSupportAsync(
            AgentToolExecutionContext toolContext,
            CancellationToken ct)
        {
            SupportChecks.Add(toolContext);
            return checkSupport(toolContext);
        }

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
}
