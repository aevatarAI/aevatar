using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

/// <summary>
/// Workflow definition actor.
/// Owns the bound workflow definition and creates per-run <see cref="WorkflowRunGAgent"/> instances.
/// </summary>
public sealed class WorkflowGAgent : GAgentBase<WorkflowState>
{
    private const string DefinitionPublisherId = "workflow.definition.actor";

    private readonly IActorRuntime _runtime;
    private readonly ISet<string> _knownStepTypes;
    private readonly WorkflowCompilationService _workflowCompilationService;

    public WorkflowGAgent(
        IActorRuntime runtime,
        IEnumerable<IWorkflowPrimitivePack> primitivePacks)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        var packs = (primitivePacks ?? throw new ArgumentNullException(nameof(primitivePacks))).ToList();
        if (packs.Count == 0)
            packs.Add(new WorkflowCorePrimitivePack());

        _knownStepTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
            packs.SelectMany(x => x.Executors).SelectMany(x => x.Names));
        _knownStepTypes.UnionWith(WorkflowPrimitiveCatalog.BuiltInCanonicalTypes);
        _workflowCompilationService = new WorkflowCompilationService(
            new HashSet<string>(_knownStepTypes, StringComparer.OrdinalIgnoreCase));
    }

    public override Task<string> GetDescriptionAsync()
    {
        var status = State.Compiled ? "compiled" : "invalid";
        return Task.FromResult($"WorkflowGAgent[{State.WorkflowName}] v{State.Version} ({status})");
    }

    protected override WorkflowState TransitionState(WorkflowState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<WorkflowStateUpdatedEvent>((_, updated) => updated.State.Clone())
            .OrCurrent();

    public async Task BindWorkflowDefinitionAsync(
        string workflowYaml,
        string? workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        CancellationToken ct = default)
    {
        EnsureWorkflowNameCanBind(workflowName);

        var next = State.Clone();
        next.WorkflowYaml = workflowYaml ?? string.Empty;
        next.InlineWorkflowYamls.Clear();
        if (inlineWorkflowYamls != null)
        {
            foreach (var (name, yaml) in inlineWorkflowYamls)
            {
                var normalizedName = WorkflowRunIdNormalizer.NormalizeWorkflowName(name);
                if (string.IsNullOrWhiteSpace(normalizedName) || string.IsNullOrWhiteSpace(yaml))
                    continue;

                next.InlineWorkflowYamls[normalizedName] = yaml;
            }
        }

        var normalizedWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(workflowName);
        if (!string.IsNullOrWhiteSpace(normalizedWorkflowName))
            next.WorkflowName = normalizedWorkflowName;

        var compileResult = EvaluateWorkflowCompilation(next.WorkflowYaml);
        next.Compiled = compileResult.Compiled;
        next.CompilationError = compileResult.CompilationError;
        next.Version = State.Version + 1;
        if (compileResult.Compiled && compileResult.Workflow != null && string.IsNullOrWhiteSpace(next.WorkflowName))
            next.WorkflowName = compileResult.Workflow.Name;

        await PersistStateAsync(next, ct);
    }

    [EventHandler]
    public Task HandleBindWorkflowDefinition(BindWorkflowDefinitionEvent request) =>
        BindWorkflowDefinitionAsync(request.WorkflowYaml, request.WorkflowName, request.InlineWorkflowYamls);

    [EventHandler]
    public async Task HandleChatRequest(ChatRequestEvent request)
    {
        if (!State.Compiled || string.IsNullOrWhiteSpace(State.WorkflowYaml))
        {
            await PublishAsync(new ChatResponseEvent
            {
                SessionId = request.SessionId,
                Content = "Workflow is not compiled or definition-bound.",
            }, EventDirection.Up);
            return;
        }

        var runActor = await CreateAndBindRunActorAsync(CancellationToken.None);
        await runActor.HandleEventAsync(CreateEnvelope(request, Id), CancellationToken.None);
    }

    [AllEventHandler(Priority = 40, AllowSelfHandling = true)]
    public async Task HandleWorkflowCompletionEnvelope(EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(WorkflowCompletedEvent.Descriptor) != true)
            return;

        if (string.IsNullOrWhiteSpace(envelope.PublisherId) ||
            string.Equals(envelope.PublisherId, Id, StringComparison.Ordinal))
        {
            return;
        }

        var actor = await _runtime.GetAsync(envelope.PublisherId);
        if (actor?.Agent is not WorkflowRunGAgent)
            return;

        await UpdateExecutionCountersAsync(envelope.Payload.Unpack<WorkflowCompletedEvent>(), CancellationToken.None);
    }

    public async Task HandleWorkflowCompleted(WorkflowCompletedEvent evt)
    {
        await UpdateExecutionCountersAsync(evt, CancellationToken.None);
        await PublishAsync(new TextMessageEndEvent
        {
            SessionId = evt.RunId,
            Content = evt.Success ? evt.Output : $"Workflow execution failed: {evt.Error}",
        }, EventDirection.Up, CancellationToken.None);
    }

    private async Task<IActor> CreateAndBindRunActorAsync(CancellationToken ct)
    {
        var runActor = await _runtime.CreateAsync<WorkflowRunGAgent>(ct: ct);
        if (!string.IsNullOrWhiteSpace(Id))
            await _runtime.LinkAsync(Id, runActor.Id, ct);

        var bind = new BindWorkflowDefinitionEvent
        {
            WorkflowYaml = State.WorkflowYaml,
            WorkflowName = State.WorkflowName,
        };
        foreach (var (name, yaml) in State.InlineWorkflowYamls)
            bind.InlineWorkflowYamls[name] = yaml;

        await runActor.HandleEventAsync(CreateEnvelope(bind, DefinitionPublisherId), ct);

        return runActor;
    }

    private async Task UpdateExecutionCountersAsync(WorkflowCompletedEvent evt, CancellationToken ct)
    {
        var next = State.Clone();
        next.TotalExecutions++;
        if (evt.Success)
            next.SuccessfulExecutions++;
        else
            next.FailedExecutions++;

        await PersistStateAsync(next, ct);
    }

    private Task PersistStateAsync(WorkflowState next, CancellationToken ct) =>
        PersistDomainEventAsync(new WorkflowStateUpdatedEvent
        {
            State = next.Clone(),
        }, ct);

    private EventEnvelope CreateEnvelope(IMessage message, string? publisherId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(message),
            PublisherId = string.IsNullOrWhiteSpace(publisherId) ? DefinitionPublisherId : publisherId,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };

    private WorkflowCompilationResult EvaluateWorkflowCompilation(string yaml)
    {
        var result = _workflowCompilationService.Compile(yaml);
        if (!result.Compiled && !string.IsNullOrWhiteSpace(result.CompilationError))
            Logger.LogWarning("WorkflowGAgent compile failed: {Error}", result.CompilationError);

        return result;
    }

    private void EnsureWorkflowNameCanBind(string? workflowName)
    {
        var incomingWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(workflowName);
        var currentWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(State.WorkflowName);
        if (!string.IsNullOrWhiteSpace(currentWorkflowName) &&
            !string.IsNullOrWhiteSpace(incomingWorkflowName) &&
            !string.Equals(currentWorkflowName, incomingWorkflowName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"WorkflowGAgent '{Id}' is already bound to workflow '{State.WorkflowName}' and cannot switch to '{workflowName}'.");
        }
    }
}
