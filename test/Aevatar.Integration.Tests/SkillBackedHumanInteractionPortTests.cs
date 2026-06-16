using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "SkillBackedHumanInteractionPort")]
public sealed class SkillBackedHumanInteractionPortTests
{
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
