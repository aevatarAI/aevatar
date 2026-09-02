using System.Text.Json;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Projection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowHumanInteractionProjectorTests
{
    private const long CommittedIssuedAtUnixMs = 1_700_000_000_000;

    [Fact]
    public async Task ProjectAsync_ShouldDeliverSuspension_WhenDeliveryTargetIsPresent()
    {
        var port = new RecordingHumanInteractionPort();
        var projector = new WorkflowHumanInteractionProjector(
            port,
            NullLogger<WorkflowHumanInteractionProjector>.Instance);

        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-human-1",
                Timestamp = Timestamp.FromDateTimeOffset(
                    DateTimeOffset.FromUnixTimeMilliseconds(CommittedIssuedAtUnixMs)),
                Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-human-interaction-test"),
                Payload = Any.Pack(new WorkflowSuspendedEvent
                {
                    RunId = "run-1",
                    StepId = "approval-1",
                    SuspensionType = "human_approval",
                    Prompt = "Need approval",
                    Content = "Please review the summary.",
                    DeliveryTargetId = "agent-delivery-1",
                    TimeoutSeconds = 90,
                    Interaction = new InteractionSpec
                    {
                        Title = "Typed review",
                        Body = "Approve the summary",
                    },
                    VariableName = "approval_note",
                    Secure = true,
                    RedactedOutput = "[captured]",
                    Metadata =
                    {
                        ["source"] = "workflow-test",
                        ["variable"] = "legacy_variable",
                        ["secure"] = "false",
                        ["input_mode"] = "password",
                        ["redacted_output"] = "[legacy]",
                    },
                }),
            },
            CancellationToken.None);

        port.Calls.Should().ContainSingle();
        var call = port.Calls[0];
        call.deliveryTargetId.Should().Be("agent-delivery-1");
        call.request.ActorId.Should().Be("workflow-actor-1");
        call.request.RunId.Should().Be("run-1");
        call.request.StepId.Should().Be("approval-1");
        JsonSerializer.SerializeToElement(
                call.request,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .GetProperty("sourceEventId")
            .GetString()
            .Should().Be("evt-human-1");
        call.request.IssuedAtUnixMs.Should().Be(CommittedIssuedAtUnixMs);
        call.request.SuspensionType.Should().Be("human_approval");
        call.request.Content.Should().Be("Please review the summary.");
        call.request.Options.Should().Equal("approve", "reject");
        call.request.InteractionSpec.Should().NotBeNull();
        call.request.InteractionSpec!.Title.Should().Be("Typed review");
        call.request.InteractionSpec.Body.Should().Be("Approve the summary");
        call.request.TimeoutSeconds.Should().Be(90);
        call.request.Annotations.Should().ContainKey("source").WhoseValue.Should().Be("workflow-test");
        call.request.Annotations.Should().ContainKey("variable").WhoseValue.Should().Be("approval_note");
        call.request.Annotations.Should().ContainKey("secure").WhoseValue.Should().Be("true");
        call.request.Annotations.Should().ContainKey("redacted_output").WhoseValue.Should().Be("[captured]");
        call.request.Annotations.Should().NotContainKey("input_mode");
    }

    [Fact]
    public async Task ProjectAsync_ShouldDerivePromptAndOptionsFromTypedInteraction()
    {
        var port = new RecordingHumanInteractionPort();
        var projector = new WorkflowHumanInteractionProjector(
            port,
            NullLogger<WorkflowHumanInteractionProjector>.Instance);

        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-human-typed-1",
                Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-human-interaction-test"),
                Payload = Any.Pack(new WorkflowSuspendedEvent
                {
                    RunId = "run-typed",
                    StepId = "approval-typed",
                    SuspensionType = "human_approval",
                    DeliveryTargetId = "agent-delivery-typed",
                    Interaction = new InteractionSpec
                    {
                        Title = "Typed approval",
                        Body = "Review typed payload",
                        Actions =
                        {
                            new InteractionAction
                            {
                                Kind = InteractionActionKind.FormSubmit,
                                ActionId = "approve",
                                Label = "Approve",
                            },
                            new InteractionAction
                            {
                                Kind = InteractionActionKind.FormSubmit,
                                ActionId = "reject",
                                Label = "Reject",
                            },
                        },
                    },
                }),
            },
            CancellationToken.None);

        port.Calls.Should().ContainSingle();
        var request = port.Calls[0].request;
        request.Prompt.Should().Be("Review typed payload");
        request.Options.Should().Equal("approve", "reject");
        request.InteractionSpec.Should().NotBeNull();
        request.InteractionSpec!.Actions.Select(action => action.ActionId)
            .Should().Equal("approve", "reject");
    }

    [Fact]
    public async Task ProjectAsync_ShouldDeliverSuspension_FromCommittedStatePublication()
    {
        var port = new RecordingHumanInteractionPort();
        var projector = new WorkflowHumanInteractionProjector(
            port,
            NullLogger<WorkflowHumanInteractionProjector>.Instance);

        await projector.ProjectAsync(
            BuildContext(),
            BuildCommittedStateEnvelope(
                "outer-human-committed",
                "evt-human-committed",
                new WorkflowSuspendedEvent
                {
                    RunId = "run-committed",
                    StepId = "approval-committed",
                    SuspensionType = "human_approval",
                    Prompt = "Approve committed event?",
                    DeliveryTargetId = "agent-delivery-committed",
                    TimeoutSeconds = 120,
                }),
            CancellationToken.None);

        port.Calls.Should().ContainSingle();
        var call = port.Calls[0];
        call.deliveryTargetId.Should().Be("agent-delivery-committed");
        call.request.RunId.Should().Be("run-committed");
        call.request.StepId.Should().Be("approval-committed");
        call.request.SourceEventId.Should().Be("evt-human-committed");
        call.request.IssuedAtUnixMs.Should().Be(CommittedIssuedAtUnixMs);
        call.request.Options.Should().Equal("approve", "reject");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreLegacySecureInputMetadataReservedKeys()
    {
        var port = new RecordingHumanInteractionPort();
        var projector = new WorkflowHumanInteractionProjector(
            port,
            NullLogger<WorkflowHumanInteractionProjector>.Instance);

        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-human-legacy-secure",
                Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-human-interaction-test"),
                Payload = Any.Pack(new WorkflowSuspendedEvent
                {
                    RunId = "run-legacy",
                    StepId = "secure-legacy",
                    SuspensionType = "secure_input",
                    Prompt = "Need secret",
                    DeliveryTargetId = "agent-delivery-legacy",
                    Metadata =
                    {
                        ["source"] = "legacy-test",
                        ["variable"] = "api_key",
                        ["secure"] = "true",
                        ["input_mode"] = "password",
                        ["redacted_output"] = "[legacy captured]",
                    },
                }),
            },
            CancellationToken.None);

        port.Calls.Should().ContainSingle();
        var annotations = port.Calls[0].request.Annotations;
        annotations.Should().ContainKey("source").WhoseValue.Should().Be("legacy-test");
        annotations.Should().NotContainKey("variable");
        annotations.Should().NotContainKey("secure");
        annotations.Should().NotContainKey("redacted_output");
        annotations.Should().NotContainKey("input_mode");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreSuspension_WhenDeliveryTargetMissing()
    {
        var port = new RecordingHumanInteractionPort();
        var projector = new WorkflowHumanInteractionProjector(
            port,
            NullLogger<WorkflowHumanInteractionProjector>.Instance);

        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-human-2",
                Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-human-interaction-test"),
                Payload = Any.Pack(new WorkflowSuspendedEvent
                {
                    RunId = "run-2",
                    StepId = "input-1",
                    SuspensionType = "human_input",
                    Prompt = "Need extra details",
                }),
            },
            CancellationToken.None);

        port.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldNotDeliverActionableRequest_ForToolApprovalSuspension()
    {
        var port = new RecordingHumanInteractionPort();
        var projector = new WorkflowHumanInteractionProjector(
            port,
            NullLogger<WorkflowHumanInteractionProjector>.Instance);

        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-tool-approval",
                Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-human-interaction-test"),
                Payload = Any.Pack(new WorkflowSuspendedEvent
                {
                    RunId = "run-tool",
                    StepId = "step-tool",
                    SuspensionType = "tool_approval",
                    DeliveryTargetId = "agent-delivery-tool",
                    ToolApproval = new WorkflowToolApprovalSuspension
                    {
                        ExecutionId = "exec-tool",
                        ToolName = "dangerous_tool",
                        ToolCallId = "call-tool",
                        ApprovalRequestId = "approval-tool",
                    },
                }),
            },
            CancellationToken.None);

        port.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreNonProjectionRoute()
    {
        var port = new RecordingHumanInteractionPort();
        var projector = new WorkflowHumanInteractionProjector(
            port,
            NullLogger<WorkflowHumanInteractionProjector>.Instance);

        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-human-3",
                Route = EnvelopeRouteSemantics.CreateDirect("projection-test", "workflow-actor-1"),
                Payload = Any.Pack(new WorkflowSuspendedEvent
                {
                    RunId = "run-3",
                    StepId = "approval-3",
                    SuspensionType = "human_approval",
                    Prompt = "Need approval",
                    DeliveryTargetId = "agent-delivery-3",
                }),
            },
            CancellationToken.None);

        port.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ApprovalResolutionProjector_ShouldDeliverResolution_WhenDeliveryTargetIsPresent()
    {
        var port = new RecordingHumanInteractionPort();
        var projector = new WorkflowHumanApprovalResolutionProjector(port);

        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-human-resolution-1",
                Timestamp = Timestamp.FromDateTimeOffset(
                    DateTimeOffset.FromUnixTimeMilliseconds(CommittedIssuedAtUnixMs)),
                Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-human-resolution-test"),
                Payload = Any.Pack(new WorkflowHumanApprovalResolvedEvent
                {
                    RunId = "run-4",
                    StepId = "approval-4",
                    Approved = false,
                    UserInput = "Need stronger CTA",
                    EditedContent = "Edited but rejected",
                    Feedback = "Need stronger CTA",
                    DeliveryTargetId = "agent-delivery-4",
                    ResolvedContent = "Draft needs stronger CTA",
                    ResolutionSource = WorkflowHumanApprovalResolutionSource.Timeout,
                }),
            },
            CancellationToken.None);

        port.ResolutionCalls.Should().ContainSingle();
        var call = port.ResolutionCalls[0];
        call.deliveryTargetId.Should().Be("agent-delivery-4");
        call.resolution.ActorId.Should().Be("workflow-actor-1");
        call.resolution.RunId.Should().Be("run-4");
        call.resolution.StepId.Should().Be("approval-4");
        call.resolution.IssuedAtUnixMs.Should().Be(CommittedIssuedAtUnixMs);
        call.resolution.Approved.Should().BeFalse();
        call.resolution.UserInput.Should().Be("Need stronger CTA");
        call.resolution.EditedContent.Should().Be("Edited but rejected");
        call.resolution.Feedback.Should().Be("Need stronger CTA");
        call.resolution.ResolvedContent.Should().Be("Draft needs stronger CTA");
        call.resolution.TimedOut.Should().BeTrue();
    }

    [Fact]
    public async Task ApprovalResolutionProjector_ShouldDeliverResolution_FromCommittedStatePublication()
    {
        var port = new RecordingHumanInteractionPort();
        var projector = new WorkflowHumanApprovalResolutionProjector(port);

        await projector.ProjectAsync(
            BuildContext(),
            BuildCommittedStateEnvelope(
                "outer-human-resolution-committed",
                "evt-human-resolution-committed",
                new WorkflowHumanApprovalResolvedEvent
                {
                    RunId = "run-resolution-committed",
                    StepId = "approval-resolution-committed",
                    Approved = true,
                    DeliveryTargetId = "agent-delivery-resolution-committed",
                    ResolvedContent = "Approved content",
                }),
            CancellationToken.None);

        port.ResolutionCalls.Should().ContainSingle();
        var call = port.ResolutionCalls[0];
        call.deliveryTargetId.Should().Be("agent-delivery-resolution-committed");
        call.resolution.RunId.Should().Be("run-resolution-committed");
        call.resolution.StepId.Should().Be("approval-resolution-committed");
        call.resolution.SourceEventId.Should().Be("evt-human-resolution-committed");
        call.resolution.IssuedAtUnixMs.Should().Be(CommittedIssuedAtUnixMs);
        call.resolution.Approved.Should().BeTrue();
        call.resolution.ResolvedContent.Should().Be("Approved content");
    }

    [Fact]
    public async Task ApprovalResolutionProjector_ShouldIgnoreResolution_WhenDeliveryTargetMissing()
    {
        var port = new RecordingHumanInteractionPort();
        var projector = new WorkflowHumanApprovalResolutionProjector(port);

        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-human-resolution-2",
                Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-human-resolution-test"),
                Payload = Any.Pack(new WorkflowHumanApprovalResolvedEvent
                {
                    RunId = "run-5",
                    StepId = "approval-5",
                    Approved = true,
                }),
            },
            CancellationToken.None);

        port.ResolutionCalls.Should().BeEmpty();
    }

    private static WorkflowExecutionProjectionContext BuildContext() => new()
    {
        SessionId = "cmd-1",
        RootActorId = "workflow-actor-1",
        ProjectionKind = "workflow-execution-session",
    };

    private static EventEnvelope BuildCommittedStateEnvelope(
        string envelopeId,
        string eventId,
        IMessage payload) => new()
    {
        Id = envelopeId,
        Timestamp = Timestamp.FromDateTimeOffset(
            DateTimeOffset.FromUnixTimeMilliseconds(CommittedIssuedAtUnixMs + 1)),
        Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-human-interaction-test"),
        Payload = Any.Pack(new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = eventId,
                Version = 12,
                Timestamp = Timestamp.FromDateTimeOffset(
                    DateTimeOffset.FromUnixTimeMilliseconds(CommittedIssuedAtUnixMs)),
                EventData = Any.Pack(payload),
            },
        }),
    };

    private sealed class RecordingHumanInteractionPort : IHumanInteractionPort
    {
        public List<(HumanInteractionRequest request, string deliveryTargetId)> Calls { get; } = [];
        public List<(HumanApprovalResolution resolution, string deliveryTargetId)> ResolutionCalls { get; } = [];

        public Task DeliverSuspensionAsync(
            HumanInteractionRequest request,
            string deliveryTargetId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((request, deliveryTargetId));
            return Task.CompletedTask;
        }

        public Task DeliverApprovalResolutionAsync(
            HumanApprovalResolution resolution,
            string deliveryTargetId,
            CancellationToken cancellationToken = default)
        {
            ResolutionCalls.Add((resolution, deliveryTargetId));
            return Task.CompletedTask;
        }
    }
}
