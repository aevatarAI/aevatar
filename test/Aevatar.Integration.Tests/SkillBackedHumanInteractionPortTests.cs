using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Skills;
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
    [Fact]
    public void AddSkillBackedHumanInteractionDelivery_ShouldNotRegisterHumanInteractionChannelToolSource()
    {
        var services = new ServiceCollection();

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
        var port = new SkillBackedHumanInteractionPort([new RecordingToolSource(tool)]);

        await port.DeliverSuspensionAsync(
            new HumanInteractionRequest
            {
                ActorId = "workflow-actor",
                RunId = "run-1",
                StepId = "approval",
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
        var port = new SkillBackedHumanInteractionPort([source]);

        await port.DeliverSuspensionAsync(
            new HumanInteractionRequest
            {
                ActorId = "workflow-actor",
                RunId = "run-1",
                StepId = "approval",
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
    public async Task SkillsAgentToolSource_ShouldMaterializeSkillCapabilityTools()
    {
        var catalog = new LocalSkillCatalog();
        var executionPort = new RecordingSkillCapabilityExecutionPort();
        catalog.Register(new SkillDefinition
        {
            Name = "lark-human-interaction",
            Description = "Lark delivery",
            Instructions = "Deliver cards.",
            Source = SkillSource.Local,
            IsModelInvocable = false,
            Capabilities =
            [
                new SkillCapabilityDescriptor
                {
                    Capability = "human_interaction.delivery",
                    ToolName = "lark_human_interaction_delivery",
                    Description = "Deliver Lark card.",
                },
                new SkillCapabilityDescriptor
                {
                    Capability = "human_interaction.resolution_update",
                    ToolName = "lark_human_interaction_resolution_update",
                    Description = "Update Lark card.",
                },
            ],
        });
        var source = new SkillsAgentToolSource(
            new SkillsOptions(),
            new SkillDiscovery(),
            catalog,
            capabilityExecutionPort: executionPort);
        var port = new SkillBackedHumanInteractionPort([source]);

        await port.DeliverSuspensionAsync(
            new HumanInteractionRequest
            {
                ActorId = "workflow-actor",
                RunId = "run-1",
                StepId = "approval",
                SuspensionType = "human_approval",
                Prompt = "Approve?",
                Options = ["approve", "reject"],
            },
            "delivery-target-1",
            CancellationToken.None);

        var tools = await source.DiscoverToolsAsync();
        tools.OfType<IAgentToolCapabilityDescriptor>().Should().Contain(descriptor =>
            descriptor.Capabilities.Contains("human_interaction.delivery"));
        tools.OfType<IAgentToolCapabilityDescriptor>().Should().Contain(descriptor =>
            descriptor.Capabilities.Contains("human_interaction.resolution_update"));
        executionPort.Requests.Should().ContainSingle();
        executionPort.Requests[0].Capability.Capability.Should().Be("human_interaction.delivery");
        executionPort.Requests[0].Skill.Name.Should().Be("lark-human-interaction");
    }

    [Fact]
    public async Task DeliverSuspensionAsync_ShouldThrowWhenDeliveryToolIsMissing()
    {
        var port = new SkillBackedHumanInteractionPort(
            [new RecordingToolSource()],
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
        var port = new SkillBackedHumanInteractionPort([new RecordingToolSource()]);

        var act = () => port.DeliverApprovalResolutionAsync(
            new HumanApprovalResolution
            {
                ActorId = "workflow-actor",
                RunId = "run-2",
                StepId = "approval",
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

    private sealed class RecordingToolSource(params IAgentTool[] tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
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

    private sealed class RecordingSkillCapabilityExecutionPort : ISkillCapabilityExecutionPort
    {
        public List<SkillCapabilityExecutionRequest> Requests { get; } = [];

        public Task<string> ExecuteAsync(
            SkillCapabilityExecutionRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult("""{"success":true}""");
        }
    }
}
