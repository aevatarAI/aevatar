using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.DynamicRuntime.Application;

internal interface IDynamicRuntimeEventProjector
{
    Task ProjectAsync(EventEnvelope envelope, CancellationToken ct = default);
}

internal sealed class DynamicRuntimeEventProjector : IDynamicRuntimeEventProjector
{
    private const string CustomStateTypeUrlMetadataKey = "custom_state_type_url";
    private const string CustomStateValueB64MetadataKey = "custom_state_value_b64";
    private const string ServiceIdMetadataKey = "service_id";
    private const string BuildPlanDigestMetadataKey = "build_plan_digest";
    private const string RequestedByAgentIdMetadataKey = "requested_by_agent_id";
    private const string RequiresManualApprovalMetadataKey = "requires_manual_approval";
    private const string ComposeYamlMetadataKey = "compose_yaml";
    private const string ServicesCountMetadataKey = "services_count";
    private const string ComposeActionMetadataKey = "compose_action";
    private const string DesiredGenerationMetadataKey = "desired_generation";
    private const string GenerationMetadataKey = "generation";
    private const string ReplicasReadyMetadataKey = "replicas_ready";
    private const string RolloutStatusMetadataKey = "rollout_status";
    private const string ServiceModeMetadataKey = "service_mode";
    private const string ReplicasDesiredMetadataKey = "replicas_desired";

    private readonly IDynamicRuntimeReadStore _readStore;

    public DynamicRuntimeEventProjector(IDynamicRuntimeReadStore readStore)
    {
        _readStore = readStore;
    }

    public async Task ProjectAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (envelope?.Payload == null)
            return;

        var payload = envelope.Payload;
        if (payload.Is(ScriptImagePublishedEvent.Descriptor))
        {
            await ProjectImagePublishedAsync(payload.Unpack<ScriptImagePublishedEvent>(), ct);
            return;
        }

