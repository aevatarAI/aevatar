using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Integration.AI;

public sealed class SkillBackedHumanInteractionPort : IHumanInteractionPort
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IEnumerable<IAgentToolSource> _toolSources;
    private readonly SkillBackedHumanInteractionPortOptions _options;

    public SkillBackedHumanInteractionPort(
        IEnumerable<IAgentToolSource> toolSources,
        IOptions<SkillBackedHumanInteractionPortOptions>? options = null)
    {
        _toolSources = toolSources ?? throw new ArgumentNullException(nameof(toolSources));
        _options = options?.Value ?? new SkillBackedHumanInteractionPortOptions();
    }

    public Task DeliverSuspensionAsync(
        HumanInteractionRequest request,
        string deliveryTargetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync(
            _options.DeliveryToolName,
            _options.DeliveryCapability,
            new HumanInteractionDeliveryEnvelope
            {
                DeliveryTargetId = deliveryTargetId,
                Capability = _options.DeliveryCapability,
                Interaction = request,
            },
            cancellationToken);
    }

    public Task DeliverApprovalResolutionAsync(
        HumanApprovalResolution resolution,
        string deliveryTargetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return InvokeAsync(
            _options.ResolutionToolName,
            _options.ResolutionCapability,
            new HumanApprovalResolutionDeliveryEnvelope
            {
                DeliveryTargetId = deliveryTargetId,
                Capability = _options.ResolutionCapability,
                Resolution = resolution,
            },
            cancellationToken);
    }

    private async Task InvokeAsync(
        string? configuredToolName,
        string capability,
        object payload,
        CancellationToken ct)
    {
        var tool = await ResolveToolAsync(configuredToolName, capability, ct).ConfigureAwait(false);
        if (tool == null)
            throw MissingTool(configuredToolName, capability);

        await tool.ExecuteAsync(JsonSerializer.Serialize(payload, JsonOptions), ct).ConfigureAwait(false);
    }

    private async Task<IAgentTool?> ResolveToolAsync(
        string? configuredToolName,
        string capability,
        CancellationToken ct)
    {
        foreach (var source in _toolSources)
        {
            var tools = await source.DiscoverToolsAsync(ct).ConfigureAwait(false);
            var selected = tools.FirstOrDefault(tool => MatchesTool(tool, configuredToolName, capability));
            if (selected != null)
                return selected;
        }

        return null;
    }

    private static bool MatchesTool(IAgentTool tool, string? configuredToolName, string capability)
    {
        if (!string.IsNullOrWhiteSpace(configuredToolName))
            return string.Equals(tool.Name, configuredToolName.Trim(), StringComparison.Ordinal);

        if (tool is IAgentToolCapabilityDescriptor descriptor &&
            descriptor.Capabilities.Any(x => string.Equals(x, capability, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return ContainsCapability(tool.Name, capability) ||
               ContainsCapability(tool.Description, capability) ||
               ContainsCapability(tool.ParametersSchema, capability);
    }

    private static bool ContainsCapability(string? value, string capability) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(capability, StringComparison.OrdinalIgnoreCase);

    private static InvalidOperationException MissingTool(string? configuredToolName, string capability)
    {
        if (string.IsNullOrWhiteSpace(configuredToolName))
        {
            return new InvalidOperationException(
                $"No human interaction delivery tool found for capability '{capability}'.");
        }

        return new InvalidOperationException(
            $"No human interaction delivery tool found for capability '{capability}' and configured tool name '{configuredToolName.Trim()}'.");
    }

    private sealed record HumanInteractionDeliveryEnvelope
    {
        public required string DeliveryTargetId { get; init; }

        public required string Capability { get; init; }

        public required HumanInteractionRequest Interaction { get; init; }
    }

    private sealed record HumanApprovalResolutionDeliveryEnvelope
    {
        public required string DeliveryTargetId { get; init; }

        public required string Capability { get; init; }

        public required HumanApprovalResolution Resolution { get; init; }
    }
}
