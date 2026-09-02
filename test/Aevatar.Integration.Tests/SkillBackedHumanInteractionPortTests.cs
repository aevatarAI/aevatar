using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "SkillBackedHumanInteractionPort")]
public sealed class SkillBackedHumanInteractionPortTests
{
    private const long StableIssuedAtUnixMs = 1_800_000_000_000;

    [Fact]
    public void AddSkillBackedHumanInteractionDelivery_ShouldNotRegisterHumanInteractionChannelToolSource()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentToolExecutionPort, PassThroughExecutionPort>();

        services.AddSkillBackedHumanInteractionDelivery();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHumanInteractionPort>()
            .Should()
            .BeOfType<SkillBackedHumanInteractionPort>();
        provider.GetServices<IAgentToolSource>()
            .Should()
            .NotContain(source => source is HumanInteractionChannelToolSource);
    }

    [Fact]
    public void AddChannelBackedHumanInteractionTools_ShouldRegisterHumanInteractionChannelToolSource()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentToolExecutionPort, PassThroughExecutionPort>();
        services.AddSingleton<IChannelInteractionNotificationPort>(new RecordingNotificationPort());
        services.AddSingleton<ILogger<HumanInteractionChannelToolSource>>(
            NullLogger<HumanInteractionChannelToolSource>.Instance);

        services.AddChannelBackedHumanInteractionTools();

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IAgentToolSource>()
            .Should()
            .ContainSingle(source => source is HumanInteractionChannelToolSource);
    }

    [Fact]
    public async Task AddSkillBackedHumanInteractionDelivery_ShouldApplyConfiguredDeliveryToolName()
    {
        var deliveryTool = new RecordingTool("configured-delivery-tool", "configured delivery");
        var capabilityTool = new RecordingTool("capability-delivery-tool", "generic delivery", ["human_interaction.delivery"]);
        var services = new ServiceCollection();
        services.AddSingleton<IAgentToolExecutionPort, PassThroughExecutionPort>();
        services.AddSingleton<IAgentToolSource>(new RecordingToolSource(capabilityTool, deliveryTool));
        services.AddSkillBackedHumanInteractionDelivery(options =>
        {
            options.DeliveryToolName = "configured-delivery-tool";
        });

        using var provider = services.BuildServiceProvider();
        var port = provider.GetRequiredService<IHumanInteractionPort>();

        await port.DeliverSuspensionAsync(
            new HumanInteractionRequest
            {
                ActorId = "workflow-actor",
                RunId = "run-1",
                StepId = "approval",
                SourceEventId = "event-configured-delivery",
                IssuedAtUnixMs = StableIssuedAtUnixMs,
                SuspensionType = "human_approval",
                Prompt = "Approve?",
            },
            "delivery-target-1",
            CancellationToken.None);

        capabilityTool.Calls.Should().BeEmpty();
        deliveryTool.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task DeliverSuspensionAsync_ShouldInvokeCapabilityMatchedToolWithStructuredPayload()
    {
        var tool = new RecordingTool("generic-human-delivery", "generic delivery", ["human_interaction.delivery"]);
        var port = new SkillBackedHumanInteractionPort(
            [new RecordingToolSource(tool)],
            new PassThroughExecutionPort());

        await port.DeliverSuspensionAsync(
            new HumanInteractionRequest
            {
                ActorId = "workflow-actor",
                RunId = "run-1",
                StepId = "approval",
                SourceEventId = "event-structured-delivery",
                IssuedAtUnixMs = StableIssuedAtUnixMs,
                SuspensionType = "human_approval",
                Prompt = "Approve?",
                Options = ["approve", "reject"],
                TimeoutSeconds = 60,
            },
            "delivery-target-1",
            CancellationToken.None);

        tool.Calls.Should().ContainSingle();
        using var document = JsonDocument.Parse(tool.Calls[0]);
        var root = document.RootElement;
        root.GetProperty("deliveryTargetId").GetString().Should().Be("delivery-target-1");
        root.GetProperty("capability").GetString().Should().Be("human_interaction.delivery");
        root.GetProperty("interaction").GetProperty("runId").GetString().Should().Be("run-1");
        root.GetProperty("interaction").GetProperty("options").EnumerateArray()
            .Select(x => x.GetString())
            .Should().Equal("approve", "reject");
    }

    [Fact]
    public async Task HumanInteractionChannelToolSource_ShouldExposeGenericCapabilitiesAndDelegateToChannelPort()
    {
        var notificationPort = new RecordingNotificationPort();
        var source = new HumanInteractionChannelToolSource(
            notificationPort,
            NullLogger<HumanInteractionChannelToolSource>.Instance);
        var tools = await source.DiscoverToolsAsync();
        var port = new SkillBackedHumanInteractionPort([source], new PassThroughExecutionPort());

        await port.DeliverSuspensionAsync(
            new HumanInteractionRequest
            {
                ActorId = "workflow-actor",
                RunId = "run-1",
                StepId = "approval",
                SourceEventId = "event-channel-delivery",
                IssuedAtUnixMs = StableIssuedAtUnixMs,
                SuspensionType = "human_approval",
                Prompt = "Approve?",
                Options = ["approve", "reject"],
                TimeoutSeconds = 60,
            },
            "delivery-target-1",
            CancellationToken.None);

        tools.Should().HaveCount(2);
        tools.OfType<IAgentToolCapabilityDescriptor>().Should().Contain(descriptor =>
            descriptor.Capabilities.Contains("human_interaction.delivery"));
        tools.OfType<IAgentToolCapabilityDescriptor>().Should().Contain(descriptor =>
            descriptor.Capabilities.Contains("human_interaction.resolution_update"));
        var call = notificationPort.Calls.Should().ContainSingle().Subject;
        call.DeliveryTargetId.Should().Be("delivery-target-1");
        call.ActorId.Should().Be("workflow-actor");
        call.RunId.Should().Be("run-1");
        call.StepId.Should().Be("approval");
        call.InteractionSpec.Should().NotBeNull();
        call.InteractionSpec!.Actions.Should().Contain(action =>
            action.Kind == InteractionActionKind.FormSubmit &&
            action.ApprovalDecision == InteractionApprovalDecision.Approve);
        call.InteractionSpec.Actions.Should().Contain(action =>
            action.Kind == InteractionActionKind.FormSubmit &&
            action.ApprovalDecision == InteractionApprovalDecision.Reject);
    }

    [Fact]
    public async Task DeliverSuspensionAsync_ShouldThrowWhenDeliveryToolIsMissing()
    {
        var port = new SkillBackedHumanInteractionPort(
            [new RecordingToolSource()],
            new PassThroughExecutionPort(),
            Options.Create(new SkillBackedHumanInteractionPortOptions
            {
                DeliveryToolName = "configured-delivery-tool",
            }));

        var act = () => port.DeliverSuspensionAsync(
            new HumanInteractionRequest
            {
                ActorId = "workflow-actor",
                RunId = "run-1",
                StepId = "approval",
                SourceEventId = "event-missing-delivery-tool",
                IssuedAtUnixMs = StableIssuedAtUnixMs,
                SuspensionType = "human_approval",
                Prompt = "Approve?",
            },
            "delivery-target-1",
            CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*human_interaction.delivery*configured-delivery-tool*");
    }

    [Fact]
    public async Task DeliverApprovalResolutionAsync_ShouldThrowWhenResolutionToolIsMissing()
    {
        var port = new SkillBackedHumanInteractionPort(
            [new RecordingToolSource()],
            new PassThroughExecutionPort());

        var act = () => port.DeliverApprovalResolutionAsync(
            new HumanApprovalResolution
            {
                ActorId = "workflow-actor",
                RunId = "run-2",
                StepId = "approval",
                SourceEventId = "event-missing-resolution-tool",
                IssuedAtUnixMs = StableIssuedAtUnixMs,
                Approved = false,
            },
            "delivery-target-2",
            CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*human_interaction.resolution_update*");
    }

    [Fact]
    public async Task DeliverApprovalResolutionAsync_ShouldInvokeConfiguredResolutionTool()
    {
        var deliveryTool = new RecordingTool("delivery-tool", "generic delivery", ["human_interaction.delivery"]);
        var resolutionTool = new RecordingTool("resolution-tool", "generic resolution updater");
        var port = new SkillBackedHumanInteractionPort(
            [new RecordingToolSource(deliveryTool, resolutionTool)],
            new PassThroughExecutionPort(),
            Options.Create(new SkillBackedHumanInteractionPortOptions
            {
                ResolutionToolName = "resolution-tool",
            }));

        await port.DeliverApprovalResolutionAsync(
            new HumanApprovalResolution
            {
                ActorId = "workflow-actor",
                RunId = "run-2",
                StepId = "approval",
                SourceEventId = "event-configured-resolution",
                IssuedAtUnixMs = StableIssuedAtUnixMs,
                Approved = false,
                TimedOut = true,
            },
            "delivery-target-2",
            CancellationToken.None);

        deliveryTool.Calls.Should().BeEmpty();
        resolutionTool.Calls.Should().ContainSingle();
        using var document = JsonDocument.Parse(resolutionTool.Calls[0]);
        var root = document.RootElement;
        root.GetProperty("deliveryTargetId").GetString().Should().Be("delivery-target-2");
        root.GetProperty("capability").GetString().Should().Be("human_interaction.resolution_update");
        root.GetProperty("resolution").GetProperty("timedOut").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(AgentToolExecutionOutcomeKind.Denied)]
    [InlineData(AgentToolExecutionOutcomeKind.Failed)]
    public async Task DeliverSuspensionAsync_WhenExecutionDoesNotComplete_ShouldThrowSafeMessage(
        AgentToolExecutionOutcomeKind outcomeKind)
    {
        var tool = new RecordingTool("human-delivery", "generic delivery", ["human_interaction.delivery"]);
        var executionPort = new FixedOutcomeExecutionPort(CreateOutcome(
            outcomeKind,
            failureCode: "delivery_failed",
            safeMessage: "The human interaction delivery failed safely."));
        var port = new SkillBackedHumanInteractionPort(
            [new RecordingToolSource(tool)],
            executionPort);

        var action = () => port.DeliverSuspensionAsync(CreateSuspensionRequest(), "delivery-target-1");

        await action.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("The human interaction delivery failed safely.");
        executionPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task DeliverSuspensionAsync_WhenSafeMessageIsEmpty_ShouldThrowFailureCode()
    {
        var tool = new RecordingTool("human-delivery", "generic delivery", ["human_interaction.delivery"]);
        var executionPort = new FixedOutcomeExecutionPort(CreateOutcome(
            AgentToolExecutionOutcomeKind.Failed,
            failureCode: "delivery_failure_code",
            safeMessage: " "));
        var port = new SkillBackedHumanInteractionPort(
            [new RecordingToolSource(tool)],
            executionPort);

        var action = () => port.DeliverSuspensionAsync(CreateSuspensionRequest(), "delivery-target-1");

        await action.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("delivery_failure_code");
        executionPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task DeliverSuspensionAsync_WhenExecutionAuditIsIncomplete_ShouldTreatDeliveryAsCompleted()
    {
        var tool = new RecordingTool("human-delivery", "generic delivery", ["human_interaction.delivery"]);
        var executionPort = new FixedOutcomeExecutionPort(CreateOutcome(
            AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete,
            failureCode: "audit_unavailable",
            safeMessage: "The terminal audit fact was not recorded."));
        var port = new SkillBackedHumanInteractionPort(
            [new RecordingToolSource(tool)],
            executionPort);

        var action = () => port.DeliverSuspensionAsync(CreateSuspensionRequest(), "delivery-target-1");

        await action.Should().NotThrowAsync();
        executionPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task DeliverSuspensionAsync_WhenAdmissionAlreadyStarted_ShouldTreatRedeliveryAsCompleted()
    {
        var tool = new RecordingTool("human-delivery", "generic delivery", ["human_interaction.delivery"]);
        var executionPort = new FixedOutcomeExecutionPort(new AgentToolExecutionOutcome(
            AgentToolExecutionOutcomeKind.Failed,
            "{}",
            new AgentToolReceipt
            {
                CallId = "delivery-call",
                ToolName = "human-delivery",
                Status = AgentToolReceiptStatus.Error,
                ResultJson = "{}",
            },
            IsMutation: true,
            FailureCode: "tool_execution_already_started",
            SafeMessage: "This exact tool call already started and will not be replayed.",
            AgentToolExecutionFailureStage.Admission,
            TerminalInvoked: false,
            Retryable: false,
            AuditCompleted: false));
        var port = new SkillBackedHumanInteractionPort(
            [new RecordingToolSource(tool)],
            executionPort);

        var action = () => port.DeliverSuspensionAsync(CreateSuspensionRequest(), "delivery-target-1");

        await action.Should().NotThrowAsync();
        executionPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task DeliveryIdentity_ShouldReuseSameSourceEventAndSeparateDifferentSourceEvents()
    {
        var tool = new RecordingTool(
            "human-delivery",
            "generic delivery",
            ["human_interaction.delivery", "human_interaction.resolution_update"]);
        var executionPort = new PassThroughExecutionPort();
        var port = new SkillBackedHumanInteractionPort(
            [new RecordingToolSource(tool)],
            executionPort);
        var suspension = new HumanInteractionRequest
        {
            ActorId = "actor-alpha",
            RunId = "run-alpha",
            StepId = "step-alpha",
            SourceEventId = "event-suspension-alpha",
            IssuedAtUnixMs = StableIssuedAtUnixMs,
            SuspensionType = "human_input",
            Prompt = "Continue?",
        };
        var resolution = new HumanApprovalResolution
        {
            ActorId = suspension.ActorId,
            RunId = suspension.RunId,
            StepId = suspension.StepId,
            SourceEventId = "event-resolution-alpha",
            IssuedAtUnixMs = suspension.IssuedAtUnixMs,
            Approved = true,
        };

        await port.DeliverSuspensionAsync(suspension, "target-alpha");
        await port.DeliverSuspensionAsync(suspension, "target-alpha");
        await port.DeliverApprovalResolutionAsync(resolution, "target-alpha");
        await port.DeliverApprovalResolutionAsync(resolution, "target-alpha");

        executionPort.Requests.Should().HaveCount(4);
        executionPort.Requests[0].ExecutionContext.Request
            .Should().BeEquivalentTo(executionPort.Requests[1].ExecutionContext.Request);
        executionPort.Requests[2].ExecutionContext.Request
            .Should().BeEquivalentTo(executionPort.Requests[3].ExecutionContext.Request);
        executionPort.Requests[0].ExecutionContext.Request.RequestId
            .Should().NotBe(executionPort.Requests[2].ExecutionContext.Request.RequestId);
        executionPort.Requests[0].ExecutionContext.Request.CallId
            .Should().NotBe(executionPort.Requests[2].ExecutionContext.Request.CallId);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("run")]
    [InlineData("step")]
    [InlineData("event")]
    [InlineData("target")]
    [InlineData("issued")]
    public async Task DeliverSuspensionAsync_WhenStableDeliveryIdentityIsMissing_ShouldFailBeforeExecution(
        string missingIdentity)
    {
        var executionPort = new PassThroughExecutionPort();
        var port = new SkillBackedHumanInteractionPort(
            [new RecordingToolSource(new RecordingTool(
                "human-delivery",
                "generic delivery",
                ["human_interaction.delivery"]))],
            executionPort);

        var action = () => port.DeliverSuspensionAsync(
            new HumanInteractionRequest
            {
                ActorId = missingIdentity == "actor" ? " " : "actor-alpha",
                RunId = missingIdentity == "run" ? " " : "run-alpha",
                StepId = missingIdentity == "step" ? " " : "step-alpha",
                SourceEventId = missingIdentity == "event" ? " " : "event-stable-identity",
                IssuedAtUnixMs = missingIdentity == "issued" ? 0 : StableIssuedAtUnixMs,
                SuspensionType = "human_input",
                Prompt = "Continue?",
            },
            missingIdentity == "target" ? " " : "target-alpha");

        await action.Should().ThrowAsync<ArgumentException>();
        executionPort.Requests.Should().BeEmpty();
    }

    private sealed class RecordingToolSource(params IAgentTool[] tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }

    private static HumanInteractionRequest CreateSuspensionRequest() => new()
    {
        ActorId = "workflow-actor",
        RunId = "run-1",
        StepId = "approval",
        SourceEventId = "event-default-suspension",
        IssuedAtUnixMs = StableIssuedAtUnixMs,
        SuspensionType = "human_approval",
        Prompt = "Approve?",
    };

    private static AgentToolExecutionOutcome CreateOutcome(
        AgentToolExecutionOutcomeKind kind,
        string failureCode,
        string safeMessage) =>
        new(
            kind,
            "{}",
            new AgentToolReceipt
            {
                CallId = "call-1",
                ToolName = "human-delivery",
                Status = kind switch
                {
                    AgentToolExecutionOutcomeKind.Denied => AgentToolReceiptStatus.Denied,
                    AgentToolExecutionOutcomeKind.Failed => AgentToolReceiptStatus.Error,
                    _ => AgentToolReceiptStatus.Success,
                },
                ResultJson = "{}",
            },
            IsMutation: false,
            failureCode,
            safeMessage,
            kind == AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete
                ? AgentToolExecutionFailureStage.TerminalAudit
                : AgentToolExecutionFailureStage.TerminalExecution,
            TerminalInvoked: kind == AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete,
            Retryable: false,
            AuditCompleted: false);

    private sealed class PassThroughExecutionPort : IAgentToolExecutionPort
    {
        public List<AgentToolExecutionRequest> Requests { get; } = [];

        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            var resultJson = await request.Tool.ExecuteAsync(request.ArgumentsJson, ct);
            return new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                resultJson,
                new AgentToolReceipt
                {
                    CallId = request.ExecutionContext.Request.CallId ?? string.Empty,
                    ToolName = request.Tool.Name,
                    Status = AgentToolReceiptStatus.Success,
                    ResultJson = resultJson,
                },
                IsMutation: false,
                FailureCode: string.Empty,
                SafeMessage: string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: true,
                Retryable: false,
                AuditCompleted: true);
        }
    }

    private sealed class FixedOutcomeExecutionPort(AgentToolExecutionOutcome outcome) : IAgentToolExecutionPort
    {
        public List<AgentToolExecutionRequest> Requests { get; } = [];

        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(outcome);
        }
    }

    private sealed class RecordingNotificationPort : IChannelInteractionNotificationPort
    {
        public List<ChannelInteractionNotificationRequest> Calls { get; } = [];

        public Task DeliverAsync(
            ChannelInteractionNotificationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTool(
        string name,
        string description,
        IReadOnlyCollection<string>? capabilities = null) : IAgentTool, IAgentToolCapabilityDescriptor
    {
        public List<string> Calls { get; } = [];

        public string Name { get; } = name;

        public string Description { get; } = description;

        public string ParametersSchema => "{}";

        public IReadOnlyCollection<string> Capabilities { get; } = capabilities ?? [];

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            Calls.Add(argumentsJson);
            return Task.FromResult("{}");
        }
    }
}
