using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Google.Protobuf.WellKnownTypes;

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
    IWorkflowDefinitionParser
{
    private const string WorkflowRunActorPortPublisherId = "workflow.run.actor.port";
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IWorkflowActorBindingReader _bindingReader;
    private readonly ISet<string> _knownStepTypes;
    private readonly IAgentKindRegistry? _agentKindRegistry;
    private readonly WorkflowParser _workflowParser = new();

    public WorkflowRunActorPort(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IWorkflowActorBindingReader bindingReader,
        IEnumerable<IWorkflowModulePack> modulePacks,
        IAgentKindRegistry? agentKindRegistry = null)
    {
        _runtime = runtime;
        _dispatchPort = dispatchPort;
        _bindingReader = bindingReader;
        _agentKindRegistry = agentKindRegistry;
        var packs = modulePacks?.ToList()
            ?? throw new ArgumentNullException(nameof(modulePacks));
        if (packs.Count == 0)
            packs.Add(new WorkflowCoreModulePack());
        _knownStepTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
            packs.SelectMany(x => x.Modules).SelectMany(x => x.Names));
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
            definitionResolution = await EnsureDefinitionActorAsync(definition, NormalizeActorId(definition.DefinitionActorId), ct);
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
                    definition.ScheduleId),
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
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("Actor id is required.", nameof(actorId));

        // Refactor (iter18/cluster-006):
        //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
        //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
        var envelope = CreateWorkflowDefinitionBindEnvelope(workflowYaml, workflowName, inlineWorkflowYamls, scopeId);
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

    public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workflowYaml))
            return Task.FromResult(WorkflowYamlParseResult.Invalid("Workflow YAML is required."));

        try
        {
            var workflow = _workflowParser.Parse(workflowYaml);
            var errors = WorkflowValidator.Validate(
                workflow,
                new WorkflowValidator.WorkflowValidationOptions
                {
                    RequireKnownStepTypes = true,
                    KnownStepTypes = _knownStepTypes,
                },
                availableWorkflowNames: null);
            if (errors.Count > 0)
                return Task.FromResult(WorkflowYamlParseResult.Invalid(string.Join("; ", errors)));

            if (_agentKindRegistry != null)
            {
                foreach (var role in workflow.Roles)
                {
                    var agentKind = role.AgentKind?.Trim() ?? string.Empty;
                    if (!_agentKindRegistry.TryResolve(agentKind, out _))
                    {
                        return Task.FromResult(WorkflowYamlParseResult.Invalid(
                            $"Role '{role.Id}' declares unknown agent_kind '{agentKind}'. " +
                            $"Register an agent for that kind or use the default '{WorkflowRoleConventions.DefaultAgentKind}'."));
                    }
                }
            }

            var workflowName = string.IsNullOrWhiteSpace(workflow.Name)
                ? string.Empty
                : workflow.Name.Trim();
            if (string.IsNullOrWhiteSpace(workflowName))
                return Task.FromResult(WorkflowYamlParseResult.Invalid("Workflow name is required."));

            return Task.FromResult(WorkflowYamlParseResult.Success(
                workflowName,
                WorkflowAuthorizationDependencyEvaluator.Evaluate(workflow)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(WorkflowYamlParseResult.Invalid(ex.Message));
        }
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
                // A missing binding doc, or one frozen to the Run kind, is the signature of a definition
                // _id whose binding read-model was clobbered by a relayed run-bind (the studio failure).
                // When the caller supplies the definition payload (built-in/catalog-resolved definitions
                // always do), re-bind from that payload instead of failing the run; the binding heal
                // write dispatcher (Definition supersedes a Run-kind slot) lets the re-bind win, so the
                // actor self-heals back to a Definition document. A genuinely unsupported actor (not a
                // workflow definition) still fails fast — re-binding onto it would be wrong.
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
                        ct);
                    return new DefinitionActorResolutionResult(existingActor.Id, CreatedNow: false);
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
                    ct);
            }

            return new DefinitionActorResolutionResult(existingActor.Id, CreatedNow: false);
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
                return new DefinitionActorResolutionResult(racedActor.Id, CreatedNow: false);

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
                ct);
            return new DefinitionActorResolutionResult(definitionActor.Id, CreatedNow: true);
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
            catch
            {
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
        string? scopeId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(BuildBindWorkflowDefinitionEvent(workflowYaml, workflowName, inlineWorkflowYamls, scopeId)),
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
        string? scheduleId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(BuildBindWorkflowRunDefinitionEvent(definitionActorId, runId, workflowYaml, workflowName, inlineWorkflowYamls, scopeId, runOrigin, scheduleId)),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(WorkflowRunActorPortPublisherId, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };

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
        string? scopeId)
    {
        var bind = new BindWorkflowDefinitionEvent
        {
            WorkflowYaml = workflowYaml ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
            ScopeId = scopeId?.Trim() ?? string.Empty,
        };

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
        string? scheduleId)
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
        bool CreatedNow);
}
