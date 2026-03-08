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
internal sealed class WorkflowRunActorPort : IWorkflowRunActorPort
{
    private const string WorkflowRunActorPortPublisherId = "workflow.run.actor.port";
    private readonly IActorRuntime _runtime;
    private readonly IAgentTypeVerifier _agentTypeVerifier;
    private readonly ISet<string> _knownStepTypes;
    private readonly WorkflowParser _workflowParser = new();

    public WorkflowRunActorPort(
        IActorRuntime runtime,
        IAgentTypeVerifier agentTypeVerifier,
        IEnumerable<IWorkflowModulePack> modulePacks)
    {
        _runtime = runtime;
        _agentTypeVerifier = agentTypeVerifier;
        var packs = modulePacks?.ToList()
            ?? throw new ArgumentNullException(nameof(modulePacks));
        if (packs.Count == 0)
            packs.Add(new WorkflowCoreModulePack());
        _knownStepTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
            packs.SelectMany(x => x.Modules).SelectMany(x => x.Names));
    }

    public Task<IActor?> GetAsync(string actorId, CancellationToken ct = default)
    {
        _ = ct;
        return _runtime.GetAsync(actorId);
    }

    public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default) =>
        _runtime.CreateAsync<WorkflowGAgent>(actorId, ct: ct);

    public Task<WorkflowActorBinding> DescribeAsync(IActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ct.ThrowIfCancellationRequested();

        var binding = actor.Agent switch
        {
            WorkflowGAgent definitionActor => new WorkflowActorBinding(
                WorkflowActorKind.Definition,
                actor.Id,
                actor.Id,
                string.Empty,
                definitionActor.State.WorkflowName,
                definitionActor.State.WorkflowYaml,
                new Dictionary<string, string>(definitionActor.State.InlineWorkflowYamls, StringComparer.OrdinalIgnoreCase)),
            WorkflowRunGAgent runActor => new WorkflowActorBinding(
                WorkflowActorKind.Run,
                actor.Id,
                runActor.State.DefinitionActorId,
                runActor.State.RunId,
                runActor.State.WorkflowName,
                runActor.State.WorkflowYaml,
                new Dictionary<string, string>(runActor.State.InlineWorkflowYamls, StringComparer.OrdinalIgnoreCase)),
            _ => WorkflowActorBinding.Unsupported(actor.Id),
        };

        return Task.FromResult(binding);
    }

    public async Task<IActor> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.WorkflowYaml) ||
            string.IsNullOrWhiteSpace(definition.WorkflowName))
        {
            throw new InvalidOperationException(
                "Workflow run creation requires a valid workflow definition binding.");
        }

        var definitionActorId = await EnsureDefinitionActorAsync(definition, ct);
        var runActor = await _runtime.CreateAsync<WorkflowRunGAgent>(ct: ct);
        if (!string.IsNullOrWhiteSpace(definitionActorId))
            await _runtime.LinkAsync(definitionActorId, runActor.Id);

        await runActor.HandleEventAsync(
            CreateWorkflowRunBindEnvelope(
                definitionActorId,
                runActor.Id,
                definition.WorkflowYaml,
                definition.WorkflowName,
                definition.InlineWorkflowYamls),
            ct);
        return runActor;
    }

    public Task DestroyAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("Actor id is required.", nameof(actorId));

        return _runtime.DestroyAsync(actorId, ct);
    }

    public async Task<bool> IsWorkflowDefinitionActorAsync(IActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return await _agentTypeVerifier.IsExpectedAsync(actor.Id, typeof(WorkflowGAgent), ct);
    }

    public async Task<bool> IsWorkflowRunActorAsync(IActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return await _agentTypeVerifier.IsExpectedAsync(actor.Id, typeof(WorkflowRunGAgent), ct);
    }

    public Task<string?> GetBoundWorkflowNameAsync(IActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ct.ThrowIfCancellationRequested();

        var workflowName = actor.Agent switch
        {
            WorkflowGAgent definitionActor => definitionActor.State.WorkflowName,
            WorkflowRunGAgent runActor => runActor.State.WorkflowName,
            _ => string.Empty,
        };

        return Task.FromResult(
            string.IsNullOrWhiteSpace(workflowName)
                ? null
                : workflowName.Trim());
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
        return actor.HandleEventAsync(envelope, ct);
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

    private async Task<string> EnsureDefinitionActorAsync(
        WorkflowDefinitionBinding definition,
        CancellationToken ct)
    {
        var requestedDefinitionActorId = definition.DefinitionActorId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(requestedDefinitionActorId))
        {
            var existingActor = await _runtime.GetAsync(requestedDefinitionActorId);
            if (existingActor != null &&
                await _agentTypeVerifier.IsExpectedAsync(existingActor.Id, typeof(WorkflowGAgent), ct))
            {
                var binding = await DescribeAsync(existingActor, ct);
                if (!binding.HasDefinitionPayload)
                {
                    await BindWorkflowDefinitionAsync(
                        existingActor,
                        definition.WorkflowYaml,
                        definition.WorkflowName,
                        definition.InlineWorkflowYamls,
                        ct);
                }
                else if (IsSameDefinition(binding, definition))
                {
                    return existingActor.Id;
                }
                else
                {
                    return await CreateBoundDefinitionActorAsync(definition, preferredActorId: null, ct);
                }

                return existingActor.Id;
            }

            return await CreateBoundDefinitionActorAsync(definition, preferredActorId: null, ct);
        }

        return await CreateBoundDefinitionActorAsync(definition, requestedDefinitionActorId, ct);
    }

    private async Task<string> CreateBoundDefinitionActorAsync(
        WorkflowDefinitionBinding definition,
        string? preferredActorId,
        CancellationToken ct)
    {
        var definitionActor = await CreateDefinitionAsync(preferredActorId, ct);
        await BindWorkflowDefinitionAsync(
            definitionActor,
            definition.WorkflowYaml,
            definition.WorkflowName,
            definition.InlineWorkflowYamls,
            ct);
        return definitionActor.Id;
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

    private static EventEnvelope CreateWorkflowDefinitionBindEnvelope(
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(BuildBindWorkflowDefinitionEvent(workflowYaml, workflowName, inlineWorkflowYamls)),
            PublisherId = WorkflowRunActorPortPublisherId,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
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
            PublisherId = WorkflowRunActorPortPublisherId,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
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
}
