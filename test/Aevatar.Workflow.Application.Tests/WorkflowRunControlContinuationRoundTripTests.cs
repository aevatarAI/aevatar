using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunControlContinuationRoundTripTests
{
    [Fact]
    public async Task SignalEnvelopeFactory_ShouldResumeWaitSignalModuleRoundTrip()
    {
        var module = new WaitSignalModule();
        var context = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-codex-result",
                StepType = "wait_signal",
                RunId = "run-signal-roundtrip",
                Input = "fallback",
                Parameters =
                {
                    ["signal_name"] = "codex_worker_done",
                    ["timeout_ms"] = "86400000",
                },
            }),
            context,
            CancellationToken.None);

        context.Published.Clear();

        var envelope = new WorkflowSignalCommandEnvelopeFactory().CreateEnvelope(
            new WorkflowSignalCommand(
                "actor-1",
                "run-signal-roundtrip",
                "codex_worker_done",
                "signal-cmd-1",
                "worker-output",
                "wait-codex-result"),
            new CommandContext("actor-1", "signal-cmd-1", "corr-signal-1", new Dictionary<string, string>()));

        await module.HandleAsync(envelope, context, CancellationToken.None);

        var completion = context.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completion.StepId.Should().Be("wait-codex-result");
        completion.RunId.Should().Be("run-signal-roundtrip");
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be("worker-output");
    }

    [Fact]
    public async Task SignalEnvelopeFactory_ShouldResumeExternalApprovalWaitSignalRoundTrip()
    {
        var module = new WaitSignalModule();
        var context = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-approval",
                StepType = "wait_signal",
                RunId = "run-approval-roundtrip",
                StepParameters = new WorkflowStepParameters
                {
                    ExternalApproval = new WorkflowExternalApprovalWaitOptions
                    {
                        SourceId = "nyxid",
                        ExternalIdKind = "instance_code",
                        ExternalId = "app-42",
                        SignalName = "approval-terminal",
                        CallbackIdempotencyKey = "idem-42",
                    },
                },
            }),
            context,
            CancellationToken.None);

        context.Published.Clear();

        var terminal = new WorkflowExternalApprovalTerminalSignalCommand(
            "nyxid",
            "instance_code",
            "app-42",
            "",
            "",
            "APPROVED",
            "idem-42");
        var envelope = new WorkflowSignalCommandEnvelopeFactory().CreateEnvelope(
            new WorkflowSignalCommand(
                "actor-1",
                "run-approval-roundtrip",
                "approval-terminal",
                "external-approval:nyxid:instance_code:app-42:APPROVED",
                terminal.TerminalStatus,
                "wait-approval",
                "external-approval:nyxid:instance_code:app-42:APPROVED",
                terminal),
            new CommandContext(
                "actor-1",
                "external-approval:nyxid:instance_code:app-42:APPROVED",
                "external-approval:nyxid:instance_code:app-42:APPROVED",
                new Dictionary<string, string>()));

        var signal = envelope.Payload.Unpack<SignalReceivedEvent>();
        signal.ExternalApproval.Should().NotBeNull();
        signal.ExternalApproval.TerminalStatus.Should().Be("APPROVED");
        signal.ExternalApproval.CallbackIdempotencyKey.Should().Be("idem-42");

        await module.HandleAsync(envelope, context, CancellationToken.None);

        var completion = context.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completion.StepId.Should().Be("wait-approval");
        completion.RunId.Should().Be("run-approval-roundtrip");
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be("APPROVED");
        context.Published.Select(x => x.Event).OfType<WorkflowExternalApprovalContinuationClearedEvent>()
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task ResumeEnvelopeFactory_ShouldResumeHumanApprovalModuleRoundTrip()
    {
        var module = new HumanApprovalModule();
        var context = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "approval-gate",
                StepType = "human_approval",
                RunId = "run-resume-roundtrip",
                Input = "draft-content",
                Parameters =
                {
                    ["prompt"] = "Approve the draft?",
                    ["timeout"] = "5400",
                },
            }),
            context,
            CancellationToken.None);

        context.Published.Clear();

        var envelope = new WorkflowResumeCommandEnvelopeFactory().CreateEnvelope(
            new WorkflowResumeCommand(
                "actor-1",
                "run-resume-roundtrip",
                "approval-gate",
                "resume-cmd-1",
                true,
                "approved by operator",
                EditedContent: "approved revision",
                Feedback: "looks good"),
            new CommandContext("actor-1", "resume-cmd-1", "corr-resume-1", new Dictionary<string, string>()));

        await module.HandleAsync(envelope, context, CancellationToken.None);

        var completion = context.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completion.StepId.Should().Be("approval-gate");
        completion.RunId.Should().Be("run-resume-roundtrip");
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be("approved revision");
    }

    private static EventEnvelope Envelope(IMessage evt) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };

    private sealed class RecordingWorkflowContext : IWorkflowExecutionContext
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);

        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "workflow-agent";

        public string RunId => "workflow-run";

        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

        public long GetTimestamp() => 1;

        public TimeSpan GetElapsedTime(long startingTimestamp)
        {
            _ = startingTimestamp;
            return TimeSpan.Zero;
        }

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            if (!_states.TryGetValue(scopeKey, out var packed) || !packed.Is(new TState().Descriptor))
                return new TState();

            return packed.Unpack<TState>() ?? new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            _states
                .Where(x => string.IsNullOrEmpty(scopeKeyPrefix) || x.Key.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
                .Where(x => x.Value.Is(new TState().Descriptor))
                .Select(x => new KeyValuePair<string, TState>(x.Key, x.Value.Unpack<TState>() ?? new TState()))
                .ToList();

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            ct.ThrowIfCancellationRequested();
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            _ = options;
            Published.Add((evt, audience));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = callbackId;
            _ = dueTime;
            _ = evt;
            _ = options;
            return Task.FromResult(new RuntimeCallbackLease(AgentId, "callback", 1, RuntimeCallbackBackend.InMemory));
        }

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            throw new NotSupportedException();
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