        if (payload.Is(ScriptComposeAppliedEvent.Descriptor))
        {
            await ProjectComposeAppliedAsync(payload.Unpack<ScriptComposeAppliedEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(ScriptComposeConvergedEvent.Descriptor))
        {
            await ProjectComposeConvergedAsync(payload.Unpack<ScriptComposeConvergedEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(ScriptComposeServiceScaledEvent.Descriptor))
        {
            await ProjectComposeServiceScaledAsync(payload.Unpack<ScriptComposeServiceScaledEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(ScriptComposeServiceRolledOutEvent.Descriptor))
        {
            await ProjectComposeServiceRolledOutAsync(payload.Unpack<ScriptComposeServiceRolledOutEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(ScriptServiceRegisteredEvent.Descriptor))
        {
            await ProjectServiceRegisteredAsync(payload.Unpack<ScriptServiceRegisteredEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(ScriptServiceUpdatedEvent.Descriptor))
        {
            await ProjectServiceUpdatedAsync(payload.Unpack<ScriptServiceUpdatedEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(ScriptServiceActivatedEvent.Descriptor))
        {
            await ProjectServiceStatusAsync(payload.Unpack<ScriptServiceActivatedEvent>().ServiceId, DynamicServiceStatus.Active, payload.Unpack<ScriptServiceActivatedEvent>().UpdatedAtUnixMs, ct);
            return;
        }

        if (payload.Is(ScriptServiceDeactivatedEvent.Descriptor))
        {
            await ProjectServiceStatusAsync(payload.Unpack<ScriptServiceDeactivatedEvent>().ServiceId, DynamicServiceStatus.Inactive, payload.Unpack<ScriptServiceDeactivatedEvent>().UpdatedAtUnixMs, ct);
            return;
        }

        if (payload.Is(ScriptCustomStateUpdatedEvent.Descriptor))
        {
            await ProjectServiceCustomStateAsync(payload.Unpack<ScriptCustomStateUpdatedEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(ScriptContainerCreatedEvent.Descriptor))
        {
            await ProjectContainerCreatedAsync(payload.Unpack<ScriptContainerCreatedEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(ScriptContainerStartedEvent.Descriptor))
        {
            await ProjectContainerStatusAsync(payload.Unpack<ScriptContainerStartedEvent>().ContainerId, "Running", envelope, ct);
            return;
        }

        if (payload.Is(ScriptContainerStoppedEvent.Descriptor))
        {
            await ProjectContainerStatusAsync(payload.Unpack<ScriptContainerStoppedEvent>().ContainerId, "Stopped", envelope, ct);
            return;
        }

        if (payload.Is(ScriptContainerDestroyedEvent.Descriptor))
        {
            await ProjectContainerStatusAsync(payload.Unpack<ScriptContainerDestroyedEvent>().ContainerId, "Destroyed", envelope, ct);
            return;
        }

        if (payload.Is(ScriptRunStartedEvent.Descriptor))
        {
            await ProjectRunStartedAsync(payload.Unpack<ScriptRunStartedEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(ScriptRunCompletedEvent.Descriptor))
        {
            await ProjectRunCompletedAsync(payload.Unpack<ScriptRunCompletedEvent>(), ct);
            return;
        }

        if (payload.Is(ScriptRunFailedEvent.Descriptor))
        {
            await ProjectRunFailedAsync(payload.Unpack<ScriptRunFailedEvent>(), ct);
            return;
        }

        if (payload.Is(ScriptRunCanceledEvent.Descriptor))
        {
            await ProjectRunCanceledAsync(payload.Unpack<ScriptRunCanceledEvent>(), ct);
            return;
        }

        if (payload.Is(ScriptRunTimedOutEvent.Descriptor))
        {
            await ProjectRunTimedOutAsync(payload.Unpack<ScriptRunTimedOutEvent>(), ct);
            return;
        }

        if (payload.Is(ScriptBuildPlanSubmittedEvent.Descriptor))
        {
            await ProjectBuildPlanSubmittedAsync(payload.Unpack<ScriptBuildPlanSubmittedEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(ScriptBuildPolicyValidatedEvent.Descriptor))
        {
            await ProjectBuildPolicyValidatedAsync(payload.Unpack<ScriptBuildPolicyValidatedEvent>(), ct);
            return;
        }

        if (payload.Is(ScriptBuildApprovedEvent.Descriptor))
        {
            await ProjectBuildApprovedAsync(payload.Unpack<ScriptBuildApprovedEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(ScriptBuildPublishedEvent.Descriptor))
        {
            await ProjectBuildPublishedAsync(payload.Unpack<ScriptBuildPublishedEvent>(), ct);
            return;
        }

        if (payload.Is(ScriptBuildRolledBackEvent.Descriptor))
        {
            await ProjectBuildRolledBackAsync(payload.Unpack<ScriptBuildRolledBackEvent>(), ct);
            return;
        }
    }

    private async Task ProjectImagePublishedAsync(ScriptImagePublishedEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.ImageName) || string.IsNullOrWhiteSpace(evt.Tag) || string.IsNullOrWhiteSpace(evt.Digest))
            return;

        var existing = await _readStore.GetImageAsync(evt.ImageName, ct);
        var tags = existing?.Tags is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(existing.Tags, StringComparer.Ordinal);
        tags[evt.Tag] = evt.Digest;

        var digests = existing?.Digests is null
            ? new List<string>()
            : [.. existing.Digests];
        if (!digests.Contains(evt.Digest, StringComparer.Ordinal))
            digests.Add(evt.Digest);

        await _readStore.UpsertImageAsync(new ImageSnapshot(evt.ImageName, tags, digests), ct);
    }

    private async Task ProjectComposeAppliedAsync(ScriptComposeAppliedEvent evt, EventEnvelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.StackId))
            return;

        var existing = await _readStore.GetStackAsync(evt.StackId, ct);
        var composeYaml = ReadMetadata(envelope, ComposeYamlMetadataKey);
        var stackSnapshot = new StackSnapshot(
            evt.StackId,
            evt.ComposeSpecDigest ?? existing?.ComposeSpecDigest ?? string.Empty,
            string.IsNullOrWhiteSpace(composeYaml) ? existing?.ComposeYaml ?? string.Empty : composeYaml,
            evt.DesiredGeneration,
            existing?.ObservedGeneration ?? 0,
            "Applying");
        await _readStore.UpsertStackAsync(stackSnapshot, ct);

        var servicesCount = ReadMetadata(envelope, ServicesCountMetadataKey);
        var details = string.IsNullOrWhiteSpace(servicesCount) ? "services=unknown" : $"services={servicesCount}";
        await _readStore.AppendComposeEventAsync(new ComposeEventSnapshot(
            evt.StackId,
            evt.DesiredGeneration,
            "ComposeApplied",
            details,
            ResolveOccurredAtUtc(envelope)), ct);
    }

    private async Task ProjectComposeConvergedAsync(ScriptComposeConvergedEvent evt, EventEnvelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.StackId))
            return;

        var existing = await _readStore.GetStackAsync(evt.StackId, ct);
        var desiredGeneration = ReadLongMetadata(envelope, DesiredGenerationMetadataKey)
            ?? existing?.DesiredGeneration
            ?? evt.ObservedGeneration;
        var stackSnapshot = new StackSnapshot(
            evt.StackId,
            existing?.ComposeSpecDigest ?? string.Empty,
            existing?.ComposeYaml ?? ReadMetadata(envelope, ComposeYamlMetadataKey),
            desiredGeneration,
            evt.ObservedGeneration,
            "Converged");
        await _readStore.UpsertStackAsync(stackSnapshot, ct);

        var action = ReadMetadata(envelope, ComposeActionMetadataKey);
        string eventType;
        string details;
        if (string.Equals(action, "up", StringComparison.OrdinalIgnoreCase))
        {
            eventType = "ComposeUp";
            details = "stack up requested";
        }
        else if (string.Equals(action, "down", StringComparison.OrdinalIgnoreCase))
        {
            eventType = "ComposeDown";
            details = "stack down requested";
        }
        else if (string.Equals(action, "apply", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        else
        {
            eventType = "ComposeConverged";
            details = $"observed_generation={evt.ObservedGeneration}";
        }

        await _readStore.AppendComposeEventAsync(new ComposeEventSnapshot(
            evt.StackId,
            desiredGeneration,
            eventType,
            details,
            ResolveOccurredAtUtc(envelope)), ct);
    }

    private async Task ProjectComposeServiceScaledAsync(ScriptComposeServiceScaledEvent evt, EventEnvelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.StackId) || string.IsNullOrWhiteSpace(evt.ServiceName))
            return;

        var existing = await GetComposeServiceAsync(evt.StackId, evt.ServiceName, ct);
        var generation = ReadLongMetadata(envelope, GenerationMetadataKey)
            ?? (existing?.Generation + 1 ?? 1);
        var replicasReady = ReadIntMetadata(envelope, ReplicasReadyMetadataKey) ?? evt.ReplicasDesired;
        var rolloutStatus = ReadMetadata(envelope, RolloutStatusMetadataKey);
        if (string.IsNullOrWhiteSpace(rolloutStatus))
            rolloutStatus = "Scaled";

        var snapshot = new ComposeServiceSnapshot(
            evt.StackId,
            evt.ServiceName,
            evt.ImageRef ?? existing?.ImageRef ?? string.Empty,
            evt.ReplicasDesired,
            replicasReady,
            ParseServiceMode(evt.ServiceMode),
            generation,
            rolloutStatus);
        await _readStore.UpsertComposeServiceAsync(snapshot, ct);

        if (string.Equals(rolloutStatus, "Scaled", StringComparison.Ordinal))
        {
            await _readStore.AppendComposeEventAsync(new ComposeEventSnapshot(
                evt.StackId,
                generation,
                "ComposeServiceScaled",
                $"service={evt.ServiceName},replicas={evt.ReplicasDesired}",
                ResolveOccurredAtUtc(envelope)), ct);
        }
    }

    private async Task ProjectComposeServiceRolledOutAsync(ScriptComposeServiceRolledOutEvent evt, EventEnvelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.StackId) || string.IsNullOrWhiteSpace(evt.ServiceName))
            return;

        var existing = await GetComposeServiceAsync(evt.StackId, evt.ServiceName, ct);
        var generation = evt.Generation > 0
            ? evt.Generation
            : ReadLongMetadata(envelope, GenerationMetadataKey) ?? (existing?.Generation + 1 ?? 1);
        var replicasDesired = ReadIntMetadata(envelope, ReplicasDesiredMetadataKey)
            ?? existing?.ReplicasDesired
            ?? 0;
        var replicasReady = ReadIntMetadata(envelope, ReplicasReadyMetadataKey)
            ?? existing?.ReplicasReady
            ?? replicasDesired;
        var modeRaw = ReadMetadata(envelope, ServiceModeMetadataKey);
        var mode = string.IsNullOrWhiteSpace(modeRaw)
            ? existing?.ServiceMode ?? DynamicServiceMode.Hybrid
            : ParseServiceMode(modeRaw);
        var imageRef = evt.ImageRef ?? existing?.ImageRef ?? string.Empty;

        await _readStore.UpsertComposeServiceAsync(new ComposeServiceSnapshot(
            evt.StackId,
            evt.ServiceName,
            imageRef,
            replicasDesired,
            replicasReady,
            mode,
            generation,
            "RolledOut"), ct);

        await _readStore.AppendComposeEventAsync(new ComposeEventSnapshot(
            evt.StackId,
            generation,
            "ComposeServiceRolledOut",
            $"service={evt.ServiceName},image={imageRef}",
            ResolveOccurredAtUtc(envelope)), ct);
    }

    private async Task ProjectServiceRegisteredAsync(ScriptServiceRegisteredEvent evt, EventEnvelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.ServiceId))
            return;

        await _readStore.UpsertServiceDefinitionAsync(new ServiceDefinitionSnapshot(
            evt.ServiceId,
            evt.Version ?? string.Empty,
            DynamicServiceStatus.Inactive,
            evt.ScriptCode ?? string.Empty,
            evt.EntrypointType ?? string.Empty,
            ParseServiceMode(evt.ServiceMode),
            evt.PublicEndpoints.ToArray(),
            evt.EventSubscriptions.ToArray(),
            evt.CapabilitiesHash ?? string.Empty,
            ResolveUpdatedAtUtc(evt.UpdatedAtUnixMs, envelope),
            ReadCustomStateFromMetadata(envelope)), ct);
    }

