using Aevatar.Foundation.Abstractions;
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
internal sealed class WorkflowRunActorPort : IWorkflowRunActorPort
{
    private const string WorkflowRunActorPortPublisherId = "workflow.run.actor.port";
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IWorkflowActorBindingReader _bindingReader;
    private readonly ISet<string> _knownStepTypes;
    private readonly WorkflowParser _workflowParser = new();

    public WorkflowRunActorPort(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IWorkflowActorBindingReader bindingReader,
        IEnumerable<IWorkflowModulePack> modulePacks)
    {
        _runtime = runtime;
        _dispatchPort = dispatchPort;
        _bindingReader = bindingReader;
        var packs = modulePacks?.ToList()
            ?? throw new ArgumentNullException(nameof(modulePacks));
        if (packs.Count == 0)
            packs.Add(new WorkflowCoreModulePack());
        _knownStepTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
            packs.SelectMany(x => x.Modules).SelectMany(x => x.Names));
    }

    public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default) =>
        _runtime.CreateAsync<WorkflowGAgent>(actorId, ct: ct);

    public async Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default)
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
            definitionResolution = await EnsureDefinitionActorAsync(definition, ct);
            if (definitionResolution.CreatedNow && !string.IsNullOrWhiteSpace(definitionResolution.ActorId))
                createdActorIds.Add(definitionResolution.ActorId);

            runActor = await _runtime.CreateAsync<WorkflowRunGAgent>(ct: ct);
            createdActorIds.Add(runActor.Id);
            if (!string.IsNullOrWhiteSpace(definitionResolution.ActorId))
                await _runtime.LinkAsync(definitionResolution.ActorId, runActor.Id, ct);

            await _dispatchPort.DispatchAsync(
                runActor.Id,
                CreateWorkflowRunBindEnvelope(
                    definitionResolution.ActorId,
                    runActor.Id,
                    definition.WorkflowYaml,
                    definition.WorkflowName,
                    definition.InlineWorkflowYamls),
                ct);

            return new WorkflowRunCreationResult(
                runActor,
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

    public Task BindWorkflowDefinitionAsync(
        IActor actor,
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var envelope = CreateWorkflowDefinitionBindEnvelope(workflowYaml, workflowName, inlineWorkflowYamls);
        return _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
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

            var workflowName = string.IsNullOrWhiteSpace(workflow.Name)
                ? string.Empty
                : workflow.Name.Trim();
            if (string.IsNullOrWhiteSpace(workflowName))
                return Task.FromResult(WorkflowYamlParseResult.Invalid("Workflow name is required."));

            return Task.FromResult(WorkflowYamlParseResult.Success(workflowName));
        }
        catch (Exception ex)
        {
            return Task.FromResult(WorkflowYamlParseResult.Invalid(ex.Message));
        }
    }

    private async Task<DefinitionActorResolutionResult> EnsureDefinitionActorAsync(
        WorkflowDefinitionBinding definition,
        CancellationToken ct)
    {
        var requestedDefinitionActorId = NormalizeActorId(definition.DefinitionActorId);
        if (requestedDefinitionActorId != null)
        {
            var existingActor = await _runtime.GetAsync(requestedDefinitionActorId);
            if (existingActor == null)
                return await CreateBoundDefinitionActorAsync(definition, requestedDefinitionActorId, ct);

            var binding = await _bindingReader.GetAsync(existingActor.Id, ct);
            if (binding == null || binding.ActorKind != WorkflowActorKind.Definition)
            {
                throw new InvalidOperationException(
                    $"Actor '{existingActor.Id}' is not a workflow definition actor and cannot be reused as a definition source.");
            }

            EnsureWorkflowNameCompatibility(existingActor.Id, binding, definition);

            if (!binding.HasDefinitionPayload || !IsSameDefinition(binding, definition))
            {
                await BindWorkflowDefinitionAsync(
                    existingActor,
                    definition.WorkflowYaml,
                    definition.WorkflowName,
                    definition.InlineWorkflowYamls,
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
            definitionActor = await CreateDefinitionAsync(preferredActorId, ct);
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
                definitionActor,
                definition.WorkflowYaml,
                definition.WorkflowName,
                definition.InlineWorkflowYamls,
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
        if (!binding.HasDefinitionPayload || !IsSameDefinition(binding, definition))
        {
            await BindWorkflowDefinitionAsync(
                existingActor,
                definition.WorkflowYaml,
                definition.WorkflowName,
                definition.InlineWorkflowYamls,
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

    private static EventEnvelope CreateWorkflowDefinitionBindEnvelope(
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(BuildBindWorkflowDefinitionEvent(workflowYaml, workflowName, inlineWorkflowYamls)),
            Route = new EnvelopeRoute
            {
                PublisherActorId = WorkflowRunActorPortPublisherId,
                Direction = EventDirection.Self,
            },
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
        IReadOnlyDictionary<string, string> inlineWorkflowYamls) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(BuildBindWorkflowRunDefinitionEvent(definitionActorId, runId, workflowYaml, workflowName, inlineWorkflowYamls)),
            Route = new EnvelopeRoute
            {
                PublisherActorId = WorkflowRunActorPortPublisherId,
                Direction = EventDirection.Self,
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };

    private static BindWorkflowDefinitionEvent BuildBindWorkflowDefinitionEvent(
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls)
    {
        var bind = new BindWorkflowDefinitionEvent
        {
            WorkflowYaml = workflowYaml ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
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
        IReadOnlyDictionary<string, string> inlineWorkflowYamls)
    {
        var bind = new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = definitionActorId ?? string.Empty,
            RunId = runId ?? string.Empty,
            WorkflowYaml = workflowYaml ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
        };

        foreach (var (key, value) in inlineWorkflowYamls)
            bind.InlineWorkflowYamls[key] = value;

        return bind;
    }

    private readonly record struct DefinitionActorResolutionResult(
        string ActorId,
        bool CreatedNow);
}
