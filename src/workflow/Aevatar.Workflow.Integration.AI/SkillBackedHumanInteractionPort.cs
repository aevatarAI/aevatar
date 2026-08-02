using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Integration.AI;

public sealed class SkillBackedHumanInteractionPort : IHumanInteractionPort
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IEnumerable<IAgentToolSource> _toolSources;
    private readonly IAgentToolExecutionPort _toolExecutionPort;
    private readonly SkillBackedHumanInteractionPortOptions _options;

    public SkillBackedHumanInteractionPort(
        IEnumerable<IAgentToolSource> toolSources,
        IAgentToolExecutionPort toolExecutionPort,
        IOptions<SkillBackedHumanInteractionPortOptions>? options = null)
    {
        _toolSources = toolSources ?? throw new ArgumentNullException(nameof(toolSources));
        _toolExecutionPort = toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
        _options = options?.Value ?? new SkillBackedHumanInteractionPortOptions();
    }

    public Task DeliverSuspensionAsync(
        HumanInteractionRequest request,
        string deliveryTargetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = CreateDeliveryIdentity(
            request.ActorId,
            request.RunId,
            request.StepId,
            request.SourceEventId,
            request.IssuedAtUnixMs,
            "suspension",
            deliveryTargetId);
        return InvokeAsync(
            _options.DeliveryToolName,
            _options.DeliveryCapability,
            identity,
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
        var identity = CreateDeliveryIdentity(
            resolution.ActorId,
            resolution.RunId,
            resolution.StepId,
            resolution.SourceEventId,
            resolution.IssuedAtUnixMs,
            "approval-resolution",
            deliveryTargetId);
        return InvokeAsync(
            _options.ResolutionToolName,
            _options.ResolutionCapability,
            identity,
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
        AgentToolRequestIdentity identity,
        object payload,
        CancellationToken ct)
    {
        var tool = await ResolveToolAsync(configuredToolName, capability, ct).ConfigureAwait(false);
        if (tool == null)
            throw MissingTool(configuredToolName, capability);

        var outcome = await _toolExecutionPort.ExecuteAsync(
            new AgentToolExecutionRequest(
                tool,
                JsonSerializer.Serialize(payload, JsonOptions),
                AgentToolExecutionContext.Empty with
                {
                    Request = identity,
                    ExecutionOwner = AgentToolExecutionOwners.HostService(
                        nameof(SkillBackedHumanInteractionPort)),
                },
                AgentToolApprovalContinuationMode.None,
                null),
            ct).ConfigureAwait(false);
        if (outcome.Kind is not (AgentToolExecutionOutcomeKind.Executed or
            AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete))
        {
            if (IsAlreadyStartedRedelivery(outcome))
                return;

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(outcome.SafeMessage)
                    ? outcome.FailureCode
                    : outcome.SafeMessage);
        }
    }

    private static bool IsAlreadyStartedRedelivery(AgentToolExecutionOutcome outcome) =>
        outcome.Kind == AgentToolExecutionOutcomeKind.Failed &&
        outcome.FailureStage == AgentToolExecutionFailureStage.Admission &&
        string.Equals(
            outcome.FailureCode,
            "tool_execution_already_started",
            StringComparison.Ordinal) &&
        !outcome.TerminalInvoked &&
        !outcome.Retryable;

    private static AgentToolRequestIdentity CreateDeliveryIdentity(
        string actorId,
        string runId,
        string stepId,
        string sourceEventId,
        long issuedAtUnixMs,
        string deliveryKind,
        string deliveryTargetId)
    {
        var normalizedActorId = NormalizeRequired(actorId, nameof(actorId));
        var normalizedRunId = NormalizeRequired(runId, nameof(runId));
        var normalizedStepId = NormalizeRequired(stepId, nameof(stepId));
        var normalizedSourceEventId = NormalizeRequired(sourceEventId, nameof(sourceEventId));
        var normalizedTargetId = NormalizeRequired(deliveryTargetId, nameof(deliveryTargetId));
        if (issuedAtUnixMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(issuedAtUnixMs));

        var requestId = "human-delivery:v2:request:" +
                        HashLengthPrefixed(
                            normalizedActorId,
                            normalizedRunId,
                            normalizedStepId,
                            normalizedSourceEventId);
        var callId = "human-delivery:v2:call:" +
                     HashLengthPrefixed(deliveryKind, normalizedTargetId);
        return new AgentToolRequestIdentity(requestId, callId, callId, issuedAtUnixMs);
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A stable human delivery identity is required.", parameterName);

        return value.Trim();
    }

    private static string HashLengthPrefixed(params string[] values)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[sizeof(uint)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            stream.Write(length);
            stream.Write(bytes);
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
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
