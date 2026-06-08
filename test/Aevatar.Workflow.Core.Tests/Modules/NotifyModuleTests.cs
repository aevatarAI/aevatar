using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class NotifyModuleTests
{
    [Fact]
    public async Task HandleAsync_WhenInteractionSpecValid_ShouldPublishNotificationThenAcceptedCompletion()
    {
        var ctx = new RecordingWorkflowContext();
        var module = new NotifyModule();

        await module.HandleAsync(Envelope(BuildRequest(interaction: BuildInteractionSpec())), ctx, CancellationToken.None);

        ctx.Published.Should().HaveCount(2);
        ctx.Published[0].Direction.Should().Be(TopologyAudience.ParentAndChildren);
        var notification = ctx.Published[0].Event.Should().BeOfType<WorkflowInteractionNotificationEvent>().Subject;
        notification.RunId.Should().Be("run-1");
        notification.StepId.Should().Be("notify-1");
        notification.DeliveryTargetId.Should().Be("agent-delivery-1");
        notification.PayloadCase.Should().Be(WorkflowInteractionNotificationEvent.PayloadOneofCase.Interaction);
        notification.Interaction.Title.Should().Be("Status");

        ctx.Published[1].Direction.Should().Be(TopologyAudience.Self);
        var completed = ctx.Published[1].Event.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("accepted");
        completed.Annotations["notification_status"].Should().Be("accepted");
        ctx.Published.Select(x => x.Event).OfType<WorkflowSuspendedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenTemplateSpecValid_ShouldPublishTemplateNotificationThenAcceptedCompletion()
    {
        var ctx = new RecordingWorkflowContext();
        var module = new NotifyModule();

        await module.HandleAsync(Envelope(BuildRequest(template: BuildTemplateSpec())), ctx, CancellationToken.None);

        ctx.Published.Should().HaveCount(2);
        var notification = ctx.Published[0].Event.Should().BeOfType<WorkflowInteractionNotificationEvent>().Subject;
        notification.PayloadCase.Should().Be(WorkflowInteractionNotificationEvent.PayloadOneofCase.InteractionTemplate);
        notification.InteractionTemplate.TemplateId.Should().Be("tpl-1");
        notification.InteractionTemplate.TemplateVariable["run"].Should().Be("run-1");
        ctx.Published[1].Event.Should().BeOfType<StepCompletedEvent>().Which.Output.Should().Be("accepted");
        ctx.Published.Select(x => x.Event).OfType<WorkflowSuspendedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenTypedDeliveryTargetMissing_ShouldIgnoreParameterBagAndFailWithoutSuspension()
    {
        var ctx = new RecordingWorkflowContext();
        var module = new NotifyModule();
        var request = BuildRequest(interaction: BuildInteractionSpec());
        request.StepParameters.DeliveryTargetId = string.Empty;
        request.Parameters["delivery_target_id"] = "bag-delivery-target";

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("delivery_target_id");
        ctx.Published.Select(x => x.Event).OfType<WorkflowSuspendedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenPayloadMissingOrBothPresent_ShouldFailWithoutNotificationOrSuspension()
    {
        var module = new NotifyModule();
        var missingCtx = new RecordingWorkflowContext();
        var bothCtx = new RecordingWorkflowContext();

        await module.HandleAsync(Envelope(BuildRequest()), missingCtx, CancellationToken.None);
        await module.HandleAsync(
            Envelope(BuildRequest(interaction: BuildInteractionSpec(), template: BuildTemplateSpec())),
            bothCtx,
            CancellationToken.None);

        missingCtx.Published.Should().ContainSingle().Which.Event
            .Should().BeOfType<StepCompletedEvent>().Which.Success.Should().BeFalse();
        bothCtx.Published.Should().ContainSingle().Which.Event
            .Should().BeOfType<StepCompletedEvent>().Which.Success.Should().BeFalse();
        missingCtx.Published.Select(x => x.Event).OfType<WorkflowInteractionNotificationEvent>().Should().BeEmpty();
        bothCtx.Published.Select(x => x.Event).OfType<WorkflowInteractionNotificationEvent>().Should().BeEmpty();
        missingCtx.Published.Select(x => x.Event).OfType<WorkflowSuspendedEvent>().Should().BeEmpty();
        bothCtx.Published.Select(x => x.Event).OfType<WorkflowSuspendedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ShouldIgnoreOtherStepTypes()
    {
        var ctx = new RecordingWorkflowContext();
        var module = new NotifyModule();

        await module.HandleAsync(
            Envelope(new StepRequestEvent { StepId = "other", StepType = "emit" }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    private static StepRequestEvent BuildRequest(
        InteractionSpec? interaction = null,
        InteractionTemplateSpec? template = null)
    {
        var request = new StepRequestEvent
        {
            StepId = "notify-1",
            StepType = "notify",
            RunId = "run-1",
            ExecutionId = "exec-1",
            StepParameters = new WorkflowStepParameters
            {
                DeliveryTargetId = "agent-delivery-1",
            },
        };
        if (interaction is not null)
            request.StepParameters.InteractionSpec = interaction;
        if (template is not null)
            request.StepParameters.InteractionTemplateSpec = template;
        return request;
    }

    private static InteractionSpec BuildInteractionSpec() => new()
    {
        Title = "Status",
        Body = "Run accepted.",
        Actions =
        {
            new InteractionAction
            {
                Kind = InteractionActionKind.Button,
                ActionId = "open",
                Label = "Open",
            },
        },
    };

    private static InteractionTemplateSpec BuildTemplateSpec()
    {
        var spec = new InteractionTemplateSpec { TemplateId = "tpl-1" };
        spec.TemplateVariable["run"] = "run-1";
        return spec;
    }

    private static EventEnvelope Envelope(IMessage message) =>
        new()
        {
            Payload = Any.Pack(message),
        };

    private sealed class RecordingWorkflowContext : IWorkflowExecutionContext
    {
        public EventEnvelope InboundEnvelope { get; } = new();

        public string AgentId => "agent-1";

        public string RunId => "run-1";

        public IServiceProvider Services => EmptyServiceProvider.Instance;

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new() =>
            new();

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            [];

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState> =>
            Task.CompletedTask;

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Published.Add((evt, audience));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(System.Type serviceType) => null;
    }
}
