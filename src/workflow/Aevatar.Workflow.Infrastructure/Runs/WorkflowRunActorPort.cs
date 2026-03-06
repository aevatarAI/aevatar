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
/// Infrastructure adapter for workflow definition lookup and run actor lifecycle.
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

    public Task<IActor?> GetDefinitionActorAsync(string definitionActorId, CancellationToken ct = default)
    {
        _ = ct;
        return _runtime.GetAsync(definitionActorId);
    }

    public Task<IActor?> GetRunActorAsync(string runActorId, CancellationToken ct = default)
    {
        _ = ct;
        return _runtime.GetAsync(runActorId);
    }

    public Task<IActor> CreateRunActorAsync(CancellationToken ct = default) =>
        _runtime.CreateAsync<WorkflowRunGAgent>(ct: ct);

    public Task DestroyRunActorAsync(string runActorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runActorId))
            throw new ArgumentException("Run actor id is required.", nameof(runActorId));

        return _runtime.DestroyAsync(runActorId, ct);
    }

    public Task<bool> IsWorkflowDefinitionActorAsync(IActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return _agentTypeVerifier.IsExpectedAsync(actor.Id, typeof(WorkflowGAgent), ct);
    }

    public Task<bool> IsWorkflowRunActorAsync(IActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return _agentTypeVerifier.IsExpectedAsync(actor.Id, typeof(WorkflowRunGAgent), ct);
    }

    public Task<WorkflowDefinitionBindingSnapshot?> GetDefinitionBindingSnapshotAsync(IActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(BuildBindingSnapshot(actor.Agent));
    }

    public Task BindWorkflowDefinitionAsync(
        IActor runActor,
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runActor);
        var envelope = CreateWorkflowDefinitionBindEnvelope(workflowYaml, workflowName, inlineWorkflowYamls);
        return runActor.HandleEventAsync(envelope, ct);
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

    private static WorkflowDefinitionBindingSnapshot? BuildBindingSnapshot(IAgent? agent)
    {
        switch (agent)
        {
            case WorkflowRunGAgent runAgent:
            {
                var snapshot = runAgent.GetBindingSnapshot();
                return new WorkflowDefinitionBindingSnapshot(
                    snapshot.WorkflowName,
                    snapshot.WorkflowYaml,
                    snapshot.InlineWorkflowYamls);
            }
            case WorkflowGAgent workflowAgent:
            {
                return new WorkflowDefinitionBindingSnapshot(
                    workflowAgent.State.WorkflowName ?? string.Empty,
                    workflowAgent.State.WorkflowYaml ?? string.Empty,
                    workflowAgent.State.InlineWorkflowYamls.ToDictionary(
                        x => x.Key,
                        x => x.Value,
                        StringComparer.OrdinalIgnoreCase));
            }
            default:
                return null;
        }
    }
}
