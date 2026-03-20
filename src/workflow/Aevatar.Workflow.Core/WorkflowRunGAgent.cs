using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.AI.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Aevatar.Workflow.Core;

[SuppressMessage(
    "Maintainability",
    "CA1506:Avoid excessive class coupling",
    Justification = "WorkflowRunGAgent is the run-scoped orchestration boundary and intentionally coordinates workflow execution dependencies.")]
public sealed class WorkflowRunGAgent
    : GAgentBase<WorkflowRunState>,
      IWorkflowExecutionStateHost
{
    private const string RunningStatus = "running";
    private const string CompletedStatus = "completed";
    private const string FailedStatus = "failed";
    private const string StoppedStatus = "stopped";
    private const string WorkflowCommandIdMetadataKey = "workflow.command_id";
    private const string WorkflowScopeIdMetadataKey = "workflow.scope_id";

    private WorkflowDefinition? _compiledWorkflow;
    private readonly WorkflowParser _parser = new();
    private readonly List<string> _childAgentIds = [];
    private readonly Dictionary<string, object?> _executionItems = new(StringComparer.Ordinal);
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IRoleAgentTypeResolver _roleAgentTypeResolver;
    private readonly IEventModuleFactory<IWorkflowExecutionContext> _stepExecutorFactory;
    private readonly IReadOnlyList<IWorkflowModuleDependencyExpander> _moduleDependencyExpanders;
    private readonly IReadOnlyList<IWorkflowModuleConfigurator> _moduleConfigurators;
    private readonly ISet<string> _knownModuleStepTypes;
    private readonly SubWorkflowOrchestrator _subWorkflowOrchestrator;

    public WorkflowRunGAgent(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IRoleAgentTypeResolver roleAgentTypeResolver,
        IEventModuleFactory<IWorkflowExecutionContext> stepExecutorFactory,
        IEnumerable<IWorkflowModulePack> modulePacks,
        IWorkflowDefinitionResolver? workflowDefinitionResolver = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _roleAgentTypeResolver = roleAgentTypeResolver ?? throw new ArgumentNullException(nameof(roleAgentTypeResolver));
        _stepExecutorFactory = stepExecutorFactory ?? throw new ArgumentNullException(nameof(stepExecutorFactory));
        _ = workflowDefinitionResolver;

        var packs = modulePacks?.ToList()
            ?? throw new ArgumentNullException(nameof(modulePacks));

        _moduleDependencyExpanders = packs
            .SelectMany(x => x.DependencyExpanders)
            .GroupBy(x => x.GetType())
            .Select(x => x.First())
            .OrderBy(x => x.Order)
            .ToList();

        _moduleConfigurators = packs
            .SelectMany(x => x.Configurators)
            .GroupBy(x => x.GetType())
            .Select(x => x.First())
            .OrderBy(x => x.Order)
            .ToList();

        _knownModuleStepTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
            packs
                .SelectMany(x => x.Modules)
                .SelectMany(x => x.Names));

        _subWorkflowOrchestrator = new SubWorkflowOrchestrator(
            _runtime,
            _dispatchPort,
            () => Id,
            () => Logger,
            (evt, token) => PersistDomainEventAsync(evt, token),
            (events, token) => PersistDomainEventsAsync(events, token),
            (evt, direction, token) => PublishAsync(evt, direction, token),
            (actorId, evt, token) => SendToAsync(actorId, evt, token),
            (callbackId, dueTime, evt, token) => ScheduleSelfDurableTimeoutAsync(callbackId, dueTime, evt, ct: token),
            (lease, token) => CancelDurableCallbackAsync(lease, token));
    }

    public string RunId => string.IsNullOrWhiteSpace(State.RunId)
        ? Id
        : State.RunId;

    public Any? GetExecutionState(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        return State.ExecutionStates.TryGetValue(scopeKey, out var state)
            ? state
            : null;
    }

    public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
        State.ExecutionStates.ToList();

    public bool TryGetExecutionItem(
        string itemKey,
        out object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);
        return _executionItems.TryGetValue(itemKey, out value);
    }

    public void SetExecutionItem(
        string itemKey,
        object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);
        _executionItems[itemKey] = value;
    }

    public bool RemoveExecutionItem(string itemKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);
        return _executionItems.Remove(itemKey);
    }

    public Task UpsertExecutionStateAsync(
        string scopeKey,
        Any state,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ArgumentNullException.ThrowIfNull(state);
        return PersistDomainEventAsync(
            new WorkflowExecutionStateUpsertedEvent
            {
                ScopeKey = scopeKey,
                State = state,
            },
            ct);
    }

    public Task ClearExecutionStateAsync(
        string scopeKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        return PersistDomainEventAsync(
            new WorkflowExecutionStateClearedEvent
            {
                ScopeKey = scopeKey,
            },
            ct);
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        RebuildCompiledWorkflowCache();
        InstallCognitiveModules();
        await base.OnActivateAsync(ct);
    }

    public async Task BindWorkflowRunDefinitionAsync(
        string definitionActorId,
        string workflowYaml,
        string? workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        string? runId = null,
        string? scopeId = null,
        CancellationToken ct = default)
    {
        EnsureWorkflowNameCanBind(workflowName);
        var childActorIdsToReset = CaptureDerivedChildActorIdsForReset();
        var stateBeforeBind = State.Clone();
        var bindDefinitionEvent = new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = definitionActorId ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
            WorkflowYaml = workflowYaml ?? string.Empty,
            RunId = string.IsNullOrWhiteSpace(runId) ? Id : WorkflowRunIdNormalizer.Normalize(runId),
            ScopeId = scopeId?.Trim() ?? string.Empty,
        };
        if (inlineWorkflowYamls != null)
        {
            foreach (var (key, value) in inlineWorkflowYamls)
                bindDefinitionEvent.InlineWorkflowYamls[key] = value;
        }

        await PersistDomainEventAsync(bindDefinitionEvent, ct);
        await _subWorkflowOrchestrator.CancelPendingDefinitionResolutionTimeoutsAsync(stateBeforeBind, CancellationToken.None);
        RebuildCompiledWorkflowCache();
        await ResetDerivedRuntimeStateAsync(childActorIdsToReset, ct);
        InstallCognitiveModules();
    }

    [EventHandler]
    public Task HandleBindWorkflowRunDefinition(BindWorkflowRunDefinitionEvent request) =>
        BindWorkflowRunDefinitionAsync(
            request.DefinitionActorId,
            request.WorkflowYaml,
            request.WorkflowName,
            request.InlineWorkflowYamls,
            request.RunId,
            request.ScopeId);

    public override Task<string> GetDescriptionAsync()
    {
        var status = State.Compiled ? (State.Status?.Trim() ?? "bound") : "invalid";
        return Task.FromResult($"WorkflowRunGAgent[{State.WorkflowName}] run={RunId} ({status})");
    }

    [EventHandler]
    public async Task HandleChatRequest(ChatRequestEvent request)
    {
        if (_compiledWorkflow == null)
        {
            await PublishAsync(new ChatResponseEvent
            {
                Content = "Workflow run is not definition-bound or compiled.",
                SessionId = request.SessionId,
            }, TopologyAudience.Parent);
            return;
        }

        if (request.Metadata.TryGetValue(WorkflowCommandIdMetadataKey, out var commandId) &&
            !string.IsNullOrWhiteSpace(commandId))
        {
            await PersistDomainEventAsync(
                new WorkflowCommandObservedEvent
                {
                    CommandId = commandId,
                },
                CancellationToken.None);
        }

        await EnsureAgentTreeAsync();

        var runId = string.IsNullOrWhiteSpace(State.RunId)
            ? WorkflowRunIdNormalizer.Normalize(Id)
            : WorkflowRunIdNormalizer.Normalize(State.RunId);
        await PersistDomainEventAsync(new WorkflowRunExecutionStartedEvent
        {
            RunId = runId,
            WorkflowName = _compiledWorkflow.Name,
            Input = request.Prompt ?? string.Empty,
            DefinitionActorId = State.DefinitionActorId ?? string.Empty,
            ScopeId = ResolveScopeId(request.ScopeId, request.Metadata, State.ScopeId),
        });

        await PublishAsync(new StartWorkflowEvent
        {
            WorkflowName = _compiledWorkflow.Name,
            Input = request.Prompt,
            RunId = runId,
        }, TopologyAudience.Self);
    }

    [EventHandler]
    public async Task HandleReplaceWorkflowDefinitionAndExecute(ReplaceWorkflowDefinitionAndExecuteEvent request)
    {
        var yaml = request.WorkflowYaml ?? string.Empty;
        if (string.IsNullOrWhiteSpace(yaml))
        {
            Logger.LogWarning("ReplaceWorkflowDefinitionAndExecute: empty workflow YAML, ignoring.");
            await PublishAsync(new ChatResponseEvent { Content = "Dynamic workflow YAML is empty." }, TopologyAudience.Parent);
            return;
        }

        var replaceResult = await ReplaceWorkflowDefinitionBypassingBindingAsync(yaml);
        if (!replaceResult.Compiled || _compiledWorkflow == null)
        {
            var reason = string.IsNullOrWhiteSpace(replaceResult.CompilationError)
                ? "Dynamic workflow YAML compilation failed."
                : $"Dynamic workflow YAML compilation failed: {replaceResult.CompilationError}";
            Logger.LogWarning("ReplaceWorkflowDefinitionAndExecute: YAML compilation failed. Error={Error}", replaceResult.CompilationError);
            await PublishAsync(new ChatResponseEvent { Content = reason }, TopologyAudience.Parent);
            return;
        }

        await EnsureAgentTreeAsync();

        var runId = string.IsNullOrWhiteSpace(State.RunId)
            ? WorkflowRunIdNormalizer.Normalize(Id)
            : WorkflowRunIdNormalizer.Normalize(State.RunId);
        await PersistDomainEventAsync(new WorkflowRunExecutionStartedEvent
        {
            RunId = runId,
            WorkflowName = _compiledWorkflow.Name,
            Input = request.Input ?? string.Empty,
            DefinitionActorId = State.DefinitionActorId ?? string.Empty,
            ScopeId = State.ScopeId ?? string.Empty,
        });

        await PublishAsync(new StartWorkflowEvent
        {
            WorkflowName = _compiledWorkflow.Name,
            Input = request.Input ?? string.Empty,
            RunId = runId,
        }, TopologyAudience.Self);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleSubWorkflowInvokeRequested(SubWorkflowInvokeRequestedEvent request)
    {
        await _subWorkflowOrchestrator.HandleInvokeRequestedAsync(request, State, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleSubWorkflowDefinitionResolved(SubWorkflowDefinitionResolvedEvent resolved)
    {
        await _subWorkflowOrchestrator.HandleDefinitionResolvedAsync(resolved, State, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleSubWorkflowDefinitionResolveFailed(SubWorkflowDefinitionResolveFailedEvent failed)
    {
        await _subWorkflowOrchestrator.HandleDefinitionResolveFailedAsync(failed, State, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleSubWorkflowDefinitionResolutionTimeoutFired(SubWorkflowDefinitionResolutionTimeoutFiredEvent timeout)
    {
        await _subWorkflowOrchestrator.HandleDefinitionResolutionTimeoutFiredAsync(
            timeout,
            ActiveInboundEnvelope,
            State,
            CancellationToken.None);
    }

    [AllEventHandler(Priority = 50, AllowSelfHandling = true)]
    public async Task HandleWorkflowCompletionEnvelope(EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(WorkflowCompletedEvent.Descriptor) != true)
            return;

        var completed = envelope.Payload.Unpack<WorkflowCompletedEvent>();
        var publisherActorId = envelope.Route?.PublisherActorId ?? string.Empty;
        if (await _subWorkflowOrchestrator.TryHandleCompletionAsync(
                completed,
                publisherActorId,
                State,
                CancellationToken.None))
        {
            return;
        }

        if (!string.Equals(publisherActorId, Id, StringComparison.Ordinal))
        {
            Logger.LogDebug(
                "Ignore external WorkflowCompletedEvent from publisher={PublisherId} run={RunId}.",
                publisherActorId,
                completed.RunId);
            return;
        }

        await HandleWorkflowCompleted(completed);
    }

    public async Task HandleWorkflowCompleted(WorkflowCompletedEvent evt)
    {
        var stateBeforeCompletion = State.Clone();
        await PersistDomainEventAsync(evt);
        await _subWorkflowOrchestrator.CancelPendingDefinitionResolutionTimeoutsAsync(stateBeforeCompletion, CancellationToken.None);
        await _subWorkflowOrchestrator.CleanupPendingInvocationsForRunAsync(evt.RunId, stateBeforeCompletion, CancellationToken.None);
        await CleanupRoleAgentTreeAsync(CancellationToken.None);
        _executionItems.Clear();
        DisableExecutionModules();
        if (evt.Success)
        {
            Logger.LogInformation(
                "Workflow run {Name} completed: success={Success} run={RunId} outputLen={OutputLen}",
                evt.WorkflowName,
                evt.Success,
                evt.RunId,
                (evt.Output ?? string.Empty).Length);
        }
        else
        {
            Logger.LogError(
                "Workflow run {Name} failed: run={RunId} error={Error} outputLen={OutputLen}",
                evt.WorkflowName,
                evt.RunId,
                string.IsNullOrWhiteSpace(evt.Error) ? "(none)" : evt.Error,
                (evt.Output ?? string.Empty).Length);
        }

        await PublishAsync(new TextMessageEndEvent
        {
            Content = evt.Success ? evt.Output : $"Workflow execution failed: {evt.Error}",
        }, TopologyAudience.Parent);
    }

    [EventHandler]
    public async Task HandleWorkflowStopped(WorkflowStoppedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!TryPrepareStop(evt.RunId, nameof(WorkflowStoppedEvent), out var runId))
            return;

        var persistedEvent = new WorkflowStoppedEvent
        {
            WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? State.WorkflowName : evt.WorkflowName,
            RunId = runId,
            Reason = evt.Reason ?? string.Empty,
        };

        await CompleteStopAsync(
            runId,
            persistedEvent.WorkflowName,
            persistedEvent.Reason,
            ct => PersistDomainEventAsync(persistedEvent, ct),
            CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleWorkflowRunStoppedAsync(WorkflowRunStoppedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!TryPrepareStop(evt.RunId, nameof(WorkflowRunStoppedEvent), out var runId))
            return;

        var persistedEvent = new WorkflowRunStoppedEvent
        {
            RunId = runId,
            Reason = evt.Reason ?? string.Empty,
        };

        await CompleteStopAsync(
            runId,
            State.WorkflowName,
            persistedEvent.Reason,
            ct => PersistDomainEventAsync(persistedEvent, ct),
            CancellationToken.None);
    }

    [AllEventHandler(Priority = 40, AllowSelfHandling = true)]
    public async Task HandleWorkflowArtifactObservationEnvelope(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (WorkflowArtifactFactBuilder.TryBuild(envelope, Id, State.RunId, out var artifactFact))
            await PersistDomainEventAsync(artifactFact, CancellationToken.None);
    }

    private async Task CleanupRoleAgentTreeAsync(CancellationToken ct)
    {
        var roleActorIds = CollectRoleActorIds();
        if (roleActorIds.Count == 0)
            return;

        var remainingActorIds = new List<string>();
        foreach (var childActorId in roleActorIds)
        {
            try
            {
                await _runtime.UnlinkAsync(childActorId, ct);
                await _runtime.DestroyAsync(childActorId, ct);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Failed to cleanup workflow role actor {ChildActorId} for run actor {ActorId}.",
                    childActorId,
                    Id);
                remainingActorIds.Add(childActorId);
            }
        }

        _childAgentIds.Clear();
        _childAgentIds.AddRange(remainingActorIds);
    }

    private IReadOnlyList<string> CollectRoleActorIds()
    {
        var roleActorIds = new HashSet<string>(_childAgentIds, StringComparer.Ordinal);
        if (_compiledWorkflow == null)
            return roleActorIds.ToList();

        foreach (var role in _compiledWorkflow.Roles)
        {
            if (string.IsNullOrWhiteSpace(role.Id))
                continue;

            roleActorIds.Add(BuildChildActorId(role.Id));
        }

        return roleActorIds.ToList();
    }

    private async Task EnsureAgentTreeAsync()
    {
        if (_childAgentIds.Count > 0 || _compiledWorkflow == null)
            return;

        var roleAgentType = _roleAgentTypeResolver.ResolveRoleAgentType();
        if (!typeof(IRoleAgent).IsAssignableFrom(roleAgentType))
        {
            throw new InvalidOperationException(
                $"Role agent type '{roleAgentType.FullName}' does not implement IRoleAgent.");
        }

        foreach (var role in _compiledWorkflow.Roles)
        {
            var roleId = role.Id;
            if (string.IsNullOrWhiteSpace(roleId))
            {
                Logger.LogWarning(
                    "Skip workflow role without id while building agent tree. workflow={WorkflowName} actor={ActorId}",
                    _compiledWorkflow.Name,
                    Id);
                continue;
            }

            var childActorId = BuildChildActorId(roleId);
            var actor = await _runtime.GetAsync(childActorId)
                        ?? await _runtime.CreateAsync(roleAgentType, childActorId);
            await _runtime.LinkAsync(Id, actor.Id);

            await _dispatchPort.DispatchAsync(actor.Id, WorkflowRoleAgentEnvelopeFactory.CreateInitializeEnvelope(role, Id));
            _childAgentIds.Add(actor.Id);
            await PersistDomainEventAsync(new WorkflowRoleActorLinkedEvent
            {
                RunId = string.IsNullOrWhiteSpace(State.RunId)
                    ? WorkflowRunIdNormalizer.Normalize(Id)
                    : WorkflowRunIdNormalizer.Normalize(State.RunId),
                RoleId = roleId,
                ChildActorId = actor.Id,
            });
        }

        Logger.LogInformation("Workflow run actor tree created: {Count} role agents", _childAgentIds.Count);
    }

    private string BuildChildActorId(string roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            throw new InvalidOperationException("Role id is required to create child actor.");

        return $"{Id}:{roleId.Trim()}";
    }

    private void InstallCognitiveModules()
    {
        if (_compiledWorkflow == null)
        {
            Logger.LogDebug("Workflow run definition is not bound yet; skipping module installation for actor {ActorId}.", Id);
            SetModules([]);
            return;
        }

        if (IsTerminalStatus(State.Status))
        {
            Logger.LogDebug(
                "Workflow run is terminal; skipping module installation for actor {ActorId} status={Status}.",
                Id,
                State.Status);
            SetModules([]);
            return;
        }

        if (_moduleDependencyExpanders.Count == 0)
        {
            SetModules(
            [
                new WorkflowExecutionKernel(_compiledWorkflow, this),
                new WorkflowExecutionBridgeModule([], this),
            ]);
            return;
        }

        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var expander in _moduleDependencyExpanders)
            expander.Expand(_compiledWorkflow, needed);

        Logger.LogInformation("Installing workflow run modules: {Modules}", string.Join(", ", needed));

        var executors = new List<IEventModule<IWorkflowExecutionContext>>();
        foreach (var name in needed)
        {
            if (_stepExecutorFactory.TryCreate(name, out var module) && module != null)
            {
                ConfigureModule(module);
                executors.Add(module);
                continue;
            }

            var workflowName = _compiledWorkflow?.Name ?? State.WorkflowName;
            throw new InvalidOperationException(
                $"Workflow '{workflowName}' requires module '{name}', but no module registration was found.");
        }

        var workflowModules = new List<IEventModule<IEventHandlerContext>>
        {
            new WorkflowExecutionKernel(_compiledWorkflow, this),
            new WorkflowExecutionBridgeModule(
                executors,
                this),
        };
        SetModules(workflowModules);
    }

    private void DisableExecutionModules() => SetModules([]);

    private void ConfigureModule(IEventModule<IWorkflowExecutionContext> module)
    {
        if (_compiledWorkflow == null)
            return;

        foreach (var configurator in _moduleConfigurators)
            configurator.Configure(module, _compiledWorkflow);
    }

    protected override WorkflowRunState TransitionState(WorkflowRunState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<BindWorkflowRunDefinitionEvent>(ApplyBindWorkflowRunDefinition)
            .On<WorkflowCommandObservedEvent>(ApplyWorkflowCommandObserved)
            .On<WorkflowRunExecutionStartedEvent>(ApplyWorkflowRunExecutionStarted)
            .On<WorkflowExecutionStateUpsertedEvent>(ApplyWorkflowExecutionStateUpserted)
            .On<WorkflowExecutionStateClearedEvent>(ApplyWorkflowExecutionStateCleared)
            .On<WorkflowStoppedEvent>(ApplyWorkflowStopped)
            .On<WorkflowCompletedEvent>(ApplyWorkflowCompleted)
            .On<WorkflowRunStoppedEvent>(ApplyWorkflowRunStopped)
            .On<SubWorkflowDefinitionResolutionRegisteredEvent>(SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionRegistered)
            .On<SubWorkflowDefinitionResolvedEvent>(KeepCurrentState)
            .On<SubWorkflowDefinitionResolveFailedEvent>(KeepCurrentState)
            .On<SubWorkflowDefinitionResolutionTimeoutFiredEvent>(KeepCurrentState)
            .On<SubWorkflowDefinitionResolutionClearedEvent>(SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionCleared)
            .On<SubWorkflowBindingUpsertedEvent>(SubWorkflowOrchestrator.ApplySubWorkflowBindingUpserted)
            .On<SubWorkflowInvocationRegisteredEvent>(SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered)
            .On<SubWorkflowInvocationCompletedEvent>(SubWorkflowOrchestrator.ApplySubWorkflowInvocationCompleted)
            .OrCurrent();

    private WorkflowRunState ApplyBindWorkflowRunDefinition(WorkflowRunState current, BindWorkflowRunDefinitionEvent evt)
    {
        var next = current.Clone();
        next.DefinitionActorId = evt.DefinitionActorId?.Trim() ?? string.Empty;
        next.WorkflowYaml = evt.WorkflowYaml ?? string.Empty;
        next.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName)
            ? current.WorkflowName
            : evt.WorkflowName.Trim();
        next.RunId = string.IsNullOrWhiteSpace(evt.RunId)
            ? (string.IsNullOrWhiteSpace(current.RunId) ? Id : current.RunId)
            : WorkflowRunIdNormalizer.Normalize(evt.RunId);
        next.ScopeId = string.IsNullOrWhiteSpace(evt.ScopeId)
            ? current.ScopeId
            : evt.ScopeId.Trim();
        next.Status = "bound";
        next.Input = string.Empty;
        next.FinalOutput = string.Empty;
        next.FinalError = string.Empty;
        next.ExecutionStates.Clear();
        next.SubWorkflowBindings.Clear();
        next.PendingSubWorkflowDefinitionResolutions.Clear();
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Clear();
        next.PendingSubWorkflowInvocations.Clear();
        next.PendingSubWorkflowInvocationIndexByChildRunId.Clear();
        next.PendingChildRunIdsByParentRunId.Clear();
        next.LastCommandId = string.Empty;
        next.InlineWorkflowYamls.Clear();
        foreach (var (workflowNameKey, workflowYamlValue) in evt.InlineWorkflowYamls)
        {
            var normalizedWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(workflowNameKey);
            if (string.IsNullOrWhiteSpace(normalizedWorkflowName) ||
                string.IsNullOrWhiteSpace(workflowYamlValue))
            {
                continue;
            }

            next.InlineWorkflowYamls[normalizedWorkflowName] = workflowYamlValue;
        }

        var compileResult = EvaluateWorkflowCompilation(next.WorkflowYaml);
        next.Compiled = compileResult.Compiled;
        next.CompilationError = compileResult.CompilationError;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowRunExecutionStarted(WorkflowRunState current, WorkflowRunExecutionStartedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(evt.RunId) ? current.RunId : WorkflowRunIdNormalizer.Normalize(evt.RunId);
        next.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? current.WorkflowName : evt.WorkflowName.Trim();
        next.Input = evt.Input ?? string.Empty;
        next.Status = RunningStatus;
        next.FinalOutput = string.Empty;
        next.FinalError = string.Empty;
        if (string.IsNullOrWhiteSpace(next.DefinitionActorId) && !string.IsNullOrWhiteSpace(evt.DefinitionActorId))
            next.DefinitionActorId = evt.DefinitionActorId.Trim();
        if (string.IsNullOrWhiteSpace(next.ScopeId) && !string.IsNullOrWhiteSpace(evt.ScopeId))
            next.ScopeId = evt.ScopeId.Trim();
        return next;
    }

    private static WorkflowRunState ApplyWorkflowCommandObserved(WorkflowRunState current, WorkflowCommandObservedEvent evt)
    {
        var next = current.Clone();
        next.LastCommandId = evt.CommandId?.Trim() ?? string.Empty;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowExecutionStateUpserted(WorkflowRunState current, WorkflowExecutionStateUpsertedEvent evt)
    {
        var next = current.Clone();
        var scopeKey = evt.ScopeKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(scopeKey) || evt.State == null)
            return next;

        next.ExecutionStates[scopeKey] = evt.State;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowExecutionStateCleared(WorkflowRunState current, WorkflowExecutionStateClearedEvent evt)
    {
        var next = current.Clone();
        var scopeKey = evt.ScopeKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(scopeKey))
            return next;

        next.ExecutionStates.Remove(scopeKey);
        return next;
    }

    private static WorkflowRunState ApplyWorkflowStopped(WorkflowRunState current, WorkflowStoppedEvent evt)
    {
        var next = current.Clone();
        next.Status = StoppedStatus;
        next.FinalOutput = string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.Reason))
            next.FinalError = evt.Reason;
        next.ExecutionStates.Clear();
        next.PendingSubWorkflowDefinitionResolutions.Clear();
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Clear();
        next.PendingSubWorkflowInvocations.Clear();
        next.PendingSubWorkflowInvocationIndexByChildRunId.Clear();
        next.PendingChildRunIdsByParentRunId.Clear();
        return next;
    }

    private static WorkflowRunState ApplyWorkflowCompleted(WorkflowRunState current, WorkflowCompletedEvent evt)
    {
        var next = current.Clone();
        next.Status = evt.Success ? CompletedStatus : FailedStatus;
        next.FinalOutput = evt.Output ?? string.Empty;
        next.FinalError = evt.Error ?? string.Empty;
        next.PendingSubWorkflowDefinitionResolutions.Clear();
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Clear();
        return next;
    }

    private static WorkflowRunState ApplyWorkflowRunStopped(WorkflowRunState current, WorkflowRunStoppedEvent evt)
    {
        var next = current.Clone();
        next.Status = StoppedStatus;
        next.FinalOutput = string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.Reason))
            next.FinalError = evt.Reason;
        next.ExecutionStates.Clear();
        next.PendingSubWorkflowDefinitionResolutions.Clear();
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Clear();
        next.PendingSubWorkflowInvocations.Clear();
        next.PendingSubWorkflowInvocationIndexByChildRunId.Clear();
        next.PendingChildRunIdsByParentRunId.Clear();
        return next;
    }

    private static WorkflowRunState KeepCurrentState(WorkflowRunState current, SubWorkflowDefinitionResolvedEvent _) => current;

    private static WorkflowRunState KeepCurrentState(WorkflowRunState current, SubWorkflowDefinitionResolveFailedEvent _) => current;

    private static WorkflowRunState KeepCurrentState(WorkflowRunState current, SubWorkflowDefinitionResolutionTimeoutFiredEvent _) => current;

    private static bool IsTerminalStatus(string? status) =>
        string.Equals(status, CompletedStatus, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, FailedStatus, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, StoppedStatus, StringComparison.OrdinalIgnoreCase);

    private static string BuildStoppedMessage(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? "Workflow execution stopped."
            : $"Workflow execution stopped: {reason}";

    private bool TryPrepareStop(
        string? requestedRunId,
        string eventName,
        out string runId)
    {
        runId = string.IsNullOrWhiteSpace(requestedRunId)
            ? RunId
            : WorkflowRunIdNormalizer.Normalize(requestedRunId);
        if (!string.IsNullOrWhiteSpace(State.RunId) &&
            !string.Equals(State.RunId, runId, StringComparison.Ordinal))
        {
            Logger.LogWarning(
                "Ignore {EventName} with mismatched run id. actor={ActorId} stateRun={StateRunId} eventRun={EventRunId}",
                eventName,
                Id,
                State.RunId,
                runId);
            return false;
        }

        if (!IsTerminalStatus(State.Status))
            return true;

        Logger.LogInformation(
            "Ignore {EventName} for terminal run. actor={ActorId} run={RunId} status={Status}",
            eventName,
            Id,
            runId,
            State.Status);
        return false;
    }

    private async Task CompleteStopAsync(
        string runId,
        string? workflowName,
        string? reason,
        Func<CancellationToken, Task> persistAsync,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(persistAsync);

        var stateBeforeStop = State.Clone();
        await persistAsync(ct);
        await _subWorkflowOrchestrator.CancelPendingDefinitionResolutionTimeoutsAsync(stateBeforeStop, CancellationToken.None);
        await _subWorkflowOrchestrator.CleanupPendingInvocationsForRunAsync(runId, stateBeforeStop, CancellationToken.None);
        await CleanupRoleAgentTreeAsync(CancellationToken.None);
        _executionItems.Clear();
        DisableExecutionModules();

        Logger.LogInformation(
            "Workflow run {Name} stopped: run={RunId} reason={Reason}",
            workflowName,
            runId,
            string.IsNullOrWhiteSpace(reason) ? "(none)" : reason);

        await PublishAsync(new TextMessageEndEvent
        {
            Content = BuildStoppedMessage(reason),
        }, TopologyAudience.Parent);
    }

    private WorkflowCompilationResult EvaluateWorkflowCompilation(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return WorkflowCompilationResult.Invalid("workflow yaml is empty");

        try
        {
            var workflow = _parser.Parse(yaml);
            var errors = ValidateWorkflowDefinition(workflow);
            if (errors.Count > 0)
                return WorkflowCompilationResult.Invalid(string.Join("; ", errors));

            return WorkflowCompilationResult.Success(workflow);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "EvaluateWorkflowCompilation: parse/validation failed.");
            return WorkflowCompilationResult.Invalid(ex.Message);
        }
    }

    private void RebuildCompiledWorkflowCache()
    {
        if (string.IsNullOrWhiteSpace(State.WorkflowYaml))
        {
            _compiledWorkflow = null;
            return;
        }

        try
        {
            var workflow = _parser.Parse(State.WorkflowYaml);
            var errors = ValidateWorkflowDefinition(workflow);
            _compiledWorkflow = errors.Count == 0 ? workflow : null;
            if (errors.Count > 0)
            {
                Logger.LogWarning(
                    "RebuildCompiledWorkflowCache: workflow has validation errors. errors={Errors}",
                    string.Join("; ", errors));
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "RebuildCompiledWorkflowCache: parse failed.");
            _compiledWorkflow = null;
        }
    }

    private async Task<WorkflowCompilationResult> ReplaceWorkflowDefinitionBypassingBindingAsync(
        string workflowYaml,
        CancellationToken ct = default)
    {
        var childActorIdsToReset = CaptureDerivedChildActorIdsForReset();
        var stateBeforeBind = State.Clone();
        WorkflowDefinition parsed;
        try
        {
            parsed = _parser.Parse(workflowYaml);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ReplaceWorkflowDefinitionBypassingBinding: parse failed.");
            return WorkflowCompilationResult.Invalid(ex.Message);
        }

        var validationErrors = ValidateWorkflowDefinition(parsed);
        if (validationErrors.Count > 0)
            return WorkflowCompilationResult.Invalid(string.Join("; ", validationErrors));

        var workflowName = parsed.Name ?? string.Empty;
        await PersistDomainEventAsync(new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = State.DefinitionActorId ?? string.Empty,
            WorkflowName = workflowName,
            WorkflowYaml = workflowYaml,
            RunId = string.IsNullOrWhiteSpace(State.RunId) ? Id : State.RunId,
            ScopeId = State.ScopeId ?? string.Empty,
            InlineWorkflowYamls = { State.InlineWorkflowYamls },
        }, ct);
        await _subWorkflowOrchestrator.CancelPendingDefinitionResolutionTimeoutsAsync(stateBeforeBind, CancellationToken.None);
        RebuildCompiledWorkflowCache();
        await ResetDerivedRuntimeStateAsync(childActorIdsToReset, ct);
        InstallCognitiveModules();
        return WorkflowCompilationResult.Success(parsed);
    }

    private static string ResolveScopeId(
        string? requestedScopeId,
        Google.Protobuf.Collections.MapField<string, string>? metadata,
        string? fallbackScopeId)
    {
        if (!string.IsNullOrWhiteSpace(requestedScopeId))
            return requestedScopeId.Trim();

        if (metadata != null &&
            metadata.TryGetValue(WorkflowScopeIdMetadataKey, out var workflowScopeId) &&
            !string.IsNullOrWhiteSpace(workflowScopeId))
        {
            return workflowScopeId.Trim();
        }

        if (metadata != null &&
            metadata.TryGetValue("scope_id", out var legacyScopeId) &&
            !string.IsNullOrWhiteSpace(legacyScopeId))
        {
            return legacyScopeId.Trim();
        }

        return fallbackScopeId?.Trim() ?? string.Empty;
    }

    private IReadOnlyCollection<string> CaptureDerivedChildActorIdsForReset()
    {
        var childActorIds = new HashSet<string>(_childAgentIds, StringComparer.Ordinal);

        foreach (var roleActorId in CaptureRoleActorIdsFromCurrentDefinition())
        {
            if (!string.IsNullOrWhiteSpace(roleActorId))
                childActorIds.Add(roleActorId);
        }

        foreach (var binding in State.SubWorkflowBindings)
        {
            var childActorId = binding.ChildActorId?.Trim();
            if (!string.IsNullOrWhiteSpace(childActorId))
                childActorIds.Add(childActorId);
        }

        foreach (var pending in State.PendingSubWorkflowInvocations)
        {
            var childActorId = pending.ChildActorId?.Trim();
            if (!string.IsNullOrWhiteSpace(childActorId))
                childActorIds.Add(childActorId);
        }

        return childActorIds;
    }

    private IReadOnlyCollection<string> CaptureRoleActorIdsFromCurrentDefinition()
    {
        var roleActorIds = new HashSet<string>(StringComparer.Ordinal);
        var currentWorkflow = _compiledWorkflow;
        if (currentWorkflow == null && !string.IsNullOrWhiteSpace(State.WorkflowYaml))
        {
            try
            {
                currentWorkflow = _parser.Parse(State.WorkflowYaml);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to parse current workflow while capturing role actor ids for reset.");
            }
        }

        if (currentWorkflow == null)
            return roleActorIds;

        foreach (var role in currentWorkflow.Roles)
        {
            if (string.IsNullOrWhiteSpace(role.Id))
                continue;

            roleActorIds.Add(BuildChildActorId(role.Id));
        }

        return roleActorIds;
    }

    private async Task ResetDerivedRuntimeStateAsync(
        IReadOnlyCollection<string> childActorIds,
        CancellationToken ct)
    {
        _executionItems.Clear();
        foreach (var childActorId in childActorIds)
        {
            await _runtime.UnlinkAsync(childActorId, ct);
            await _runtime.DestroyAsync(childActorId, ct);
        }

        _childAgentIds.Clear();
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
                $"WorkflowRunGAgent '{Id}' is already bound to workflow '{State.WorkflowName}' and cannot switch to '{workflowName}'.");
        }
    }

    private readonly record struct WorkflowCompilationResult(bool Compiled, string CompilationError, WorkflowDefinition? Workflow)
    {
        public static WorkflowCompilationResult Success(WorkflowDefinition workflow) =>
            new(true, string.Empty, workflow);

        public static WorkflowCompilationResult Invalid(string error) =>
            new(false, error ?? string.Empty, null);
    }

    private List<string> ValidateWorkflowDefinition(WorkflowDefinition workflow) =>
        WorkflowRunDefinitionValidationSupport.Validate(workflow, _knownModuleStepTypes, _stepExecutorFactory);
}
