using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Google.Protobuf.WellKnownTypes;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowProjectionDispatchFailureReporterTests
{
    [Fact]
    public async Task ReportAsync_ShouldPublishCustomFailureEventToSessionStream()
    {
        var hub = new CapturingRunEventHub();
        var clock = new FixedProjectionClock(new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero));
        var reporter = new WorkflowProjectionDispatchFailureReporter(hub, clock);
        var context = BuildContext();
        var envelope = new EventEnvelope
        {
            Id = "evt-1",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        };

        await reporter.ReportAsync(context, envelope, new InvalidOperationException("boom"), CancellationToken.None);

        hub.Published.Should().ContainSingle();
        hub.Published[0].ScopeId.Should().Be(context.RootActorId);
        hub.Published[0].SessionId.Should().Be(context.SessionId);
        hub.Published[0].Event.EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.Custom);
        hub.Published[0].Event.Custom.Name.Should().Be(WorkflowProjectionDispatchFailureReporter.ProjectionDispatchFailureEventName);
        var payload = hub.Published[0].Event.Custom.Payload.Unpack<WorkflowProjectionDispatchFailureCustomPayload>();
        payload.EventId.Should().Be("evt-1");
        payload.PayloadType.Should().Contain("google.protobuf.StringValue");
        payload.Reason.Should().Be("boom");
        hub.Published[0].Event.Timestamp.Should().Be(clock.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ReportAsync_ShouldSkipPublish_WhenContextIdsAreMissing()
    {
        var hub = new CapturingRunEventHub();
        var reporter = new WorkflowProjectionDispatchFailureReporter(hub, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = BuildContext(string.Empty);

        await reporter.ReportAsync(
            context,
            new EventEnvelope { Id = "evt-1", Payload = Any.Pack(new StringValue { Value = "payload" }) },
            new InvalidOperationException("boom"),
            CancellationToken.None);

        hub.Published.Should().BeEmpty();
    }

    private static WorkflowExecutionProjectionContext BuildContext(string sessionId = "cmd-1") => new()
    {
        SessionId = sessionId,
        RootActorId = "actor-1",
        ProjectionKind = "workflow-execution",
    };
}

internal sealed class CapturingRunEventHub : IProjectionSessionEventHub<WorkflowRunEventEnvelope>
{
    public List<(string ScopeId, string SessionId, WorkflowRunEventEnvelope Event)> Published { get; } = [];

    public Task PublishAsync(
        string scopeId,
        string sessionId,
        WorkflowRunEventEnvelope evt,
        CancellationToken ct = default)
    {
        Published.Add((scopeId, sessionId, evt));
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> SubscribeAsync(
        string scopeId,
        string sessionId,
        Func<WorkflowRunEventEnvelope, ValueTask> handler,
        CancellationToken ct = default)
    {
        return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
    }
}

internal sealed class FixedProjectionClock : IProjectionClock
{
    public FixedProjectionClock(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; }
}

internal sealed class NoopAsyncDisposable : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
