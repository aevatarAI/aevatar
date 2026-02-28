using System.Security.Cryptography;
using System.Text;
using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Core.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.DynamicRuntime.Application;

public sealed class DynamicRuntimeApplicationService : IDynamicRuntimeCommandService, IDynamicRuntimeQueryService
{
    private const string PublisherId = "dynamic-runtime.application";
    private const string RuntimeDomainEventKind = "runtime_domain_event";
    private const string ScriptOutputEventKind = "script_output";
    private const int DefaultRunTimeoutMs = 30_000;
    private const int DefaultRetryBackoffMs = 100;
    private const int MaxEnvelopeDispatchCycles = 64;
    private const int MaxDeliveryHop = 8;
    private static readonly TimeSpan EnvelopeDedupTtl = TimeSpan.FromMinutes(10);

    private readonly IActorRuntime _runtime;
    private readonly IDynamicRuntimeReadStore _readStore;
    private readonly IStateStore<ScriptServiceDefinitionState> _serviceDefinitionStateStore;
    private readonly IIdempotencyPort _idempotencyPort;
    private readonly IConcurrencyTokenPort _concurrencyTokenPort;
    private readonly IImageReferenceResolver _imageReferenceResolver;
    private readonly IScriptComposeSpecValidator _composeSpecValidator;
    private readonly IScriptComposeReconcilePort _composeReconcilePort;
    private readonly IAgentBuildPlanPort _buildPlanPort;
    private readonly IAgentBuildPolicyPort _buildPolicyPort;
    private readonly IAgentBuildExecutionPort _buildExecutionPort;
    private readonly IServiceModePolicyPort _serviceModePolicyPort;
    private readonly IBuildApprovalPort _buildApprovalPort;
    private readonly IEventEnvelopePublisherPort _eventEnvelopePublisherPort;
    private readonly IEventEnvelopeSubscriberPort _eventEnvelopeSubscriberPort;
    private readonly IEventEnvelopeDedupPort _eventEnvelopeDedupPort;
    private readonly IEventEnvelopeDeliveryPort _eventEnvelopeDeliveryPort;
    private readonly IDynamicScriptExecutionService _scriptExecutionService;
    private readonly IScriptSideEffectPlanner _scriptSideEffectPlanner;
    private readonly IDynamicRuntimeEventProjector _eventProjector;

    public DynamicRuntimeApplicationService(
        IActorRuntime runtime,
        IDynamicRuntimeReadStore readStore,
        IStateStore<ScriptServiceDefinitionState> serviceDefinitionStateStore,
        IIdempotencyPort idempotencyPort,
        IConcurrencyTokenPort concurrencyTokenPort,
        IImageReferenceResolver imageReferenceResolver,
        IScriptComposeSpecValidator composeSpecValidator,
        IScriptComposeReconcilePort composeReconcilePort,
        IAgentBuildPlanPort buildPlanPort,
        IAgentBuildPolicyPort buildPolicyPort,
        IAgentBuildExecutionPort buildExecutionPort,
        IServiceModePolicyPort serviceModePolicyPort,
        IBuildApprovalPort buildApprovalPort,
        IEventEnvelopePublisherPort eventEnvelopePublisherPort,
        IEventEnvelopeSubscriberPort eventEnvelopeSubscriberPort,
        IEventEnvelopeDedupPort eventEnvelopeDedupPort,
        IEventEnvelopeDeliveryPort eventEnvelopeDeliveryPort,
        IDynamicScriptExecutionService scriptExecutionService,
        IScriptSideEffectPlanner scriptSideEffectPlanner,
        IDynamicRuntimeEventProjector eventProjector)
    {
        _runtime = runtime;
        _readStore = readStore;
        _serviceDefinitionStateStore = serviceDefinitionStateStore;
        _idempotencyPort = idempotencyPort;
        _concurrencyTokenPort = concurrencyTokenPort;
        _imageReferenceResolver = imageReferenceResolver;
        _composeSpecValidator = composeSpecValidator;
        _composeReconcilePort = composeReconcilePort;
        _buildPlanPort = buildPlanPort;
        _buildPolicyPort = buildPolicyPort;
        _buildExecutionPort = buildExecutionPort;
        _serviceModePolicyPort = serviceModePolicyPort;
        _buildApprovalPort = buildApprovalPort;
        _eventEnvelopePublisherPort = eventEnvelopePublisherPort;
        _eventEnvelopeSubscriberPort = eventEnvelopeSubscriberPort;
        _eventEnvelopeDedupPort = eventEnvelopeDedupPort;
        _eventEnvelopeDeliveryPort = eventEnvelopeDeliveryPort;
        _scriptExecutionService = scriptExecutionService;
        _scriptSideEffectPlanner = scriptSideEffectPlanner;
        _eventProjector = eventProjector;
    }

