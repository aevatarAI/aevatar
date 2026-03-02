using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Infrastructure.Runs;

/// <summary>
/// Infrastructure adapter for workflow actor lifecycle and binding operations.
/// </summary>
internal sealed class WorkflowRunActorPort : IWorkflowRunActorPort
{
    private const string WorkflowRunActorPortPublisherId = "workflow.run.actor.port";
    private readonly IActorRuntime _runtime;
    private readonly IAgentManifestStore _manifestStore;
    private readonly IAgentTypeVerifier _agentTypeVerifier;
    private readonly ISet<string> _knownStepTypes;
    private readonly WorkflowParser _workflowParser = new();

    public WorkflowRunActorPort(
        IActorRuntime runtime,
        IAgentManifestStore manifestStore,
        IAgentTypeVerifier agentTypeVerifier,
        IEnumerable<IWorkflowModulePack> modulePacks)
    {
        _runtime = runtime;
        _manifestStore = manifestStore;
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

    public Task<IActor> CreateAsync(CancellationToken ct = default) =>
        _runtime.CreateAsync<WorkflowGAgent>(ct: ct);

    public Task DestroyAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("Actor id is required.", nameof(actorId));

        return _runtime.DestroyAsync(actorId, ct);
    }

    public async Task<bool> IsWorkflowActorAsync(IActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return await _agentTypeVerifier.IsExpectedAsync(actor.Id, typeof(WorkflowGAgent), ct);
    }

    public async Task<string?> GetBoundWorkflowNameAsync(IActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var manifest = await _manifestStore.LoadAsync(actor.Id, ct);
        if (manifest?.Metadata == null)
            return null;

        return manifest.Metadata.TryGetValue(WorkflowManifestMetadataKeys.WorkflowName, out var workflowName)
            ? workflowName
            : null;
    }

    public Task ConfigureWorkflowAsync(
        IActor actor,
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var envelope = CreateConfigureWorkflowEnvelope(workflowYaml, workflowName, inlineWorkflowYamls);
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

    private static EventEnvelope CreateConfigureWorkflowEnvelope(
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(BuildConfigureWorkflowEvent(workflowYaml, workflowName, inlineWorkflowYamls)),
            PublisherId = WorkflowRunActorPortPublisherId,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };

    private static ConfigureWorkflowEvent BuildConfigureWorkflowEvent(
        string workflowYaml,
        string workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls)
    {
        var configure = new ConfigureWorkflowEvent
        {
            WorkflowYaml = workflowYaml ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
        };

        if (inlineWorkflowYamls != null)
        {
            foreach (var (key, value) in inlineWorkflowYamls)
                configure.InlineWorkflowYamls[key] = value;
        }

        return configure;
    }
}
