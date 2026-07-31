using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Infrastructure.Runs;

/// <summary>
/// Infrastructure adapter for workflow definition actor lifecycle and run actor creation.
/// </summary>
// Refactor (iter51/issue-900-workflow-actor-port-runtime-object):
//   Old pattern: Application layer ports returned and passed IActor runtime objects directly; one port owned actor lifecycle + topology + dispatch + parse.
//   New principle: Application layer exchanges typed actor-id receipts (ActorId, DefinitionActorId, CreatedActorIds); runtime IActor objects stay infrastructure-only; lifecycle/provisioning, dispatch, and YAML parsing are split into narrow ports.
internal sealed class WorkflowRunActorPort :
    IWorkflowDefinitionProvisioningPort,
    IWorkflowRunProvisioningPort,
    IWorkflowRunIdentityProvisioningPort,
    IWorkflowRunIdentityExecutionPort
{
    private const string WorkflowRunActorPortPublisherId = "workflow.run.actor.port";
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IWorkflowActorBindingReader _bindingReader;
    private readonly WorkflowDefinitionParser _definitionParser;
    private readonly ILogger<WorkflowRunActorPort> _logger;

    public WorkflowRunActorPort(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IWorkflowActorBindingReader bindingReader,
        IEnumerable<IWorkflowModulePack> modulePacks,
        IAgentKindRegistry? agentKindRegistry = null,
        ILogger<WorkflowRunActorPort>? logger = null)
    {
        _runtime = runtime;
        _dispatchPort = dispatchPort;
        _bindingReader = bindingReader;
        _logger = logger ?? NullLogger<WorkflowRunActorPort>.Instance;
        _definitionParser = new WorkflowDefinitionParser(modulePacks, agentKindRegistry);
    }

    public async Task<WorkflowDefinitionProvisioningReceipt> EnsureDefinitionAsync(
        WorkflowDefinitionBinding definition,
        string? preferredActorId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var requestedDefinitionActorId = NormalizeActorId(preferredActorId)
                                         ?? NormalizeActorId(definition.DefinitionActorId);
        var definitionResolution = requestedDefinitionActorId == null
            ? await CreateBoundDefinitionActorAsync(definition, preferredActorId: null, ct)
            : await EnsureDefinitionActorAsync(definition, requestedDefinitionActorId, ct);

        return new WorkflowDefinitionProvisioningReceipt(
            definitionResolution.ActorId,
            definitionResolution.CreatedNow);
    }

    public async Task<WorkflowRunCreationReceipt> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.WorkflowYaml) ||
            string.IsNullOrWhiteSpace(definition.WorkflowName))
        {
            throw new InvalidOperationException(
                "Workflow run creation requires a valid workflow definition binding.");
        }

        DefinitionActorResolutionResult definitionResolution = default;
        IActor? runActor = null;
        var createdActorIds = new List<string>(2);
        try
        {
            definitionResolution = await ResolveDefinitionActorForRunAsync(definition, ct);
            if (definitionResolution.CreatedNow && !string.IsNullOrWhiteSpace(definitionResolution.ActorId))
                createdActorIds.Add(definitionResolution.ActorId);

            runActor = await _runtime.CreateAsync<WorkflowRunGAgent>(
                BuildRunActorId(definitionResolution.ActorId),
                ct: ct);
            createdActorIds.Add(runActor.Id);
            if (!string.IsNullOrWhiteSpace(definitionResolution.ActorId))
                await _runtime.LinkAsync(definitionResolution.ActorId, runActor.Id, ct);

            // Refactor (iter18/cluster-006):
            //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
            //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
            await _dispatchPort.DispatchAsync(
                runActor.Id,
                CreateWorkflowRunBindEnvelope(
                    definitionResolution.ActorId,
                    runActor.Id,
                    definition.WorkflowYaml,
                    definition.WorkflowName,
                    definition.InlineWorkflowYamls,
                    definition.ScopeId,
                    definition.RunOrigin,
                    definition.ScheduleId,
                    definitionResolution.CapabilityAdmissionPlan),
                ct);

            return new WorkflowRunCreationReceipt(
                runActor.Id,
                definitionResolution.ActorId,
                createdActorIds);
        }
        catch
        {
            await TryDestroyActorsAsync(createdActorIds);
            throw;
        }
    }

    public Task<WorkflowRunCreationReceipt> EnsureRunAsync(
        WorkflowDefinitionBinding definition,
        string requestedRunId,
        CancellationToken ct = default) =>
        EnsureRunCoreAsync(
            definition,
            requestedRunId,
            executionRequest: null,
            commandId: null,
            correlationId: null,
            ct);

    public Task<WorkflowRunCreationReceipt> EnsureRunAndDispatchAsync(
        WorkflowDefinitionBinding definition,
        string requestedRunId,
        WorkflowChatRequestEvent executionRequest,
        string commandId,
        string correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(executionRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return EnsureRunCoreAsync(
            definition,
            requestedRunId,
            executionRequest,
            commandId.Trim(),
            correlationId.Trim(),
            ct);
    }

    private async Task<WorkflowRunCreationReceipt> EnsureRunCoreAsync(
        WorkflowDefinitionBinding definition,
        string requestedRunId,
        WorkflowChatRequestEvent? executionRequest,
        string? commandId,
        string? correlationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var normalizedRunId = NormalizeActorId(requestedRunId)
            ?? throw new ArgumentException("Requested Run id is required.", nameof(requestedRunId));
        if (string.IsNullOrWhiteSpace(definition.WorkflowYaml) ||
            string.IsNullOrWhiteSpace(definition.WorkflowName))
        {
            throw new InvalidOperationException(
                "Workflow Run identity provisioning requires a valid workflow definition binding.");
        }

        DefinitionActorResolutionResult definitionResolution = default;
        var createdActorIds = new List<string>(1);
        try
        {
            definitionResolution = await ResolveDefinitionActorForRunAsync(definition, ct);
            if (definitionResolution.CreatedNow && !string.IsNullOrWhiteSpace(definitionResolution.ActorId))
                createdActorIds.Add(definitionResolution.ActorId);

            var runActor = await _runtime.CreateAsync<WorkflowRunGAgent>(normalizedRunId, ct: ct);

            var admission = await _dispatchPort.DispatchAsync(
                runActor.Id,
                CreateWorkflowRunEnsureEnvelope(
                    definitionResolution.ActorId,
                    runActor.Id,
                    definition.WorkflowYaml,
                    definition.WorkflowName,
                    definition.InlineWorkflowYamls,
                    definition.ScopeId,
                    definition.RunOrigin,
                    definition.ScheduleId,
                    definitionResolution.CapabilityAdmissionPlan,
                    executionRequest,
                    commandId,
                    correlationId),
                ct);
            if (!admission.Accepted)
            {
                throw new InvalidOperationException(
                    $"Workflow Run ensure dispatch was not accepted for actor '{runActor.Id}'.");
            }

            return new WorkflowRunCreationReceipt(
                runActor.Id,
                definitionResolution.ActorId,
                createdActorIds);
        }
        catch
        {
            // The stable Run actor is intentionally not destroyed here: a
            // concurrent or previously accepted caller may already own it.
            await TryDestroyActorsAsync(createdActorIds);
            throw;
        }
    }

    public Task DestroyAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("Actor id is required.", nameof(actorId));

        return _runtime.DestroyAsync(actorId, ct);
    }

    public async Task BindWorkflowDefinitionAsync(
        string actorId,
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        string? scopeId = null,
        string? sourceKind = null,
        WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("Actor id is required.", nameof(actorId));

        // Refactor (iter18/cluster-006):
        //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
        //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
        var envelope = CreateWorkflowDefinitionBindEnvelope(
            workflowYaml,
            workflowName,
            inlineWorkflowYamls,
            scopeId,
            sourceKind,
            capabilityAdmissionPlan);
        await _dispatchPort.DispatchAsync(actorId, envelope, ct);
    }

    public Task MarkStoppedAsync(
        string actorId,
        string runId,
        string reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("Actor id is required.", nameof(actorId));

        return _dispatchPort.DispatchAsync(
            actorId,
            CreateWorkflowRunStoppedEnvelope(actorId, runId, reason),
            ct);
    }

    public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
        string workflowYaml,
        CancellationToken ct = default) =>
        _definitionParser.ParseWorkflowYamlAsync(workflowYaml, ct);

    private async Task<DefinitionActorResolutionResult> ResolveDefinitionActorForRunAsync(
        WorkflowDefinitionBinding definition,
        CancellationToken ct)
    {
        var requestedDefinitionActorId = NormalizeActorId(definition.DefinitionActorId);
        if (requestedDefinitionActorId == null)
            return await CreateBoundDefinitionActorAsync(definition, preferredActorId: null, ct);

        var existingActor = await _runtime.GetAsync(requestedDefinitionActorId);
        if (existingActor == null)
        {
            throw new InvalidOperationException(
                $"Workflow definition actor '{requestedDefinitionActorId}' does not exist. Provision the Definition before creating a Run.");
        }

        var binding = await _bindingReader.GetAsync(existingActor.Id, ct);
        if (binding == null)
        {
            throw new InvalidOperationException(
                $"Workflow definition actor '{existingActor.Id}' does not have an available Definition binding read model.");
        }

        if (binding.ActorKind != WorkflowActorKind.Definition)
        {
            throw new InvalidOperationException(
                $"Actor '{existingActor.Id}' is not a workflow definition actor and cannot be reused as a definition source.");
        }

        EnsureScopeCompatibility(existingActor.Id, binding, definition);
        EnsureWorkflowNameCompatibility(existingActor.Id, binding, definition);
        if (!binding.HasDefinitionPayload)
        {
            throw new InvalidOperationException(
                $"Workflow definition actor '{existingActor.Id}' does not have a materialized definition payload.");
        }

        if (!IsSameDefinitionPayload(binding, definition))
        {
            throw new InvalidOperationException(
                $"Workflow definition actor '{existingActor.Id}' payload does not match the requested Run definition.");
        }

        return new DefinitionActorResolutionResult(
            existingActor.Id,
            CreatedNow: false,
            binding.CapabilityAdmissionPlan?.Clone());
    }

    private async Task<DefinitionActorResolutionResult> EnsureDefinitionActorAsync(
        WorkflowDefinitionBinding definition,
        string? requestedDefinitionActorId,
        CancellationToken ct)
    {
        if (requestedDefinitionActorId != null)
        {
            var existingActor = await _runtime.GetAsync(requestedDefinitionActorId);
            if (existingActor == null)
                return await CreateBoundDefinitionActorAsync(definition, requestedDefinitionActorId, ct);

            var binding = await _bindingReader.GetAsync(existingActor.Id, ct);
            if (binding == null || binding.ActorKind != WorkflowActorKind.Definition)
            {
                // Explicit Definition provisioning may repair a missing or Run-kind binding document
                // from its authoritative payload. Run provisioning uses ResolveDefinitionActorForRunAsync
                // and never enters this write-capable repair path.
                var isClobberedDefinitionSlot =
                    binding == null || binding.ActorKind == WorkflowActorKind.Run;
                if (isClobberedDefinitionSlot && HasDefinitionPayload(definition))
                {
                    await BindWorkflowDefinitionAsync(
                        existingActor.Id,
                        definition.WorkflowYaml,
                        definition.WorkflowName,
                        definition.InlineWorkflowYamls,
                        definition.ScopeId,
                        definition.SourceKind,
                        definition.CapabilityAdmissionPlan,
                        ct);
                    return new DefinitionActorResolutionResult(
                        existingActor.Id,
                        CreatedNow: false,
                        definition.CapabilityAdmissionPlan?.Clone());
                }

                throw new InvalidOperationException(
                    $"Actor '{existingActor.Id}' is not a workflow definition actor and cannot be reused as a definition source.");
            }

            EnsureScopeCompatibility(existingActor.Id, binding, definition);
            EnsureWorkflowNameCompatibility(existingActor.Id, binding, definition);

            if (!binding.HasDefinitionPayload || !IsSameDefinition(binding, definition))
            {
                await BindWorkflowDefinitionAsync(
                    existingActor.Id,
                    definition.WorkflowYaml,
                    definition.WorkflowName,
                    definition.InlineWorkflowYamls,
                    definition.ScopeId,
                    definition.SourceKind,
                    definition.CapabilityAdmissionPlan,
                    ct);
            }

            return new DefinitionActorResolutionResult(
                existingActor.Id,
                CreatedNow: false,
                definition.CapabilityAdmissionPlan?.Clone() ?? binding.CapabilityAdmissionPlan?.Clone());
        }

        return await CreateBoundDefinitionActorAsync(definition, preferredActorId: null, ct);
    }

    private async Task<DefinitionActorResolutionResult> CreateBoundDefinitionActorAsync(
        WorkflowDefinitionBinding definition,
        string? preferredActorId,
        CancellationToken ct)
    {
        IActor definitionActor;
        try
        {
            definitionActor = await _runtime.CreateAsync<WorkflowGAgent>(preferredActorId, ct: ct);
        }
        catch (InvalidOperationException) when (!string.IsNullOrWhiteSpace(preferredActorId))
        {
            var racedActor = await TryResolveRacedDefinitionActorAsync(definition, preferredActorId!, ct);
            if (racedActor != null)
            {
                return new DefinitionActorResolutionResult(
                    racedActor.Id,
                    CreatedNow: false,
                    definition.CapabilityAdmissionPlan?.Clone());
            }

            throw;
        }

        try
        {
            await BindWorkflowDefinitionAsync(
                definitionActor.Id,
                definition.WorkflowYaml,
                definition.WorkflowName,
                definition.InlineWorkflowYamls,
                definition.ScopeId,
                definition.SourceKind,
                definition.CapabilityAdmissionPlan,
                ct);
            return new DefinitionActorResolutionResult(
                definitionActor.Id,
                CreatedNow: true,
                definition.CapabilityAdmissionPlan?.Clone());
        }
        catch
        {
            await TryDestroyActorsAsync([definitionActor.Id]);
            throw;
        }
    }

    private async Task<IActor?> TryResolveRacedDefinitionActorAsync(
        WorkflowDefinitionBinding definition,
        string preferredActorId,
        CancellationToken ct)
    {
        var existingActor = await _runtime.GetAsync(preferredActorId);
        if (existingActor == null)
            return null;

        var binding = await _bindingReader.GetAsync(existingActor.Id, ct);
        if (binding == null || binding.ActorKind != WorkflowActorKind.Definition)
            return null;

        EnsureWorkflowNameCompatibility(existingActor.Id, binding, definition);
        EnsureScopeCompatibility(existingActor.Id, binding, definition);
        if (!binding.HasDefinitionPayload || !IsSameDefinition(binding, definition))
        {
            await BindWorkflowDefinitionAsync(
                existingActor.Id,
                definition.WorkflowYaml,
                definition.WorkflowName,
                definition.InlineWorkflowYamls,
                definition.ScopeId,
                definition.SourceKind,
                definition.CapabilityAdmissionPlan,
                ct);
        }

        return existingActor;
    }

    private async Task TryDestroyActorsAsync(IReadOnlyList<string> actorIds)
    {
        foreach (var actorId in actorIds
                     .Where(static x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal)
                     .Reverse())
        {
            try
            {
                await _runtime.DestroyAsync(actorId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to roll back workflow actor {ActorId}.", actorId);
                // Best effort rollback path.
            }
        }
    }

    private static string? NormalizeActorId(string? actorId)
    {
        var normalized = actorId?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized;
    }

    private static bool HasDefinitionPayload(WorkflowDefinitionBinding definition) =>
        !string.IsNullOrWhiteSpace(definition.WorkflowYaml) ||
        definition.InlineWorkflowYamls.Count > 0;

    private static bool IsSameDefinition(
        WorkflowActorBinding binding,
        WorkflowDefinitionBinding definition)
    {
        if (!IsSameDefinitionPayload(binding, definition))
            return false;

        return string.Equals(
            binding.CapabilityAdmissionPlan?.AdmissionDigest ?? string.Empty,
            definition.CapabilityAdmissionPlan?.AdmissionDigest ?? string.Empty,
            StringComparison.Ordinal);
    }

    private static bool IsSameDefinitionPayload(
        WorkflowActorBinding binding,
        WorkflowDefinitionBinding definition)
    {
        if (!string.Equals(
                binding.WorkflowName?.Trim(),
                definition.WorkflowName?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(
                binding.WorkflowYaml ?? string.Empty,
                definition.WorkflowYaml ?? string.Empty,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (binding.InlineWorkflowYamls.Count != definition.InlineWorkflowYamls.Count)
            return false;

        foreach (var (key, value) in definition.InlineWorkflowYamls)
        {
            if (!binding.InlineWorkflowYamls.TryGetValue(key, out var boundValue) ||
                !string.Equals(boundValue, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureWorkflowNameCompatibility(
        string actorId,
        WorkflowActorBinding binding,
        WorkflowDefinitionBinding definition)
    {
        var boundWorkflowName = binding.WorkflowName?.Trim() ?? string.Empty;
        var requestedWorkflowName = definition.WorkflowName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(boundWorkflowName) ||
            string.IsNullOrWhiteSpace(requestedWorkflowName) ||
            string.Equals(boundWorkflowName, requestedWorkflowName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Workflow definition actor '{actorId}' is already bound to workflow '{binding.WorkflowName}' and cannot switch to '{definition.WorkflowName}'.");
    }

    private static void EnsureScopeCompatibility(
        string actorId,
        WorkflowActorBinding binding,
        WorkflowDefinitionBinding definition)
    {
        var boundScopeId = binding.ScopeId?.Trim() ?? string.Empty;
        var requestedScopeId = definition.ScopeId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(boundScopeId) ||
            string.IsNullOrWhiteSpace(requestedScopeId) ||
            string.Equals(boundScopeId, requestedScopeId, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Workflow definition actor '{actorId}' is already bound to scope '{binding.ScopeId}' and cannot switch to '{definition.ScopeId}'.");
    }

    private static EventEnvelope CreateWorkflowDefinitionBindEnvelope(
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        string? scopeId,
        string? sourceKind,
        WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(BuildBindWorkflowDefinitionEvent(
                workflowYaml,
                workflowName,
                inlineWorkflowYamls,
                scopeId,
                sourceKind,
                capabilityAdmissionPlan)),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(WorkflowRunActorPortPublisherId, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };

    private static EventEnvelope CreateWorkflowRunBindEnvelope(
        string definitionActorId,
        string runId,
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string> inlineWorkflowYamls,
        string? scopeId,
        string? runOrigin,
        string? scheduleId,
        WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(BuildBindWorkflowRunDefinitionEvent(
                definitionActorId,
                runId,
                workflowYaml,
                workflowName,
                inlineWorkflowYamls,
                scopeId,
                runOrigin,
                scheduleId,
                capabilityAdmissionPlan)),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(WorkflowRunActorPortPublisherId, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };

    private static EventEnvelope CreateWorkflowRunEnsureEnvelope(
        string definitionActorId,
        string runId,
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string> inlineWorkflowYamls,
        string? scopeId,
        string? runOrigin,
        string? scheduleId,
        WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan,
        WorkflowChatRequestEvent? executionRequest = null,
        string? commandId = null,
        string? correlationId = null)
    {
        var envelopeId = executionRequest == null
            ? $"ensure-workflow-run-{runId}"
            : commandId?.Trim() ?? string.Empty;
        var ensure = new EnsureWorkflowRunDefinitionEvent
        {
            Binding = BuildBindWorkflowRunDefinitionEvent(
                definitionActorId,
                runId,
                workflowYaml,
                workflowName,
                inlineWorkflowYamls,
                scopeId,
                runOrigin,
                scheduleId,
                capabilityAdmissionPlan),
        };
        if (executionRequest != null)
            ensure.ExecutionRequest = executionRequest.Clone();

        return new EventEnvelope
        {
            Id = envelopeId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(ensure),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                WorkflowRunActorPortPublisherId,
                TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = executionRequest == null
                    ? envelopeId
                    : correlationId?.Trim() ?? string.Empty,
            },
        };
    }

    private static EventEnvelope CreateWorkflowRunStoppedEnvelope(
        string actorId,
        string runId,
        string reason) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new WorkflowRunStoppedEvent
            {
                RunId = runId ?? string.Empty,
                Reason = reason ?? string.Empty,
            }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(WorkflowRunActorPortPublisherId, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = actorId ?? string.Empty,
                CausationEventId = "workflow_run_stopped",
            },
        };

    private static BindWorkflowDefinitionEvent BuildBindWorkflowDefinitionEvent(
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        string? scopeId,
        string? sourceKind,
        WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan)
    {
        var bind = new BindWorkflowDefinitionEvent
        {
            WorkflowYaml = workflowYaml ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
            SourceKind = sourceKind?.Trim() ?? string.Empty,
            CapabilityAdmissionPlan = capabilityAdmissionPlan?.Clone(),
        };
        if (scopeId is not null)
            bind.ScopeId = scopeId.Trim();

        if (inlineWorkflowYamls != null)
        {
            foreach (var (key, value) in inlineWorkflowYamls)
                bind.InlineWorkflowYamls[key] = value;
        }

        return bind;
    }

    private static BindWorkflowRunDefinitionEvent BuildBindWorkflowRunDefinitionEvent(
        string definitionActorId,
        string runId,
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string> inlineWorkflowYamls,
        string? scopeId,
        string? runOrigin,
        string? scheduleId,
        WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan)
    {
        var bind = new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = definitionActorId ?? string.Empty,
            RunId = runId ?? string.Empty,
            WorkflowYaml = workflowYaml ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
            ScopeId = scopeId?.Trim() ?? string.Empty,
            RunOrigin = runOrigin?.Trim() ?? string.Empty,
            ScheduleId = scheduleId?.Trim() ?? string.Empty,
            CapabilityAdmissionPlan = capabilityAdmissionPlan?.Clone(),
        };

        foreach (var (key, value) in inlineWorkflowYamls)
            bind.InlineWorkflowYamls[key] = value;

        return bind;
    }

    private static string? BuildRunActorId(string? definitionActorId)
    {
        var normalizedDefinitionActorId = NormalizeActorId(definitionActorId);
        return normalizedDefinitionActorId == null
            ? null
            : $"{normalizedDefinitionActorId}:run:{Guid.NewGuid():N}";
    }

    private readonly record struct DefinitionActorResolutionResult(
        string ActorId,
        bool CreatedNow,
        WorkflowCapabilityAdmissionPlan? CapabilityAdmissionPlan = null);
}