    private async Task ProjectServiceUpdatedAsync(ScriptServiceUpdatedEvent evt, EventEnvelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.ServiceId))
            return;

        var existing = await _readStore.GetServiceDefinitionAsync(evt.ServiceId, ct);
        await _readStore.UpsertServiceDefinitionAsync(new ServiceDefinitionSnapshot(
            evt.ServiceId,
            evt.Version ?? string.Empty,
            existing?.Status ?? DynamicServiceStatus.Inactive,
            evt.ScriptCode ?? string.Empty,
            evt.EntrypointType ?? string.Empty,
            ParseServiceMode(evt.ServiceMode),
            evt.PublicEndpoints.ToArray(),
            evt.EventSubscriptions.ToArray(),
            evt.CapabilitiesHash ?? string.Empty,
            ResolveUpdatedAtUtc(evt.UpdatedAtUnixMs, envelope),
            ReadCustomStateFromMetadata(envelope) ?? existing?.CustomState?.Clone()), ct);
    }

    private async Task ProjectServiceStatusAsync(string serviceId, DynamicServiceStatus status, long updatedAtUnixMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
            return;

        var existing = await _readStore.GetServiceDefinitionAsync(serviceId, ct);
        if (existing == null)
            return;

        await _readStore.UpsertServiceDefinitionAsync(existing with
        {
            Status = status,
            UpdatedAtUtc = ResolveUpdatedAtUtc(updatedAtUnixMs, envelope: null),
        }, ct);
    }

    private async Task ProjectServiceCustomStateAsync(ScriptCustomStateUpdatedEvent evt, EventEnvelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.ServiceId))
            return;

        var existing = await _readStore.GetServiceDefinitionAsync(evt.ServiceId, ct);
        if (existing == null)
            return;

        await _readStore.UpsertServiceDefinitionAsync(existing with
        {
            CustomState = evt.CustomState?.Clone(),
            UpdatedAtUtc = ResolveOccurredAtUtc(envelope),
        }, ct);
    }

    private async Task ProjectContainerCreatedAsync(ScriptContainerCreatedEvent evt, EventEnvelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.ContainerId))
            return;

        var serviceId = ReadMetadata(envelope, ServiceIdMetadataKey);
        if (string.IsNullOrWhiteSpace(serviceId))
            serviceId = evt.ServiceName ?? string.Empty;

        await _readStore.UpsertContainerAsync(new ContainerSnapshot(
            evt.ContainerId,
            evt.StackId ?? string.Empty,
            evt.ServiceName ?? string.Empty,
            serviceId,
            evt.ImageDigest ?? string.Empty,
            "Created",
            evt.RoleActorId ?? string.Empty), ct);
    }

    private async Task ProjectContainerStatusAsync(string containerId, string status, EventEnvelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(containerId))
            return;

        var existing = await _readStore.GetContainerAsync(containerId, ct);
        if (existing != null)
        {
            await _readStore.UpsertContainerAsync(existing with { Status = status }, ct);
            return;
        }

        await _readStore.UpsertContainerAsync(new ContainerSnapshot(
            containerId,
            ReadMetadata(envelope, "stack_id"),
            ReadMetadata(envelope, "service_name"),
            ReadMetadata(envelope, ServiceIdMetadataKey),
            string.Empty,
            status,
            string.Empty), ct);
    }

    private async Task ProjectRunStartedAsync(ScriptRunStartedEvent evt, EventEnvelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.RunId))
            return;

        var serviceId = ReadMetadata(envelope, ServiceIdMetadataKey);
        if (string.IsNullOrWhiteSpace(serviceId))
            serviceId = ReadMetadata(envelope, "service_name");

        await _readStore.UpsertRunAsync(new RunSnapshot(
            evt.RunId,
            evt.ContainerId ?? string.Empty,
            serviceId,
            "Running",
            string.Empty,
            string.Empty,
            string.Empty), ct);
    }

    private async Task ProjectRunCompletedAsync(ScriptRunCompletedEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.RunId))
            return;

        var existing = await _readStore.GetRunAsync(evt.RunId, ct);
        await _readStore.UpsertRunAsync((existing ?? new RunSnapshot(evt.RunId, string.Empty, string.Empty, "Running", string.Empty, string.Empty, string.Empty)) with
        {
            Status = "Succeeded",
            Result = evt.Result ?? string.Empty,
            Error = string.Empty,
            CancellationReason = string.Empty,
        }, ct);
    }

    private async Task ProjectRunFailedAsync(ScriptRunFailedEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.RunId))
            return;

        var existing = await _readStore.GetRunAsync(evt.RunId, ct);
        await _readStore.UpsertRunAsync((existing ?? new RunSnapshot(evt.RunId, string.Empty, string.Empty, "Running", string.Empty, string.Empty, string.Empty)) with
        {
            Status = "Failed",
            Result = string.Empty,
            Error = evt.Error ?? string.Empty,
            CancellationReason = string.Empty,
        }, ct);
    }

    private async Task ProjectRunCanceledAsync(ScriptRunCanceledEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.RunId))
            return;

        var existing = await _readStore.GetRunAsync(evt.RunId, ct);
        await _readStore.UpsertRunAsync((existing ?? new RunSnapshot(evt.RunId, string.Empty, string.Empty, "Running", string.Empty, string.Empty, string.Empty)) with
        {
            Status = "Canceled",
            Result = string.Empty,
            Error = string.Empty,
            CancellationReason = evt.Reason ?? string.Empty,
        }, ct);
    }

    private async Task ProjectRunTimedOutAsync(ScriptRunTimedOutEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.RunId))
            return;

        var existing = await _readStore.GetRunAsync(evt.RunId, ct);
        await _readStore.UpsertRunAsync((existing ?? new RunSnapshot(evt.RunId, string.Empty, string.Empty, "Running", string.Empty, string.Empty, string.Empty)) with
        {
            Status = "TimedOut",
            Result = string.Empty,
            Error = evt.Reason ?? string.Empty,
            CancellationReason = string.Empty,
        }, ct);
    }

    private async Task ProjectBuildPlanSubmittedAsync(ScriptBuildPlanSubmittedEvent evt, EventEnvelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.BuildJobId))
            return;

        await _readStore.UpsertBuildJobAsync(new BuildJobSnapshot(
            evt.BuildJobId,
            evt.StackId ?? string.Empty,
            evt.ServiceName ?? string.Empty,
            evt.SourceBundleDigest ?? string.Empty,
            ReadMetadata(envelope, BuildPlanDigestMetadataKey),
            "Planned",
            string.Empty,
            "Planned",
            RequiresManualApproval: false,
            ReadMetadata(envelope, RequestedByAgentIdMetadataKey)), ct);
    }

    private async Task ProjectBuildPolicyValidatedAsync(ScriptBuildPolicyValidatedEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.BuildJobId))
            return;

        var existing = await _readStore.GetBuildJobAsync(evt.BuildJobId, ct);
        if (existing == null)
            return;

        await _readStore.UpsertBuildJobAsync(existing with
        {
            PolicyDecision = evt.PolicyDecision ?? existing.PolicyDecision,
            RequiresManualApproval = evt.RequiresManualApproval,
            Status = evt.RequiresManualApproval ? "ApprovalRequired" : "Validated",
        }, ct);
    }

    private async Task ProjectBuildApprovedAsync(ScriptBuildApprovedEvent evt, EventEnvelope envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.BuildJobId))
            return;

        var existing = await _readStore.GetBuildJobAsync(evt.BuildJobId, ct);
        if (existing == null)
            return;

        await _readStore.UpsertBuildJobAsync(existing with
        {
            RequiresManualApproval = ReadBoolMetadata(envelope, RequiresManualApprovalMetadataKey) ?? existing.RequiresManualApproval,
            Status = "Approved",
        }, ct);
    }

    private async Task ProjectBuildPublishedAsync(ScriptBuildPublishedEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.BuildJobId))
            return;

        var existing = await _readStore.GetBuildJobAsync(evt.BuildJobId, ct);
        if (existing == null)
            return;

        await _readStore.UpsertBuildJobAsync(existing with
        {
            ResultImageDigest = evt.ResultImageDigest ?? string.Empty,
            Status = "Published",
        }, ct);
    }

    private async Task ProjectBuildRolledBackAsync(ScriptBuildRolledBackEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.BuildJobId))
            return;

        var existing = await _readStore.GetBuildJobAsync(evt.BuildJobId, ct);
        if (existing == null)
            return;

        await _readStore.UpsertBuildJobAsync(existing with { Status = "RolledBack" }, ct);
    }

    private static DynamicServiceMode ParseServiceMode(string? value)
    {
        if (string.Equals(value, "daemon", StringComparison.OrdinalIgnoreCase))
            return DynamicServiceMode.Daemon;
        if (string.Equals(value, "event", StringComparison.OrdinalIgnoreCase))
            return DynamicServiceMode.Event;
        return DynamicServiceMode.Hybrid;
    }

    private async Task<ComposeServiceSnapshot?> GetComposeServiceAsync(string stackId, string serviceName, CancellationToken ct)
    {
        var services = await _readStore.GetComposeServicesAsync(stackId, ct);
        return services.FirstOrDefault(item => string.Equals(item.ServiceName, serviceName, StringComparison.Ordinal));
    }

    private static string ReadMetadata(EventEnvelope envelope, string key)
    {
        return envelope.Metadata.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static long? ReadLongMetadata(EventEnvelope envelope, string key)
    {
        var raw = ReadMetadata(envelope, key);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (long.TryParse(raw, out var parsed))
            return parsed;
        return null;
    }

    private static int? ReadIntMetadata(EventEnvelope envelope, string key)
    {
        var raw = ReadMetadata(envelope, key);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (int.TryParse(raw, out var parsed))
            return parsed;
        return null;
    }

    private static DateTime ResolveUpdatedAtUtc(long updatedAtUnixMs, EventEnvelope? envelope)
    {
        if (updatedAtUnixMs > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(updatedAtUnixMs).UtcDateTime;
        return ResolveOccurredAtUtc(envelope);
    }

    private static DateTime ResolveOccurredAtUtc(EventEnvelope? envelope)
    {
        if (envelope?.Metadata != null &&
            envelope.Metadata.TryGetValue("occurred_at", out var occurredAtRaw) &&
            DateTime.TryParse(occurredAtRaw, out var occurredAtUtc))
        {
            return occurredAtUtc.ToUniversalTime();
        }

        return DateTime.UtcNow;
    }

    private static Any? ReadCustomStateFromMetadata(EventEnvelope envelope)
    {
        var typeUrl = ReadMetadata(envelope, CustomStateTypeUrlMetadataKey);
        var valueB64 = ReadMetadata(envelope, CustomStateValueB64MetadataKey);
        if (string.IsNullOrWhiteSpace(typeUrl) || string.IsNullOrWhiteSpace(valueB64))
            return null;

        try
        {
            return new Any
            {
                TypeUrl = typeUrl,
                Value = ByteString.CopyFrom(Convert.FromBase64String(valueB64)),
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadBoolMetadata(EventEnvelope envelope, string key)
    {
        var raw = ReadMetadata(envelope, key);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (bool.TryParse(raw, out var parsed))
            return parsed;
        return null;
    }
}
