using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowRunGAgent
    : GAgentBase<WorkflowRunState>,
      IWorkflowExecutionStateHost
{
    private const string RunningStatus = "running";
    private const string CompletedStatus = "completed";
    private const string FailedStatus = "failed";

    private WorkflowDefinition? _compiledWorkflow;
    private readonly WorkflowParser _parser = new();
    private readonly List<string> _childAgentIds = [];
    private readonly IActorRuntime _runtime;
    private readonly IRoleAgentTypeResolver _roleAgentTypeResolver;
    private readonly IEventModuleFactory<IWorkflowExecutionContext> _stepExecutorFactory;
    private readonly IWorkflowDefinitionResolver? _workflowDefinitionResolver;
    private readonly IReadOnlyList<IWorkflowModuleDependencyExpander> _moduleDependencyExpanders;
    private readonly IReadOnlyList<IWorkflowModuleConfigurator> _moduleConfigurators;
    private readonly ISet<string> _knownModuleStepTypes;
    private readonly SubWorkflowOrchestrator _subWorkflowOrchestrator;

    public WorkflowRunGAgent(
        IActorRuntime runtime,
        IRoleAgentTypeResolver roleAgentTypeResolver,
        IEventModuleFactory<IWorkflowExecutionContext> stepExecutorFactory,
        IEnumerable<IWorkflowModulePack> modulePacks,
        IWorkflowDefinitionResolver? workflowDefinitionResolver = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _roleAgentTypeResolver = roleAgentTypeResolver ?? throw new ArgumentNullException(nameof(roleAgentTypeResolver));
        _stepExecutorFactory = stepExecutorFactory ?? throw new ArgumentNullException(nameof(stepExecutorFactory));
        _workflowDefinitionResolver = workflowDefinitionResolver;

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
            _workflowDefinitionResolver,
            () => Services,
            () => Id,
            () => Logger,
            (evt, token) => PersistDomainEventAsync(evt, token),
            (events, token) => PersistDomainEventsAsync(events, token),
            (evt, direction, token) => PublishAsync(evt, direction, token),
            (actorId, evt) => SendToAsync(actorId, evt));
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
        CancellationToken ct = default)
    {
        EnsureWorkflowNameCanBind(workflowName);
        var bindDefinitionEvent = new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = definitionActorId ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
            WorkflowYaml = workflowYaml ?? string.Empty,
            RunId = string.IsNullOrWhiteSpace(runId) ? Id : WorkflowRunIdNormalizer.Normalize(runId),
        };
        if (inlineWorkflowYamls != null)
        {
            foreach (var (key, value) in inlineWorkflowYamls)
                bindDefinitionEvent.InlineWorkflowYamls[key] = value;
        }

        await PersistDomainEventAsync(bindDefinitionEvent, ct);
        RebuildCompiledWorkflowCache();
        _childAgentIds.Clear();
        InstallCognitiveModules();
    }

    [EventHandler]
    public Task HandleBindWorkflowRunDefinition(BindWorkflowRunDefinitionEvent request) =>
        BindWorkflowRunDefinitionAsync(
            request.DefinitionActorId,
            request.WorkflowYaml,
            request.WorkflowName,
            request.InlineWorkflowYamls,
            request.RunId);

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
            }, EventDirection.Up);
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
            Input = request.Prompt ?? string.Empty,
            DefinitionActorId = State.DefinitionActorId ?? string.Empty,
        });

        await PublishAsync(new StartWorkflowEvent
        {
            WorkflowName = _compiledWorkflow.Name,
            Input = request.Prompt,
            RunId = runId,
        }, EventDirection.Self);
    }

    [EventHandler]
    public async Task HandleReplaceWorkflowDefinitionAndExecute(ReplaceWorkflowDefinitionAndExecuteEvent request)
    {
        var yaml = request.WorkflowYaml ?? string.Empty;
        if (string.IsNullOrWhiteSpace(yaml))
        {
            Logger.LogWarning("ReplaceWorkflowDefinitionAndExecute: empty workflow YAML, ignoring.");
            await PublishAsync(new ChatResponseEvent { Content = "Dynamic workflow YAML is empty." }, EventDirection.Up);
            return;
        }

        var replaceResult = await ReplaceWorkflowDefinitionBypassingBindingAsync(yaml);
        if (!replaceResult.Compiled || _compiledWorkflow == null)
        {
            var reason = string.IsNullOrWhiteSpace(replaceResult.CompilationError)
                ? "Dynamic workflow YAML compilation failed."
                : $"Dynamic workflow YAML compilation failed: {replaceResult.CompilationError}";
            Logger.LogWarning("ReplaceWorkflowDefinitionAndExecute: YAML compilation failed. Error={Error}", replaceResult.CompilationError);
            await PublishAsync(new ChatResponseEvent { Content = reason }, EventDirection.Up);
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
        });

        await PublishAsync(new StartWorkflowEvent
        {
            WorkflowName = _compiledWorkflow.Name,
            Input = request.Input ?? string.Empty,
            RunId = runId,
        }, EventDirection.Self);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleSubWorkflowInvokeRequested(SubWorkflowInvokeRequestedEvent request)
    {
        await _subWorkflowOrchestrator.HandleInvokeRequestedAsync(request, State, CancellationToken.None);
    }

    [AllEventHandler(Priority = 50, AllowSelfHandling = true)]
    public async Task HandleWorkflowCompletionEnvelope(EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(WorkflowCompletedEvent.Descriptor) != true)
            return;

        var completed = envelope.Payload.Unpack<WorkflowCompletedEvent>();
        if (await _subWorkflowOrchestrator.TryHandleCompletionAsync(
                completed,
                envelope.PublisherId,
                State,
                CancellationToken.None))
        {
            return;
        }

        if (!string.Equals(envelope.PublisherId, Id, StringComparison.Ordinal))
        {
            Logger.LogDebug(
                "Ignore external WorkflowCompletedEvent from publisher={PublisherId} run={RunId}.",
                envelope.PublisherId,
                completed.RunId);
            return;
        }

        await HandleWorkflowCompleted(completed);
    }

    public async Task HandleWorkflowCompleted(WorkflowCompletedEvent evt)
    {
        await PersistDomainEventAsync(evt);
        await _subWorkflowOrchestrator.CleanupPendingInvocationsForRunAsync(evt.RunId, State, CancellationToken.None);
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
        }, EventDirection.Up);
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
            var childActorId = BuildChildActorId(role.Id);
            var actor = await _runtime.GetAsync(childActorId)
                        ?? await _runtime.CreateAsync(roleAgentType, childActorId);
            await _runtime.LinkAsync(Id, actor.Id);

            await actor.HandleEventAsync(CreateRoleAgentInitializeEnvelope(role));
            _childAgentIds.Add(actor.Id);
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
            .On<WorkflowRunExecutionStartedEvent>(ApplyWorkflowRunExecutionStarted)
            .On<WorkflowExecutionStateUpsertedEvent>(ApplyWorkflowExecutionStateUpserted)
            .On<WorkflowExecutionStateClearedEvent>(ApplyWorkflowExecutionStateCleared)
            .On<WorkflowCompletedEvent>(ApplyWorkflowCompleted)
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
        if (string.IsNullOrWhiteSpace(next.Status))
            next.Status = "bound";
        if (compileResult.Compiled && compileResult.Workflow != null)
            SubWorkflowOrchestrator.PruneIdleSubWorkflowBindings(next, compileResult.Workflow);
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

    private static WorkflowRunState ApplyWorkflowCompleted(WorkflowRunState current, WorkflowCompletedEvent evt)
    {
        var next = current.Clone();
        next.Status = evt.Success ? CompletedStatus : FailedStatus;
        next.FinalOutput = evt.Output ?? string.Empty;
        next.FinalError = evt.Error ?? string.Empty;
        return next;
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
            InlineWorkflowYamls = { State.InlineWorkflowYamls },
        }, ct);
        RebuildCompiledWorkflowCache();
        _childAgentIds.Clear();
        InstallCognitiveModules();
        return WorkflowCompilationResult.Success(parsed);
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

    private List<string> ValidateWorkflowDefinition(WorkflowDefinition workflow)
    {
        var knownStepTypes = new HashSet<string>(_knownModuleStepTypes, StringComparer.OrdinalIgnoreCase);
        knownStepTypes.UnionWith(WorkflowPrimitiveCatalog.BuiltInCanonicalTypes);
        ExpandKnownStepTypesFromFactory(workflow, knownStepTypes);

        return WorkflowValidator.Validate(
            workflow,
            new WorkflowValidator.WorkflowValidationOptions
            {
                RequireKnownStepTypes = true,
                KnownStepTypes = knownStepTypes,
            },
            availableWorkflowNames: null);
    }

    private void ExpandKnownStepTypesFromFactory(WorkflowDefinition workflow, ISet<string> knownStepTypes)
    {
        foreach (var stepType in EnumerateReferencedStepTypes(workflow.Steps))
        {
            var canonical = WorkflowPrimitiveCatalog.ToCanonicalType(stepType);
            if (string.IsNullOrWhiteSpace(canonical) || knownStepTypes.Contains(canonical))
                continue;

            if (_stepExecutorFactory.TryCreate(canonical, out _))
                knownStepTypes.Add(canonical);
        }
    }

    private static IEnumerable<string> EnumerateReferencedStepTypes(IEnumerable<StepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step.Type;

            foreach (var (key, value) in step.Parameters)
            {
                if (WorkflowPrimitiveCatalog.IsStepTypeParameterKey(key) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }

            if (step.Children is { Count: > 0 })
            {
                foreach (var childType in EnumerateReferencedStepTypes(step.Children))
                    yield return childType;
            }
        }
    }

    private EventEnvelope CreateRoleAgentInitializeEnvelope(RoleDefinition role)
    {
        var initialize = new InitializeRoleAgentEvent
        {
            RoleName = role.Name ?? string.Empty,
            ProviderName = string.IsNullOrWhiteSpace(role.Provider) ? string.Empty : role.Provider,
            Model = string.IsNullOrWhiteSpace(role.Model) ? string.Empty : role.Model,
            SystemPrompt = role.SystemPrompt ?? string.Empty,
            MaxTokens = role.MaxTokens ?? 0,
            MaxToolRounds = role.MaxToolRounds ?? 0,
            MaxHistoryMessages = role.MaxHistoryMessages ?? 0,
            StreamBufferCapacity = role.StreamBufferCapacity ?? 0,
            EventModules = role.EventModules ?? string.Empty,
            EventRoutes = role.EventRoutes ?? string.Empty,
        };

        if (role.Temperature.HasValue)
            initialize.Temperature = role.Temperature.Value;

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(initialize),
            PublisherId = Id,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
    }
}