    public async Task<DynamicCommandResult> BuildImageAsync(BuildImageRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("image.build", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        if (string.IsNullOrWhiteSpace(request.ImageName))
            throw new InvalidOperationException("IMAGE_BUILD_INVALID");
        if (string.IsNullOrWhiteSpace(request.SourceBundleDigest))
            throw new InvalidOperationException("IMAGE_BUILD_INVALID");

        var digest = BuildDigest(request.ImageName, request.SourceBundleDigest);
        var tag = string.IsNullOrWhiteSpace(request.Tag) ? "latest" : request.Tag;
        var actorId = await PublishImageInternalAsync(request.ImageName, tag, digest, ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "BUILT", etag);
        await CommitIdempotencyAsync("image.build", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> PublishImageAsync(PublishImageRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("image.publish", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var actorId = await PublishImageInternalAsync(request.ImageName, request.Tag, request.Digest, ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "PUBLISHED", etag);
        await CommitIdempotencyAsync("image.publish", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ApplyComposeAsync(ComposeApplyYamlRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("compose.apply", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var validation = await _composeSpecValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            throw new InvalidOperationException(validation.ErrorCode ?? "COMPOSE_SPEC_INVALID");

        var stackActorId = $"dynamic:stack:{request.StackId}";
        var stackActor = await EnsureActorAsync<ScriptComposeStackGAgent>(stackActorId, ct);
        await PublishActorEventAsync(
            stackActor,
            request.StackId,
            serviceName: "_stack",
            instanceSelector: "stack-controller",
            new ScriptComposeAppliedEvent
        {
            StackId = request.StackId,
            ComposeSpecDigest = request.ComposeSpecDigest,
            DesiredGeneration = request.DesiredGeneration,
        },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["compose_yaml"] = request.ComposeYaml ?? string.Empty,
                ["services_count"] = request.Services.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["compose_action"] = "apply",
            },
            ct);

        foreach (var service in request.Services)
        {
            await ValidateServiceModeAsync(request.StackId, service, ct);

            var serviceActorId = $"dynamic:stack:{request.StackId}:service:{service.ServiceName}";
            var serviceActor = await EnsureActorAsync<ScriptComposeServiceGAgent>(serviceActorId, ct);
            await EnsureLinkedAsync(stackActorId, serviceActorId, ct);
            await PublishActorEventAsync(
                serviceActor,
                request.StackId,
                service.ServiceName,
                "all",
                new ScriptComposeServiceScaledEvent
            {
                StackId = request.StackId,
                ServiceName = service.ServiceName,
                ReplicasDesired = service.ReplicasDesired,
                ServiceMode = service.ServiceMode.ToString().ToLowerInvariant(),
                ImageRef = service.ImageRef,
            },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["generation"] = request.DesiredGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["replicas_ready"] = service.ReplicasDesired.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["rollout_status"] = "RolledOut",
                },
                ct);

            await EnsureServiceEnvelopeSubscriptionAsync(
                request.StackId,
                service.ServiceName,
                service.ServiceMode,
                request.DesiredGeneration,
                ct);
        }

        var reconcile = await _composeReconcilePort.ReconcileAsync(request.StackId, request.DesiredGeneration, ct);
        if (!reconcile.Converged)
            throw new InvalidOperationException(reconcile.ErrorCode ?? "COMPOSE_GENERATION_CONFLICT");
        await PublishActorEventAsync(
            stackActor,
            request.StackId,
            serviceName: "_stack",
            instanceSelector: "stack-controller",
            new ScriptComposeConvergedEvent
        {
            StackId = request.StackId,
            ObservedGeneration = reconcile.ObservedGeneration,
        },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["desired_generation"] = request.DesiredGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["compose_action"] = "apply",
            },
            ct);

        var etag = await AdvanceVersionAsync(stackActorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(stackActorId, "APPLIED", etag);
        await CommitIdempotencyAsync("compose.apply", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ComposeUpAsync(string stackId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("compose.up", context.IdempotencyKey, new { stackId }, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var snapshot = await _readStore.GetStackAsync(stackId, ct)
            ?? throw new InvalidOperationException($"Stack '{stackId}' does not exist.");

        var stackActorId = $"dynamic:stack:{stackId}";
        var stackActor = await EnsureActorAsync<ScriptComposeStackGAgent>(stackActorId, ct);
        await PublishActorEventAsync(
            stackActor,
            stackId,
            serviceName: "_stack",
            instanceSelector: "stack-controller",
            new ScriptComposeConvergedEvent
        {
            StackId = stackId,
            ObservedGeneration = snapshot.DesiredGeneration,
        },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["desired_generation"] = snapshot.DesiredGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["compose_action"] = "up",
            },
            ct);

        var etag = await AdvanceVersionAsync(stackActorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(stackActorId, "UP", etag);
        await CommitIdempotencyAsync("compose.up", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ComposeDownAsync(string stackId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("compose.down", context.IdempotencyKey, new { stackId }, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var snapshot = await _readStore.GetStackAsync(stackId, ct)
            ?? throw new InvalidOperationException($"Stack '{stackId}' does not exist.");
        var nextGeneration = snapshot.DesiredGeneration + 1;

        var stackActorId = $"dynamic:stack:{stackId}";
        var services = await _readStore.GetComposeServicesAsync(stackId, ct);
        foreach (var service in services)
        {
            var serviceActorId = $"dynamic:stack:{stackId}:service:{service.ServiceName}";
            var serviceActor = await EnsureActorAsync<ScriptComposeServiceGAgent>(serviceActorId, ct);
            await EnsureLinkedAsync(stackActorId, serviceActorId, ct);
            await PublishActorEventAsync(
                serviceActor,
                stackId,
                service.ServiceName,
                "all",
                new ScriptComposeServiceScaledEvent
            {
                StackId = stackId,
                ServiceName = service.ServiceName,
                ReplicasDesired = 0,
                ServiceMode = service.ServiceMode.ToString().ToLowerInvariant(),
                ImageRef = service.ImageRef,
            },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["generation"] = nextGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["replicas_ready"] = "0",
                    ["rollout_status"] = "Stopped",
                },
                ct);
        }
        await PublishActorEventAsync(
            await EnsureActorAsync<ScriptComposeStackGAgent>(stackActorId, ct),
            stackId,
            serviceName: "_stack",
            instanceSelector: "stack-controller",
            new ScriptComposeConvergedEvent
            {
                StackId = stackId,
                ObservedGeneration = nextGeneration,
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["desired_generation"] = nextGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["compose_action"] = "down",
            },
            ct);
        var etag = await AdvanceVersionAsync(stackActorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(stackActorId, "DOWN", etag);
        await CommitIdempotencyAsync("compose.down", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ScaleComposeServiceAsync(ComposeServiceScaleRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("compose.service.scale", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var serviceSnapshot = await GetComposeServiceAsync(request.StackId, request.ServiceName, ct);
        await ValidateServiceModeAsync(request.StackId, new ComposeServiceSpec(serviceSnapshot.ServiceName, serviceSnapshot.ImageRef, request.ReplicasDesired, serviceSnapshot.ServiceMode), ct);

        var serviceActorId = $"dynamic:stack:{request.StackId}:service:{request.ServiceName}";
        var serviceActor = await EnsureActorAsync<ScriptComposeServiceGAgent>(serviceActorId, ct);
        await PublishActorEventAsync(
            serviceActor,
            request.StackId,
            request.ServiceName,
            "all",
            new ScriptComposeServiceScaledEvent
        {
            StackId = request.StackId,
            ServiceName = request.ServiceName,
            ReplicasDesired = request.ReplicasDesired,
            ServiceMode = serviceSnapshot.ServiceMode.ToString().ToLowerInvariant(),
            ImageRef = serviceSnapshot.ImageRef,
        },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["generation"] = (serviceSnapshot.Generation + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["replicas_ready"] = request.ReplicasDesired.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["rollout_status"] = "Scaled",
            },
            ct);

        var etag = await AdvanceVersionAsync(serviceActorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(serviceActorId, "SCALED", etag);
        await CommitIdempotencyAsync("compose.service.scale", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> RolloutComposeServiceAsync(ComposeServiceRolloutRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("compose.service.rollout", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var serviceSnapshot = await GetComposeServiceAsync(request.StackId, request.ServiceName, ct);
        var resolvedImageDigest = await ResolveImageDigestAsync($"{request.StackId}/{request.ServiceName}", request.ImageRef, ct);
        var nextGeneration = serviceSnapshot.Generation + 1;

        var serviceActorId = $"dynamic:stack:{request.StackId}:service:{request.ServiceName}";
        var serviceActor = await EnsureActorAsync<ScriptComposeServiceGAgent>(serviceActorId, ct);
        await PublishActorEventAsync(
            serviceActor,
            request.StackId,
            request.ServiceName,
            "all",
            new ScriptComposeServiceRolledOutEvent
            {
                StackId = request.StackId,
                ServiceName = request.ServiceName,
                ImageRef = resolvedImageDigest,
                Generation = nextGeneration,
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service_mode"] = serviceSnapshot.ServiceMode.ToString().ToLowerInvariant(),
                ["replicas_desired"] = serviceSnapshot.ReplicasDesired.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["replicas_ready"] = serviceSnapshot.ReplicasReady.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            ct);

        var etag = await AdvanceVersionAsync(serviceActorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(serviceActorId, "ROLLED_OUT", etag);
        await CommitIdempotencyAsync("compose.service.rollout", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> RegisterServiceAsync(RegisterServiceDefinitionRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("service.register", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        ValidateServiceRequest(request.ServiceId, request.Version, request.ScriptCode, request.EntrypointType);

        var updatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var actorId = $"dynamic:service:{request.ServiceId}";
        var actor = await EnsureActorAsync<ScriptServiceDefinitionGAgent>(actorId, ct);
        var registeredEvent = new ScriptServiceRegisteredEvent
        {
            ServiceId = request.ServiceId,
            Version = request.Version,
            ScriptCode = request.ScriptCode,
            EntrypointType = request.EntrypointType,
            ServiceMode = request.ServiceMode.ToString().ToLowerInvariant(),
            CapabilitiesHash = request.CapabilitiesHash,
            UpdatedAtUnixMs = updatedAtUnixMs,
        };
        registeredEvent.PublicEndpoints.AddRange(request.PublicEndpoints);
        registeredEvent.EventSubscriptions.AddRange(request.EventSubscriptions);
        await PublishActorEventAsync(
            actor,
            stackId: "_services",
            serviceName: request.ServiceId,
            instanceSelector: "definition",
            registeredEvent,
            BuildCustomStateMetadata(request.CustomState),
            ct);

        var state = new ScriptServiceDefinitionState
        {
            ServiceId = request.ServiceId,
            Version = request.Version,
            Status = DynamicServiceStatus.Inactive.ToString(),
            ScriptCode = request.ScriptCode,
            EntrypointType = request.EntrypointType,
            ServiceMode = request.ServiceMode.ToString().ToLowerInvariant(),
            CapabilitiesHash = request.CapabilitiesHash,
            UpdatedAtUnixMs = updatedAtUnixMs,
            CustomState = request.CustomState?.Clone(),
        };
        state.PublicEndpoints.AddRange(request.PublicEndpoints);
        state.EventSubscriptions.AddRange(request.EventSubscriptions);
        await _serviceDefinitionStateStore.SaveAsync(actorId, state, ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "REGISTERED", etag);
        await CommitIdempotencyAsync("service.register", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> UpdateServiceAsync(UpdateServiceDefinitionRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("service.update", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        ValidateServiceRequest(request.ServiceId, request.Version, request.ScriptCode, request.EntrypointType);

        var updatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var actorId = $"dynamic:service:{request.ServiceId}";
        var actor = await EnsureActorAsync<ScriptServiceDefinitionGAgent>(actorId, ct);
        var updatedEvent = new ScriptServiceUpdatedEvent
        {
            ServiceId = request.ServiceId,
            Version = request.Version,
            ScriptCode = request.ScriptCode,
            EntrypointType = request.EntrypointType,
            ServiceMode = request.ServiceMode.ToString().ToLowerInvariant(),
            CapabilitiesHash = request.CapabilitiesHash,
            UpdatedAtUnixMs = updatedAtUnixMs,
        };
        updatedEvent.PublicEndpoints.AddRange(request.PublicEndpoints);
        updatedEvent.EventSubscriptions.AddRange(request.EventSubscriptions);
        await PublishActorEventAsync(
            actor,
            stackId: "_services",
            serviceName: request.ServiceId,
            instanceSelector: "definition",
            updatedEvent,
            BuildCustomStateMetadata(request.CustomState),
            ct);

        var existingState = await _serviceDefinitionStateStore.LoadAsync(actorId, ct);
        var nextState = existingState?.Clone() ?? new ScriptServiceDefinitionState();
        nextState.ServiceId = request.ServiceId;
        nextState.Version = request.Version;
        nextState.ScriptCode = request.ScriptCode;
        nextState.EntrypointType = request.EntrypointType;
        nextState.ServiceMode = request.ServiceMode.ToString().ToLowerInvariant();
        nextState.CapabilitiesHash = request.CapabilitiesHash;
        nextState.UpdatedAtUnixMs = updatedAtUnixMs;
        if (request.CustomState != null)
            nextState.CustomState = request.CustomState.Clone();
        if (string.IsNullOrWhiteSpace(nextState.Status))
            nextState.Status = DynamicServiceStatus.Inactive.ToString();
        nextState.PublicEndpoints.Clear();
        nextState.PublicEndpoints.AddRange(request.PublicEndpoints);
        nextState.EventSubscriptions.Clear();
        nextState.EventSubscriptions.AddRange(request.EventSubscriptions);
        await _serviceDefinitionStateStore.SaveAsync(actorId, nextState, ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "UPDATED", etag);
        await CommitIdempotencyAsync("service.update", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ActivateServiceAsync(string serviceId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("service.activate", context.IdempotencyKey, new { serviceId }, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var result = await SetServiceStatusAsync(serviceId, DynamicServiceStatus.Active, context, ct);
        await CommitIdempotencyAsync("service.activate", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> DeactivateServiceAsync(string serviceId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("service.deactivate", context.IdempotencyKey, new { serviceId }, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var result = await SetServiceStatusAsync(serviceId, DynamicServiceStatus.Inactive, context, ct);
        await CommitIdempotencyAsync("service.deactivate", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> CreateContainerAsync(CreateContainerRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("container.create", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        var resolvedImageDigest = await ResolveImageDigestAsync(request.ServiceId, request.ImageDigest, ct);

        var actorId = $"dynamic:container:{request.ContainerId}";
        var actor = await EnsureActorAsync<ScriptContainerGAgent>(actorId, ct);
        var stackActorId = $"dynamic:stack:{request.StackId}";
        var serviceActorId = $"dynamic:stack:{request.StackId}:service:{request.ServiceName}";
        await EnsureActorAsync<ScriptComposeStackGAgent>(stackActorId, ct);
        await EnsureActorAsync<ScriptComposeServiceGAgent>(serviceActorId, ct);
        await EnsureLinkedAsync(stackActorId, serviceActorId, ct);
        await EnsureLinkedAsync(serviceActorId, actorId, ct);
        await PublishActorEventAsync(
            actor,
            request.StackId,
            request.ServiceName,
            request.ContainerId,
            new ScriptContainerCreatedEvent
        {
            ContainerId = request.ContainerId,
            StackId = request.StackId,
            ServiceName = request.ServiceName,
            ImageDigest = resolvedImageDigest,
            RoleActorId = request.RoleActorId,
        },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service_id"] = request.ServiceId,
            },
            ct);

        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "CREATED", etag);
        await CommitIdempotencyAsync("container.create", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> StartContainerAsync(string containerId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("container.start", context.IdempotencyKey, new { containerId }, ct);
        if (replay != null)
            return replay;
        var snapshot = await _readStore.GetContainerAsync(containerId, ct)
            ?? throw new InvalidOperationException($"Container '{containerId}' does not exist.");
        var serviceState = await GetServiceStateAsync(snapshot.ServiceId, ct);

        var actorId = $"dynamic:container:{containerId}";
        var actor = await EnsureActorAsync<ScriptContainerGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            snapshot.StackId,
            snapshot.ServiceName,
            snapshot.ContainerId,
            new ScriptContainerStartedEvent { ContainerId = containerId },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service_id"] = snapshot.ServiceId,
            },
            ct);

        var roleActor = await EnsureActorAsync<ScriptRoleContainerAgent>(snapshot.RoleActorId, ct);
        await EnsureLinkedAsync(actorId, snapshot.RoleActorId, ct);
        await PublishActorEventAsync(
            roleActor,
            snapshot.StackId,
            snapshot.ServiceName,
            snapshot.ContainerId,
            new ConfigureScriptRoleCapabilitiesEvent
            {
                ServiceId = serviceState.ServiceId,
                Version = serviceState.Version,
                ImageDigest = snapshot.ImageDigest,
                EntrypointType = serviceState.EntrypointType,
                CapabilitiesHash = serviceState.CapabilitiesHash,
            },
            ct);

        await EnsureServiceEnvelopeSubscriptionAsync(
            snapshot.StackId,
            snapshot.ServiceName,
            ParseServiceMode(serviceState.ServiceMode),
            generation: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ct);

        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "RUNNING", etag);
        await CommitIdempotencyAsync("container.start", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> StopContainerAsync(string containerId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("container.stop", context.IdempotencyKey, new { containerId }, ct);
        if (replay != null)
            return replay;
        var snapshot = await _readStore.GetContainerAsync(containerId, ct)
            ?? throw new InvalidOperationException($"Container '{containerId}' does not exist.");

        var actorId = $"dynamic:container:{containerId}";
        var actor = await EnsureActorAsync<ScriptContainerGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            snapshot.StackId,
            snapshot.ServiceName,
            snapshot.ContainerId,
            new ScriptContainerStoppedEvent { ContainerId = containerId },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service_id"] = snapshot.ServiceId,
            },
            ct);
        await UnlinkIfLinkedAsync(actorId, ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "STOPPED", etag);
        await CommitIdempotencyAsync("container.stop", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> DestroyContainerAsync(string containerId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("container.destroy", context.IdempotencyKey, new { containerId }, ct);
        if (replay != null)
            return replay;
        var snapshot = await _readStore.GetContainerAsync(containerId, ct)
            ?? throw new InvalidOperationException($"Container '{containerId}' does not exist.");

        var actorId = $"dynamic:container:{containerId}";
        var actor = await EnsureActorAsync<ScriptContainerGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            snapshot.StackId,
            snapshot.ServiceName,
            snapshot.ContainerId,
            new ScriptContainerDestroyedEvent { ContainerId = containerId },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service_id"] = snapshot.ServiceId,
            },
            ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "DESTROYED", etag);
        await CommitIdempotencyAsync("container.destroy", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ExecuteContainerAsync(ExecuteContainerRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        return await ExecuteContainerInternalAsync(request, context, dispatchFollowUp: true, useIdempotency: true, ct);
    }

    private async Task<DynamicCommandResult> ExecuteContainerInternalAsync(
        ExecuteContainerRequest request,
        DynamicCommandContext context,
        bool dispatchFollowUp,
        bool useIdempotency,
        CancellationToken ct)
    {
        if (useIdempotency)
        {
            var replay = await EnsureIdempotentAsync("container.exec", context.IdempotencyKey, request, ct);
            if (replay != null)
                return replay;
        }

        var result = await ExecuteContainerCoreAsync(request, context.IfMatch, dispatchFollowUp, ct);
        if (useIdempotency)
            await CommitIdempotencyAsync("container.exec", context.IdempotencyKey, result, ct);
        return result;
    }

    private async Task<DynamicCommandResult> ExecuteContainerCoreAsync(
        ExecuteContainerRequest request,
        string? ifMatch,
        bool dispatchFollowUp,
        CancellationToken ct)
    {
        var container = await _readStore.GetContainerAsync(request.ContainerId, ct)
            ?? throw new InvalidOperationException($"Container '{request.ContainerId}' does not exist.");
        if (!string.Equals(container.Status, "Running", StringComparison.Ordinal))
            throw new InvalidOperationException("CONTAINER_STATE_CONFLICT");

        var service = await GetServiceStateAsync(request.ServiceId, ct);
        if (!string.Equals(service.Status, DynamicServiceStatus.Active.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException("SERVICE_MODE_CONFLICT");

        var runId = string.IsNullOrWhiteSpace(request.RunId) ? Guid.NewGuid().ToString("N") : request.RunId;
        var runActorId = $"dynamic:run:{runId}";
        var runActor = await EnsureActorAsync<ScriptRunGAgent>(runActorId, ct);
        await EnsureLinkedAsync($"dynamic:container:{container.ContainerId}", runActorId, ct);

        var maxAttempts = Math.Max(1, request.MaxRetries + 1);
        var timeoutMs = NormalizeRunTimeoutMs(request.TimeoutMs);
        var retryBackoffMs = request.RetryBackoffMs <= 0 ? DefaultRetryBackoffMs : request.RetryBackoffMs;
        var runMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["service_id"] = request.ServiceId,
        };

        string status = "FAILED";
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var startedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await PublishActorEventAsync(
                runActor,
                container.StackId,
                container.ServiceName,
                container.ContainerId,
                new ScriptRunStartedEvent
                {
                    RunId = runId,
                    ContainerId = request.ContainerId,
                    Attempt = attempt,
                    MaxAttempts = maxAttempts,
                    StartedAtUnixMs = startedAtUnixMs,
                },
                runMetadata,
                ct);

            var scriptEnvelope = BuildScriptExecutionEnvelope(
                request.Envelope,
                runId,
                request.ContainerId,
                request.ServiceId,
                container.StackId,
                container.ServiceName,
                attempt,
                maxAttempts);

            var (scriptResult, timedOut, timeoutReason) = await ExecuteScriptAttemptAsync(service, scriptEnvelope, timeoutMs, ct);
            if (timedOut)
            {
                var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await PublishActorEventAsync(
                    runActor,
                    container.StackId,
                    container.ServiceName,
                    container.ContainerId,
                    new ScriptRunAttemptTimedOutEvent
                    {
                        RunId = runId,
                        Attempt = attempt,
                        MaxAttempts = maxAttempts,
                        Reason = timeoutReason,
                        OccurredAtUnixMs = nowUnixMs,
                    },
                    runMetadata,
                    ct);

                if (attempt < maxAttempts)
                {
                    var backoff = ComputeRunRetryBackoffMs(retryBackoffMs, attempt);
                    await PublishActorEventAsync(
                        runActor,
                        container.StackId,
                        container.ServiceName,
                        container.ContainerId,
                        new ScriptRunRetryScheduledEvent
                        {
                            RunId = runId,
                            Attempt = attempt + 1,
                            MaxAttempts = maxAttempts,
                            BackoffMs = backoff,
                            Reason = timeoutReason,
                            OccurredAtUnixMs = nowUnixMs,
                        },
                        runMetadata,
                        ct);
                    await Task.Delay(TimeSpan.FromMilliseconds(backoff), ct);
                    continue;
                }

                await PublishActorEventAsync(
                    runActor,
                    container.StackId,
                    container.ServiceName,
                    container.ContainerId,
                    new ScriptRunTimedOutEvent
                    {
                        RunId = runId,
                        Reason = timeoutReason,
                        CompletedAtUnixMs = nowUnixMs,
                    },
                    runMetadata,
                    ct);
                status = "TIMED_OUT";
                break;
            }

            if (scriptResult.Success)
            {
                try
                {
                    await ApplyScriptSideEffectsAsync(
                        runActor,
                        runId,
                        request.ServiceId,
                        service,
                        container.StackId,
                        container.ServiceName,
                        container.ContainerId,
                        scriptResult,
                        ct);

                    await PublishScriptOutputEnvelopesAsync(container.StackId, container.ServiceName, container.ContainerId, scriptResult.PublishedEvents, ct);

                    await PublishActorEventAsync(
                        runActor,
                        container.StackId,
                        container.ServiceName,
                        container.ContainerId,
                        new ScriptRunCompletedEvent
                        {
                            RunId = runId,
                            Result = scriptResult.Output,
                            CompletedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        },
                        runMetadata,
                        ct);

                    status = "SUCCEEDED";
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var reason = $"SCRIPT_SIDE_EFFECT_FAILED: {ex.Message}";
                    if (attempt < maxAttempts)
                    {
                        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var backoff = ComputeRunRetryBackoffMs(retryBackoffMs, attempt);
                        await PublishActorEventAsync(
                            runActor,
                            container.StackId,
                            container.ServiceName,
                            container.ContainerId,
                            new ScriptRunRetryScheduledEvent
                            {
                                RunId = runId,
                                Attempt = attempt + 1,
                                MaxAttempts = maxAttempts,
                                BackoffMs = backoff,
                                Reason = reason,
                                OccurredAtUnixMs = nowUnixMs,
                            },
                            runMetadata,
                            ct);
                        await Task.Delay(TimeSpan.FromMilliseconds(backoff), ct);
                        continue;
                    }

                    await PublishActorEventAsync(
                        runActor,
                        container.StackId,
                        container.ServiceName,
                        container.ContainerId,
                        new ScriptRunFailedEvent
                        {
                            RunId = runId,
                            Error = reason,
                            CompletedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        },
                        runMetadata,
                        ct);
                    status = "FAILED";
                    break;
                }
            }

            var failureReason = scriptResult.Error ?? "Unknown script failure.";
            if (attempt < maxAttempts)
            {
                var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var backoff = ComputeRunRetryBackoffMs(retryBackoffMs, attempt);
                await PublishActorEventAsync(
                    runActor,
                    container.StackId,
                    container.ServiceName,
                    container.ContainerId,
                    new ScriptRunRetryScheduledEvent
                    {
                        RunId = runId,
                        Attempt = attempt + 1,
                        MaxAttempts = maxAttempts,
                        BackoffMs = backoff,
                        Reason = failureReason,
                        OccurredAtUnixMs = nowUnixMs,
                    },
                    runMetadata,
                    ct);
                await Task.Delay(TimeSpan.FromMilliseconds(backoff), ct);
                continue;
            }

            await PublishActorEventAsync(
                runActor,
                container.StackId,
                container.ServiceName,
                container.ContainerId,
                new ScriptRunFailedEvent
                {
                    RunId = runId,
                    Error = failureReason,
                    CompletedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
                runMetadata,
                ct);
            status = "FAILED";
            break;
        }

        var etag = await AdvanceVersionAsync(runActorId, ifMatch, ct);
        if (dispatchFollowUp && string.Equals(status, "SUCCEEDED", StringComparison.Ordinal))
            await DispatchStackEnvelopesAsync(container.StackId, ct);

        return new DynamicCommandResult(runActorId, status, etag);
    }

    public async Task<DynamicCommandResult> CancelRunAsync(string runId, string reason, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("run.cancel", context.IdempotencyKey, new { runId, reason }, ct);
        if (replay != null)
            return replay;
        var snapshot = await _readStore.GetRunAsync(runId, ct)
            ?? throw new InvalidOperationException($"Run '{runId}' does not exist.");
        if (IsRunTerminal(snapshot.Status))
            throw new InvalidOperationException("RUN_ALREADY_TERMINAL");

        var actorId = $"dynamic:run:{runId}";
        var actor = await EnsureActorAsync<ScriptRunGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            stackId: "_runs",
            serviceName: snapshot.ServiceId,
            instanceSelector: snapshot.ContainerId,
            new ScriptRunCanceledEvent
            {
                RunId = runId,
                Reason = reason ?? string.Empty,
                CompletedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service_id"] = snapshot.ServiceId,
            },
            ct);

        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "CANCELED", etag);
        await CommitIdempotencyAsync("run.cancel", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> SubmitBuildPlanAsync(SubmitBuildPlanRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("build.plan", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        var plan = await _buildPlanPort.PlanAsync(new BuildPlanRequest(request.BuildJobId, request.StackId, request.ServiceName, request.SourceBundleDigest), ct);
        if (!plan.Accepted)
            throw new InvalidOperationException(plan.ErrorCode ?? "BUILD_POLICY_REJECTED");

        var actorId = $"dynamic:build:{request.BuildJobId}";
        var actor = await EnsureActorAsync<ScriptBuildJobGAgent>(actorId, ct);
        var stackActorId = $"dynamic:stack:{request.StackId}";
        var serviceActorId = $"dynamic:stack:{request.StackId}:service:{request.ServiceName}";
        await EnsureActorAsync<ScriptComposeStackGAgent>(stackActorId, ct);
        await EnsureActorAsync<ScriptComposeServiceGAgent>(serviceActorId, ct);
        await EnsureLinkedAsync(stackActorId, serviceActorId, ct);
        await EnsureLinkedAsync(serviceActorId, actorId, ct);
        await PublishActorEventAsync(
            actor,
            request.StackId,
            request.ServiceName,
            request.BuildJobId,
            new ScriptBuildPlanSubmittedEvent
        {
            BuildJobId = request.BuildJobId,
            StackId = request.StackId,
            ServiceName = request.ServiceName,
            SourceBundleDigest = request.SourceBundleDigest,
        },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["build_plan_digest"] = plan.BuildPlanDigest,
                ["requested_by_agent_id"] = request.RequestedByAgentId ?? string.Empty,
                ["requires_manual_approval"] = bool.FalseString,
            },
            ct);

        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "PLANNED", etag);
        await CommitIdempotencyAsync("build.plan", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ValidateBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("build.validate", context.IdempotencyKey, new { buildJobId }, ct);
        if (replay != null)
            return replay;
        var build = await _readStore.GetBuildJobAsync(buildJobId, ct)
            ?? throw new InvalidOperationException($"Build job '{buildJobId}' does not exist.");
        var policyDecision = await _buildPolicyPort.ValidateAsync(new BuildPolicyRequest(
            build.BuildJobId,
            build.StackId,
            build.ServiceName,
            build.SourceBundleDigest,
            build.BuildPlanDigest), ct);
        if (!policyDecision.Allowed)
        {
            throw new InvalidOperationException(policyDecision.ErrorCode ?? "BUILD_POLICY_REJECTED");
        }

        var actorId = $"dynamic:build:{buildJobId}";
        var actor = await EnsureActorAsync<ScriptBuildJobGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            build.StackId,
            build.ServiceName,
            build.BuildJobId,
            new ScriptBuildPolicyValidatedEvent
            {
                BuildJobId = build.BuildJobId,
                PolicyDecision = policyDecision.PolicyDecision,
                RequiresManualApproval = policyDecision.RequiresManualApproval,
            },
            ct);

        var status = policyDecision.RequiresManualApproval ? "ApprovalRequired" : "Validated";

        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, status.ToUpperInvariant(), etag);
        await CommitIdempotencyAsync("build.validate", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ApproveBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("build.approve", context.IdempotencyKey, new { buildJobId }, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var build = await _readStore.GetBuildJobAsync(buildJobId, ct)
            ?? throw new InvalidOperationException($"Build job '{buildJobId}' does not exist.");
        var approval = await _buildApprovalPort.DecideAsync(new BuildApprovalRequest(build.BuildJobId, build.StackId, build.ServiceName, build.SourceBundleDigest), ct);
        if (!approval.Approved)
            throw new InvalidOperationException(approval.Reason ?? "BUILD_APPROVAL_REQUIRED");

        var actorId = $"dynamic:build:{buildJobId}";
        var actor = await EnsureActorAsync<ScriptBuildJobGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            build.StackId,
            build.ServiceName,
            build.BuildJobId,
            new ScriptBuildApprovedEvent
        {
            BuildJobId = buildJobId,
        },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["requires_manual_approval"] = approval.RequiresManualApproval ? bool.TrueString : bool.FalseString,
            },
            ct);

        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "APPROVED", etag);
        await CommitIdempotencyAsync("build.approve", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> ExecuteBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("build.execute", context.IdempotencyKey, new { buildJobId }, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);

        var build = await _readStore.GetBuildJobAsync(buildJobId, ct)
            ?? throw new InvalidOperationException($"Build job '{buildJobId}' does not exist.");
        if (!string.Equals(build.Status, "Approved", StringComparison.OrdinalIgnoreCase) &&
            !(string.Equals(build.Status, "Validated", StringComparison.OrdinalIgnoreCase) && !build.RequiresManualApproval))
            throw new InvalidOperationException("BUILD_APPROVAL_REQUIRED");

        var executionResult = await _buildExecutionPort.ExecuteAsync(new BuildExecutionRequest(
            build.BuildJobId,
            $"{build.StackId}/{build.ServiceName}",
            build.SourceBundleDigest,
            build.BuildPlanDigest), ct);
        if (!executionResult.Succeeded)
            throw new InvalidOperationException(executionResult.ErrorCode ?? "BUILD_POLICY_REJECTED");

        var publishResult = await PublishBuildResultInternalAsync(new PublishBuildResultRequest(buildJobId, executionResult.ResultImageDigest), context.IfMatch, ct);
        await TryDeployBuildResultToComposeAsync(build.StackId, build.ServiceName, executionResult.ResultImageDigest, ct);

        var actorId = $"dynamic:build:{buildJobId}";
        var result = new DynamicCommandResult(actorId, "EXECUTED", publishResult.ETag);
        await CommitIdempotencyAsync("build.execute", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> RollbackBuildAsync(string buildJobId, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("build.rollback", context.IdempotencyKey, new { buildJobId }, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var build = await _readStore.GetBuildJobAsync(buildJobId, ct)
            ?? throw new InvalidOperationException($"Build job '{buildJobId}' does not exist.");
        var actorId = $"dynamic:build:{buildJobId}";
        var actor = await EnsureActorAsync<ScriptBuildJobGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            build.StackId,
            build.ServiceName,
            build.BuildJobId,
            new ScriptBuildRolledBackEvent { BuildJobId = buildJobId },
            ct);
        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        var result = new DynamicCommandResult(actorId, "ROLLEDBACK", etag);
        await CommitIdempotencyAsync("build.rollback", context.IdempotencyKey, result, ct);
        return result;
    }

    public async Task<DynamicCommandResult> PublishBuildResultAsync(PublishBuildResultRequest request, DynamicCommandContext context, CancellationToken ct = default)
    {
        var replay = await EnsureIdempotentAsync("build.publish", context.IdempotencyKey, request, ct);
        if (replay != null)
            return replay;
        RequireIfMatch(context.IfMatch);
        var result = await PublishBuildResultInternalAsync(request, context.IfMatch, ct);
        await CommitIdempotencyAsync("build.publish", context.IdempotencyKey, result, ct);
        return result;
    }

    public Task<ImageSnapshot?> GetImageAsync(string imageName, CancellationToken ct = default) => _readStore.GetImageAsync(imageName, ct);
    public Task<StackSnapshot?> GetStackAsync(string stackId, CancellationToken ct = default) => _readStore.GetStackAsync(stackId, ct);
    public Task<IReadOnlyList<ComposeServiceSnapshot>> GetComposeServicesAsync(string stackId, CancellationToken ct = default) => _readStore.GetComposeServicesAsync(stackId, ct);
    public Task<IReadOnlyList<ComposeEventSnapshot>> GetComposeEventsAsync(string stackId, CancellationToken ct = default) => _readStore.GetComposeEventsAsync(stackId, ct);
    public Task<ServiceDefinitionSnapshot?> GetServiceDefinitionAsync(string serviceId, CancellationToken ct = default) => _readStore.GetServiceDefinitionAsync(serviceId, ct);
    public Task<ContainerSnapshot?> GetContainerAsync(string containerId, CancellationToken ct = default) => _readStore.GetContainerAsync(containerId, ct);
    public Task<IReadOnlyList<RunSnapshot>> GetContainerRunsAsync(string containerId, CancellationToken ct = default) => _readStore.GetContainerRunsAsync(containerId, ct);
    public Task<RunSnapshot?> GetRunAsync(string runId, CancellationToken ct = default) => _readStore.GetRunAsync(runId, ct);
    public Task<BuildJobSnapshot?> GetBuildJobAsync(string buildJobId, CancellationToken ct = default) => _readStore.GetBuildJobAsync(buildJobId, ct);
    public Task<IReadOnlyList<BuildJobSnapshot>> GetBuildJobsAsync(CancellationToken ct = default) => _readStore.GetBuildJobsAsync(ct);
    public Task<IReadOnlyList<ScriptReadModelDefinitionSnapshot>> GetScriptReadModelDefinitionsAsync(string serviceId, CancellationToken ct = default) => _readStore.GetScriptReadModelDefinitionsAsync(serviceId, ct);
    public Task<IReadOnlyList<ScriptReadModelRelationSnapshot>> GetScriptReadModelRelationsAsync(string serviceId, CancellationToken ct = default) => _readStore.GetScriptReadModelRelationsAsync(serviceId, ct);
    public Task<IReadOnlyList<ScriptReadModelDocumentSnapshot>> GetScriptReadModelDocumentsAsync(string serviceId, string readModelName, CancellationToken ct = default) => _readStore.GetScriptReadModelDocumentsAsync(serviceId, readModelName, ct);
    public Task<ScriptReadModelDocumentSnapshot?> GetScriptReadModelDocumentAsync(string serviceId, string readModelName, string documentId, CancellationToken ct = default) => _readStore.GetScriptReadModelDocumentAsync(serviceId, readModelName, documentId, ct);

    public async Task<ImageTagSnapshot?> GetImageTagAsync(string imageName, string tag, CancellationToken ct = default)
    {
        var image = await _readStore.GetImageAsync(imageName, ct);
        if (image == null || !image.Tags.TryGetValue(tag, out var digest))
            return null;
        return new ImageTagSnapshot(imageName, tag, digest);
    }

    public async Task<ImageDigestSnapshot?> GetImageDigestAsync(string imageName, string digest, CancellationToken ct = default)
    {
        var image = await _readStore.GetImageAsync(imageName, ct);
        if (image == null)
            return null;
        return new ImageDigestSnapshot(imageName, digest, image.Digests.Contains(digest, StringComparer.Ordinal));
    }

    private async Task<DynamicCommandResult> SetServiceStatusAsync(string serviceId, DynamicServiceStatus status, DynamicCommandContext context, CancellationToken ct)
    {
        var actorId = $"dynamic:service:{serviceId}";
        var actor = await EnsureActorAsync<ScriptServiceDefinitionGAgent>(actorId, ct);
        var updatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (status == DynamicServiceStatus.Active)
        {
            await PublishActorEventAsync(
                actor,
                stackId: "_services",
                serviceName: serviceId,
                instanceSelector: "definition",
                new ScriptServiceActivatedEvent
                {
                    ServiceId = serviceId,
                    UpdatedAtUnixMs = updatedAtUnixMs,
                },
                ct);
        }
        else
        {
            await PublishActorEventAsync(
                actor,
                stackId: "_services",
                serviceName: serviceId,
                instanceSelector: "definition",
                new ScriptServiceDeactivatedEvent
                {
                    ServiceId = serviceId,
                    UpdatedAtUnixMs = updatedAtUnixMs,
                },
                ct);
        }

        var existing = await _serviceDefinitionStateStore.LoadAsync(actorId, ct)
            ?? throw new InvalidOperationException($"Service '{serviceId}' does not exist.");
        existing.Status = status.ToString();
        existing.UpdatedAtUnixMs = updatedAtUnixMs;
        await _serviceDefinitionStateStore.SaveAsync(actorId, existing, ct);

        var snapshot = ToServiceSnapshot(existing);
        if (status == DynamicServiceStatus.Active)
        {
            await EnsureServiceEnvelopeSubscriptionAsync(
                stackId: "_services",
                serviceName: serviceId,
                serviceMode: snapshot.ServiceMode,
                generation: updatedAtUnixMs,
                ct);
        }

        var etag = await AdvanceVersionAsync(actorId, context.IfMatch, ct);
        return new DynamicCommandResult(actorId, status == DynamicServiceStatus.Active ? "ACTIVE" : "INACTIVE", etag);
    }

    private async Task<ScriptServiceDefinitionState> GetServiceStateAsync(string serviceId, CancellationToken ct)
    {
        var actorId = $"dynamic:service:{serviceId}";
        var state = await _serviceDefinitionStateStore.LoadAsync(actorId, ct);
        if (state != null)
            return state;

        var snapshot = await _readStore.GetServiceDefinitionAsync(serviceId, ct)
            ?? throw new InvalidOperationException($"Service '{serviceId}' does not exist.");
        return new ScriptServiceDefinitionState
        {
            ServiceId = snapshot.ServiceId,
            Version = snapshot.Version,
            Status = snapshot.Status.ToString(),
            ScriptCode = snapshot.ScriptCode,
            EntrypointType = snapshot.EntrypointType,
            ServiceMode = snapshot.ServiceMode.ToString().ToLowerInvariant(),
            CapabilitiesHash = snapshot.CapabilitiesHash,
            UpdatedAtUnixMs = new DateTimeOffset(snapshot.UpdatedAtUtc).ToUnixTimeMilliseconds(),
            CustomState = snapshot.CustomState?.Clone(),
        };
    }

    private async Task<ComposeServiceSnapshot> GetComposeServiceAsync(string stackId, string serviceName, CancellationToken ct)
    {
        var services = await _readStore.GetComposeServicesAsync(stackId, ct);
        var service = services.FirstOrDefault(item => string.Equals(item.ServiceName, serviceName, StringComparison.Ordinal));
        return service ?? throw new InvalidOperationException($"Service '{serviceName}' on stack '{stackId}' does not exist.");
    }

    private static ServiceDefinitionSnapshot ToServiceSnapshot(ScriptServiceDefinitionState state)
    {
        var status = System.Enum.TryParse<DynamicServiceStatus>(state.Status, true, out var parsedStatus)
            ? parsedStatus
            : DynamicServiceStatus.Inactive;
        var mode = ParseServiceMode(state.ServiceMode);
        var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(state.UpdatedAtUnixMs == 0
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            : state.UpdatedAtUnixMs).UtcDateTime;
        return new ServiceDefinitionSnapshot(
            state.ServiceId,
            state.Version,
            status,
            state.ScriptCode,
            state.EntrypointType,
            mode,
            [.. state.PublicEndpoints],
            [.. state.EventSubscriptions],
            state.CapabilitiesHash,
            updatedAt,
            state.CustomState?.Clone());
    }

    private static DynamicServiceMode ParseServiceMode(string? value)
    {
        if (string.Equals(value, "daemon", StringComparison.OrdinalIgnoreCase))
            return DynamicServiceMode.Daemon;
        if (string.Equals(value, "event", StringComparison.OrdinalIgnoreCase))
            return DynamicServiceMode.Event;
        return DynamicServiceMode.Hybrid;
    }

    private async Task<IActor> EnsureActorAsync<TAgent>(string actorId, CancellationToken ct) where TAgent : IAgent
    {
        var existing = await _runtime.GetAsync(actorId);
        if (existing != null)
            return existing;

        return await _runtime.CreateAsync<TAgent>(actorId, ct);
    }

    private async Task EnsureLinkedAsync(string parentActorId, string childActorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(parentActorId) || string.IsNullOrWhiteSpace(childActorId))
            return;
        if (string.Equals(parentActorId, childActorId, StringComparison.Ordinal))
            return;

        await _runtime.LinkAsync(parentActorId, childActorId, ct);
    }

    private async Task UnlinkIfLinkedAsync(string childActorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(childActorId))
            return;
        await _runtime.UnlinkAsync(childActorId, ct);
    }

    private async Task<string> PublishImageInternalAsync(string imageName, string tag, string digest, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imageName))
            throw new InvalidOperationException("IMAGE_NOT_PUBLISHED");
        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidOperationException("IMAGE_TAG_CONFLICT");
        if (string.IsNullOrWhiteSpace(digest))
            throw new InvalidOperationException("IMAGE_NOT_PUBLISHED");

        var actorId = $"dynamic:image:{imageName}";
        var actor = await EnsureActorAsync<ScriptImageCatalogGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            stackId: "_images",
            serviceName: imageName,
            instanceSelector: tag,
            new ScriptImagePublishedEvent
            {
                ImageName = imageName,
                Tag = tag,
                Digest = digest,
            },
            ct);

        return actorId;
    }

    private async Task ValidateServiceModeAsync(string stackId, ComposeServiceSpec service, CancellationToken ct)
    {
        var decision = await _serviceModePolicyPort.ValidateAsync(new ServiceModePolicyRequest(
            stackId,
            service.ServiceName,
            service.ServiceMode,
            service.ReplicasDesired), ct);
        if (!decision.Allowed)
            throw new InvalidOperationException(decision.Reason ?? "SERVICE_MODE_CONFLICT");
    }

    private async Task<string> ResolveImageDigestAsync(string imageName, string imageRef, CancellationToken ct)
    {
        var resolved = await _imageReferenceResolver.ResolveAsync(imageName, imageRef, ct);
        if (!resolved.Found || string.IsNullOrWhiteSpace(resolved.Digest))
            throw new InvalidOperationException(resolved.ErrorCode ?? "IMAGE_NOT_PUBLISHED");
        if (!resolved.Digest.StartsWith("sha256:", StringComparison.Ordinal))
            throw new InvalidOperationException("IMAGE_NOT_PUBLISHED");
        return resolved.Digest;
    }

    private async Task EnsureServiceEnvelopeSubscriptionAsync(
        string stackId,
        string serviceName,
        DynamicServiceMode serviceMode,
        long generation,
        CancellationToken ct)
    {
        if (serviceMode == DynamicServiceMode.Daemon)
            return;

        var leaseId = $"{stackId}:{serviceName}:gen:{generation}";
        var result = await _eventEnvelopeSubscriberPort.SubscribeAsync(
            new EnvelopeSubscribeRequest(
                stackId,
                serviceName,
                $"dynamic-runtime.subscriber.{serviceName}",
                leaseId,
                MaxInFlight: 64),
            ct);
        if (!result.Success)
            throw new InvalidOperationException(result.ErrorCode ?? "ENVELOPE_LEASE_INVALID");
    }

    private async Task TryDeployBuildResultToComposeAsync(
        string stackId,
        string serviceName,
        string imageDigest,
        CancellationToken ct)
    {
        var services = await _readStore.GetComposeServicesAsync(stackId, ct);
        var target = services.FirstOrDefault(item => string.Equals(item.ServiceName, serviceName, StringComparison.Ordinal));
        if (target == null)
            return;

        var nextGeneration = target.Generation + 1;
        var actorId = $"dynamic:stack:{stackId}:service:{serviceName}";
        var actor = await EnsureActorAsync<ScriptComposeServiceGAgent>(actorId, ct);
        await PublishActorEventAsync(
            actor,
            stackId,
            serviceName,
            "all",
            new ScriptComposeServiceRolledOutEvent
            {
                StackId = stackId,
                ServiceName = serviceName,
                ImageRef = imageDigest,
                Generation = nextGeneration,
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service_mode"] = target.ServiceMode.ToString().ToLowerInvariant(),
                ["replicas_desired"] = target.ReplicasDesired.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["replicas_ready"] = target.ReplicasReady.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            ct);

        await EnsureServiceEnvelopeSubscriptionAsync(stackId, serviceName, target.ServiceMode, nextGeneration, ct);
    }

    private async Task<DynamicCommandResult> PublishBuildResultInternalAsync(PublishBuildResultRequest request, string? ifMatch, CancellationToken ct)
    {
        var actorId = $"dynamic:build:{request.BuildJobId}";
        var actor = await EnsureActorAsync<ScriptBuildJobGAgent>(actorId, ct);
        var existing = await _readStore.GetBuildJobAsync(request.BuildJobId, ct)
            ?? throw new InvalidOperationException($"Build job '{request.BuildJobId}' does not exist.");

        await PublishActorEventAsync(
            actor,
            existing.StackId,
            existing.ServiceName,
            existing.BuildJobId,
            new ScriptBuildPublishedEvent
            {
                BuildJobId = request.BuildJobId,
                ResultImageDigest = request.ResultImageDigest,
            },
            ct);

        var imageName = $"{existing.StackId}/{existing.ServiceName}";
        await PublishImageInternalAsync(imageName, "latest", request.ResultImageDigest, ct);
        await PublishImageInternalAsync(imageName, request.BuildJobId, request.ResultImageDigest, ct);

        var etag = await AdvanceVersionAsync(actorId, ifMatch, ct);
        return new DynamicCommandResult(actorId, "PUBLISHED", etag);
    }

    private async Task<string> AdvanceVersionAsync(string aggregateId, string? expectedVersion, CancellationToken ct)
    {
        var check = await _concurrencyTokenPort.CheckAndAdvanceAsync(aggregateId, expectedVersion, ct);
        if (!check.Passed)
            throw new InvalidOperationException(check.ErrorCode ?? "VERSION_CONFLICT");
        return check.NextVersion;
    }

    private async Task PublishActorEventAsync(
        IActor actor,
        string stackId,
        string serviceName,
        string instanceSelector,
        Google.Protobuf.IMessage payload,
        CancellationToken ct)
        => await PublishActorEventAsync(actor, stackId, serviceName, instanceSelector, payload, metadata: null, ct);

    private async Task PublishActorEventAsync(
        IActor actor,
        string stackId,
        string serviceName,
        string instanceSelector,
        Google.Protobuf.IMessage payload,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct)
    {
        var envelope = CreateEnvelope(stackId, serviceName, instanceSelector, payload, metadata);
        var dedup = await _eventEnvelopeDedupPort.CheckAndRecordAsync(
            scope: actor.Id,
            dedupKey: envelope.Metadata["dedup_key"],
            ttl: EnvelopeDedupTtl,
            ct);
        if (!dedup.Allowed)
            throw new InvalidOperationException(dedup.ErrorCode ?? "ENVELOPE_DUPLICATE");

        await actor.HandleEventAsync(envelope, ct);
        await _eventProjector.ProjectAsync(envelope, ct);
        await _eventEnvelopePublisherPort.PublishAsync(
            new ScriptEventEnvelope(envelope.Id, stackId, serviceName, instanceSelector, envelope),
            ct);
    }

    private static EventEnvelope CreateEnvelope(
        string stackId,
        string serviceName,
        string instanceSelector,
        Google.Protobuf.IMessage payload,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var eventId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var traceId = Guid.NewGuid().ToString("N");
        var correlationId = Guid.NewGuid().ToString("N");
        var packedPayload = Any.Pack(payload);
        var envelope = new EventEnvelope
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTime(now),
            Payload = packedPayload,
            PublisherId = PublisherId,
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
            Metadata =
            {
                ["trace_id"] = traceId,
                ["correlation_id"] = correlationId,
                ["causation_id"] = eventId,
                ["dedup_key"] = $"{packedPayload.TypeUrl}:{eventId}",
                ["type_url"] = packedPayload.TypeUrl,
                ["stack_id"] = stackId,
                ["service_name"] = serviceName,
                ["instance_selector"] = instanceSelector,
                ["delivery_kind"] = RuntimeDomainEventKind,
                ["occurred_at"] = now.ToString("O"),
            },
        };

        if (metadata == null || metadata.Count == 0)
            return envelope;

        foreach (var pair in metadata)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;
            envelope.Metadata[pair.Key] = pair.Value ?? string.Empty;
        }

        return envelope;
    }

    private async Task<DynamicCommandResult?> EnsureIdempotentAsync(string scope, string key, object requestPayload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Idempotency key is required.");

        var requestHash = ComputeHashBytes(requestPayload);
        var acquire = await _idempotencyPort.AcquireAsync(scope, key, requestHash, ct);
        if (acquire.Acquired)
            return null;

        if (string.Equals(acquire.ErrorCode, "IDEMPOTENCY_PAYLOAD_MISMATCH", StringComparison.Ordinal))
            throw new InvalidOperationException("IDEMPOTENCY_PAYLOAD_MISMATCH");

        if (acquire.IsReplay)
        {
            var responsePayload = await _idempotencyPort.GetCommittedResponseAsync(scope, key, ct);
            if (!string.IsNullOrWhiteSpace(responsePayload))
            {
                var replay = System.Text.Json.JsonSerializer.Deserialize<DynamicCommandResult>(responsePayload);
                if (replay != null)
                    return replay;
            }

            throw new InvalidOperationException($"Duplicate command detected for scope '{scope}'.");
        }

        throw new InvalidOperationException(acquire.ErrorCode ?? "IDEMPOTENCY_CONFLICT");
    }

    private async Task CommitIdempotencyAsync(string scope, string key, DynamicCommandResult result, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        var payload = System.Text.Json.JsonSerializer.Serialize(result);
        await _idempotencyPort.CommitAsync(scope, key, ComputeHashBytes(result), payload, ct);
    }

    private static void RequireIfMatch(string? ifMatch)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
            throw new InvalidOperationException("VERSION_CONFLICT");
    }

    private static void ValidateServiceRequest(string serviceId, string version, string scriptCode, string entrypointType)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
            throw new InvalidOperationException("SERVICE_DEFINITION_INVALID: service_id is required.");
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("SERVICE_DEFINITION_INVALID: version is required.");
        if (string.IsNullOrWhiteSpace(scriptCode))
            throw new InvalidOperationException("SERVICE_DEFINITION_INVALID: script_code is required.");
        if (string.IsNullOrWhiteSpace(entrypointType))
            throw new InvalidOperationException("SERVICE_DEFINITION_INVALID: entrypoint_type is required.");
    }

    private static IReadOnlyDictionary<string, string> BuildCustomStateMetadata(Any? customState)
    {
        if (customState == null || string.IsNullOrWhiteSpace(customState.TypeUrl))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["custom_state_type_url"] = customState.TypeUrl,
            ["custom_state_value_b64"] = Convert.ToBase64String(customState.Value.Span),
        };
    }

    private static bool IsRunTerminal(string status) =>
        string.Equals(status, "Succeeded", StringComparison.Ordinal) ||
        string.Equals(status, "Failed", StringComparison.Ordinal) ||
        string.Equals(status, "Canceled", StringComparison.Ordinal) ||
        string.Equals(status, "TimedOut", StringComparison.Ordinal);

    private async Task<(DynamicScriptExecutionResult Result, bool TimedOut, string TimeoutReason)> ExecuteScriptAttemptAsync(
        ScriptServiceDefinitionState service,
        EventEnvelope envelope,
        int timeoutMs,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            var result = await _scriptExecutionService.ExecuteAsync(
                new DynamicScriptExecutionRequest(service.ScriptCode, envelope, service.EntrypointType, service.CustomState?.Clone()),
                timeoutCts.Token);
            if (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                var timeoutReason = $"RUN_ATTEMPT_TIMEOUT:{timeoutMs}ms";
                return (new DynamicScriptExecutionResult(false, string.Empty, Error: timeoutReason), TimedOut: true, TimeoutReason: timeoutReason);
            }
            return (result, TimedOut: false, TimeoutReason: string.Empty);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            var reason = $"RUN_ATTEMPT_TIMEOUT:{timeoutMs}ms";
            return (new DynamicScriptExecutionResult(false, string.Empty, Error: reason), TimedOut: true, TimeoutReason: reason);
        }
    }

    private static int NormalizeRunTimeoutMs(int? timeoutMs)
    {
        if (!timeoutMs.HasValue || timeoutMs.Value <= 0)
            return DefaultRunTimeoutMs;
        return Math.Clamp(timeoutMs.Value, 100, 300_000);
    }

    private static int ComputeRunRetryBackoffMs(int baseBackoffMs, int attempt)
    {
        var normalizedBase = Math.Clamp(baseBackoffMs <= 0 ? DefaultRetryBackoffMs : baseBackoffMs, 10, 60_000);
        var exponent = Math.Clamp(attempt - 1, 0, 8);
        var scaled = normalizedBase * (1 << exponent);
        return Math.Min(scaled, 60_000);
    }

    private static EventEnvelope BuildScriptExecutionEnvelope(
        EventEnvelope envelope,
        string runId,
        string containerId,
        string serviceId,
        string stackId,
        string serviceName,
        int attempt,
        int maxAttempts)
    {
        var next = envelope?.Clone() ?? new EventEnvelope();
        if (next.Payload == null)
            next.Payload = Any.Pack(new StringValue());
        if (next.Timestamp == null)
            next.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        if (string.IsNullOrWhiteSpace(next.PublisherId))
            next.PublisherId = PublisherId;
        if (string.IsNullOrWhiteSpace(next.Id))
            next.Id = Guid.NewGuid().ToString("N");

        var now = DateTime.UtcNow;
        var correlationId = string.IsNullOrWhiteSpace(next.CorrelationId) ? runId : next.CorrelationId;
        var causationId = next.Metadata.TryGetValue("causation_id", out var existingCausation)
            ? existingCausation
            : Guid.NewGuid().ToString("N");

        next.CorrelationId = correlationId;
        next.Metadata["trace_id"] = next.Metadata.TryGetValue("trace_id", out var traceId) && !string.IsNullOrWhiteSpace(traceId)
            ? traceId
            : Guid.NewGuid().ToString("N");
        next.Metadata["correlation_id"] = correlationId;
        next.Metadata["causation_id"] = causationId;
        next.Metadata["dedup_key"] = next.Metadata.TryGetValue("dedup_key", out var dedup) && !string.IsNullOrWhiteSpace(dedup)
            ? dedup
            : $"{next.Payload.TypeUrl}:{runId}";
        next.Metadata["type_url"] = next.Payload.TypeUrl;
        next.Metadata["stack_id"] = stackId;
        next.Metadata["service_name"] = serviceName;
        next.Metadata["instance_selector"] = containerId;
        next.Metadata["run_id"] = runId;
        next.Metadata["container_id"] = containerId;
        next.Metadata["service_id"] = serviceId;
        next.Metadata["run_attempt"] = attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        next.Metadata["run_max_attempts"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture);
        next.Metadata["occurred_at"] = now.ToString("O");

        return next;
    }

    private async Task PublishScriptOutputEnvelopesAsync(
        string stackId,
        string serviceName,
        string instanceSelector,
        IReadOnlyList<EventEnvelope>? envelopes,
        CancellationToken ct)
    {
        if (envelopes == null || envelopes.Count == 0)
            return;

        foreach (var envelope in envelopes)
        {
            ct.ThrowIfCancellationRequested();
            await _eventEnvelopePublisherPort.PublishAsync(
                NormalizeScriptOutputEnvelope(envelope, stackId, serviceName, instanceSelector),
                ct);
        }
    }

    private static ScriptEventEnvelope NormalizeScriptOutputEnvelope(
        EventEnvelope envelope,
        string stackId,
        string serviceName,
        string instanceSelector)
    {
        var next = envelope?.Clone() ?? new EventEnvelope();
        if (next.Payload == null)
            next.Payload = Any.Pack(new StringValue());
        if (next.Timestamp == null)
            next.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        if (string.IsNullOrWhiteSpace(next.PublisherId))
            next.PublisherId = PublisherId;
        if (string.IsNullOrWhiteSpace(next.Id))
            next.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(next.CorrelationId))
            next.CorrelationId = Guid.NewGuid().ToString("N");

        var now = DateTime.UtcNow;
        next.Metadata["trace_id"] = next.Metadata.TryGetValue("trace_id", out var traceId) && !string.IsNullOrWhiteSpace(traceId)
            ? traceId
            : Guid.NewGuid().ToString("N");
        next.Metadata["correlation_id"] = next.CorrelationId;
        next.Metadata["causation_id"] = next.Metadata.TryGetValue("causation_id", out var causationId) && !string.IsNullOrWhiteSpace(causationId)
            ? causationId
            : next.Id;
        next.Metadata["dedup_key"] = next.Metadata.TryGetValue("dedup_key", out var dedupKey) && !string.IsNullOrWhiteSpace(dedupKey)
            ? dedupKey
            : $"{next.Payload.TypeUrl}:{next.Id}";
        next.Metadata["type_url"] = next.Payload.TypeUrl;
        next.Metadata["stack_id"] = stackId;
        next.Metadata["service_name"] = serviceName;
        next.Metadata["instance_selector"] = instanceSelector;
        next.Metadata["delivery_kind"] = ScriptOutputEventKind;
        next.Metadata["occurred_at"] = now.ToString("O");

        return new ScriptEventEnvelope(next.Id, stackId, serviceName, instanceSelector, next);
    }

    private async Task DispatchStackEnvelopesAsync(string stackId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stackId))
            return;

        for (var cycle = 0; cycle < MaxEnvelopeDispatchCycles; cycle++)
        {
            ct.ThrowIfCancellationRequested();
            var madeProgress = false;
            var leases = await _eventEnvelopeDeliveryPort.ListLeasesAsync(stackId, ct);
            if (leases.Count == 0)
                break;

            foreach (var lease in leases)
            {
                var pullCount = Math.Clamp(lease.MaxInFlight, 1, 32);
                var deliveries = await _eventEnvelopeDeliveryPort.PullAsync(lease.LeaseId, pullCount, ct);
                if (deliveries.Count == 0)
                    continue;
                madeProgress = true;

                foreach (var delivery in deliveries)
                {
                    ct.ThrowIfCancellationRequested();
                    await DispatchEnvelopeDeliveryAsync(lease, delivery, ct);
                }
            }

            if (!madeProgress)
                break;
        }
    }

    private async Task DispatchEnvelopeDeliveryAsync(
        EnvelopeSubscribeRequest lease,
        EnvelopeDeliverySnapshot delivery,
        CancellationToken ct)
    {
        var input = delivery.Envelope.Envelope;
        var currentHop = ReadMetadataInt(input, "delivery_hop");
        if (currentHop >= MaxDeliveryHop)
        {
            await _eventEnvelopeDeliveryPort.AckAsync(lease.LeaseId, delivery.DeliveryId, ct);
            return;
        }

        var target = await SelectDeliveryTargetContainerAsync(lease.StackId, lease.ServiceName, delivery.Envelope.EnvelopeId, ct);
        if (target == null)
        {
            await RetryDeliveryAsync(lease.LeaseId, delivery, "CONTAINER_UNAVAILABLE", ct);
            return;
        }

        var forwardEnvelope = input.Clone();
        forwardEnvelope.Metadata["delivery_hop"] = (currentHop + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var runId = BuildDeliveryRunId(lease.LeaseId, delivery.Envelope.EnvelopeId, delivery.Attempt);

        try
        {
            var result = await ExecuteContainerInternalAsync(
                new ExecuteContainerRequest(
                    target.ContainerId,
                    target.ServiceId,
                    forwardEnvelope,
                    RunId: runId,
                    TimeoutMs: DefaultRunTimeoutMs,
                    MaxRetries: 0,
                    RetryBackoffMs: DefaultRetryBackoffMs),
                new DynamicCommandContext($"delivery:{lease.LeaseId}:{delivery.Envelope.EnvelopeId}:{delivery.Attempt}"),
                dispatchFollowUp: false,
                useIdempotency: false,
                ct);

            if (string.Equals(result.Status, "SUCCEEDED", StringComparison.Ordinal))
            {
                await _eventEnvelopeDeliveryPort.AckAsync(lease.LeaseId, delivery.DeliveryId, ct);
                return;
            }

            await RetryDeliveryAsync(lease.LeaseId, delivery, $"RUN_{result.Status}", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RetryDeliveryAsync(lease.LeaseId, delivery, $"RUN_DISPATCH_EXCEPTION:{ex.GetType().Name}", ct);
        }
    }

    private async Task<ContainerSnapshot?> SelectDeliveryTargetContainerAsync(
        string stackId,
        string serviceName,
        string envelopeId,
        CancellationToken ct)
    {
        var candidates = await _readStore.GetServiceContainersAsync(stackId, serviceName, ct);
        if (candidates.Count == 0)
            return null;

        var running = candidates
            .Where(item => string.Equals(item.Status, "Running", StringComparison.Ordinal))
            .OrderBy(item => item.ContainerId, StringComparer.Ordinal)
            .ToArray();
        if (running.Length == 0)
            return null;

        var active = new List<ContainerSnapshot>(running.Length);
        foreach (var container in running)
        {
            var service = await _readStore.GetServiceDefinitionAsync(container.ServiceId, ct);
            if (service != null && service.Status == DynamicServiceStatus.Active)
                active.Add(container);
        }

        if (active.Count == 0)
            return null;

        var index = ComputeStableIndex($"{serviceName}:{envelopeId}", active.Count);
        return active[index];
    }

    private async Task RetryDeliveryAsync(string leaseId, EnvelopeDeliverySnapshot delivery, string reason, CancellationToken ct)
    {
        var backoffMs = ComputeDeliveryRetryBackoffMs(delivery.Attempt);
        await _eventEnvelopeDeliveryPort.RetryAsync(
            leaseId,
            delivery.DeliveryId,
            TimeSpan.FromMilliseconds(backoffMs),
            reason,
            ct);
    }

    private static int ComputeDeliveryRetryBackoffMs(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 8);
        var scaled = DefaultRetryBackoffMs * (1 << exponent);
        return Math.Clamp(scaled, DefaultRetryBackoffMs, 30_000);
    }

    private static string BuildDeliveryRunId(string leaseId, string envelopeId, int attempt) =>
        $"delivery-{BuildStableHash($"{leaseId}:{envelopeId}:{attempt}")}";

    private static int ComputeStableIndex(string seed, int modulo)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed ?? string.Empty));
        var value = BitConverter.ToUInt32(bytes, 0);
        return (int)(value % (uint)modulo);
    }

    private static string BuildStableHash(string seed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static int ReadMetadataInt(EventEnvelope envelope, string key)
    {
        if (envelope == null || !envelope.Metadata.TryGetValue(key, out var raw))
            return 0;
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private async Task ApplyScriptSideEffectsAsync(
        IActor runActor,
        string runId,
        string serviceId,
        ScriptServiceDefinitionState serviceState,
        string stackId,
        string serviceName,
        string instanceSelector,
        DynamicScriptExecutionResult scriptResult,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var serviceActorId = $"dynamic:service:{serviceId}";
        var sideEffects = await _scriptSideEffectPlanner.BuildAsync(runId, serviceId, serviceState, scriptResult, now, ct);

        // Event-first sequencing: no state/read-model mutation without persisted run events.
        foreach (var evt in sideEffects.Events)
        {
            ct.ThrowIfCancellationRequested();
            await PublishActorEventAsync(runActor, stackId, serviceName, instanceSelector, evt, ct);
        }

        if (sideEffects.CustomState != null)
        {
            serviceState.CustomState = sideEffects.CustomState.Clone();
            serviceState.UpdatedAtUnixMs = sideEffects.CustomStateUpdatedAtUnixMs;
            await _serviceDefinitionStateStore.SaveAsync(serviceActorId, serviceState, ct);
        }
    }

    private static string BuildDigest(string imageName, string sourceBundleDigest)
    {
        var normalized = $"{imageName}:{sourceBundleDigest}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"sha256:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static byte[] ComputeHashBytes(object payload)
    {
        var serialized = System.Text.Json.JsonSerializer.Serialize(payload);
        return SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
    }
}
