using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// Encapsulates workflow_call runtime orchestration for <see cref="WorkflowRunGAgent"/>.
/// Keeps sub-workflow actor lifecycle and state transition helpers out of the run actor.
/// </summary>
internal sealed class SubWorkflowOrchestrator
{
    private const string WorkflowCallMetadataPrefix = "workflow_call.";
    private const string WorkflowCallInvocationIdMetadataKey = WorkflowCallMetadataPrefix + "invocation_id";
    private const string WorkflowCallWorkflowNameMetadataKey = WorkflowCallMetadataPrefix + "workflow_name";
    private const string WorkflowCallLifecycleMetadataKey = WorkflowCallMetadataPrefix + "lifecycle";
    private const string WorkflowCallChildActorIdMetadataKey = WorkflowCallMetadataPrefix + "child_actor_id";
    private const string WorkflowCallChildRunIdMetadataKey = WorkflowCallMetadataPrefix + "child_run_id";
    private const string WorkflowCallParentRunIdMetadataKey = WorkflowCallMetadataPrefix + "parent_run_id";
    private const string WorkflowCallParentStepIdMetadataKey = WorkflowCallMetadataPrefix + "parent_step_id";

    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IWorkflowDefinitionResolver? _workflowDefinitionResolver;
    private readonly Func<IServiceProvider?> _serviceProviderAccessor;
    private readonly Func<string> _ownerActorIdAccessor;
    private readonly Func<ILogger> _loggerAccessor;
    private readonly Func<IMessage, CancellationToken, Task> _persistDomainEventAsync;
    private readonly Func<IReadOnlyList<IMessage>, CancellationToken, Task> _persistDomainEventsAsync;
    private readonly Func<IMessage, BroadcastDirection, CancellationToken, Task> _publishAsync;
    private readonly Func<string, IMessage, Task> _sendToAsync;

    public SubWorkflowOrchestrator(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IWorkflowDefinitionResolver? workflowDefinitionResolver,
        Func<IServiceProvider?> serviceProviderAccessor,
        Func<string> ownerActorIdAccessor,
        Func<ILogger> loggerAccessor,
        Func<IMessage, CancellationToken, Task> persistDomainEventAsync,
        Func<IReadOnlyList<IMessage>, CancellationToken, Task> persistDomainEventsAsync,
        Func<IMessage, BroadcastDirection, CancellationToken, Task> publishAsync,
        Func<string, IMessage, Task> sendToAsync)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _workflowDefinitionResolver = workflowDefinitionResolver;
        _serviceProviderAccessor = serviceProviderAccessor ?? throw new ArgumentNullException(nameof(serviceProviderAccessor));
        _ownerActorIdAccessor = ownerActorIdAccessor ?? throw new ArgumentNullException(nameof(ownerActorIdAccessor));
        _loggerAccessor = loggerAccessor ?? throw new ArgumentNullException(nameof(loggerAccessor));
        _persistDomainEventAsync = persistDomainEventAsync ?? throw new ArgumentNullException(nameof(persistDomainEventAsync));
        _persistDomainEventsAsync = persistDomainEventsAsync ?? throw new ArgumentNullException(nameof(persistDomainEventsAsync));
        _publishAsync = publishAsync ?? throw new ArgumentNullException(nameof(publishAsync));
        _sendToAsync = sendToAsync ?? throw new ArgumentNullException(nameof(sendToAsync));
    }

    public async Task HandleInvokeRequestedAsync(
        SubWorkflowInvokeRequestedEvent request,
        WorkflowRunState state,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(state);

        var parentRunId = WorkflowRunIdNormalizer.Normalize(request.ParentRunId);
        var parentStepId = request.ParentStepId?.Trim() ?? string.Empty;
        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(request.WorkflowName);
        if (string.IsNullOrWhiteSpace(parentStepId))
        {
            _loggerAccessor().LogWarning(
                "workflow_call invocation failed: missing parent step id. parentRun={ParentRunId}",
                parentRunId);
            await PublishWorkflowCallFailureAsync(parentStepId, parentRunId, "workflow_call missing parent_step_id", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(workflowName))
        {
            await PublishWorkflowCallFailureAsync(parentStepId, parentRunId, "workflow_call missing workflow parameter", ct);
            return;
        }

        if (!WorkflowCallLifecycle.IsSupported(request.Lifecycle))
        {
            await PublishWorkflowCallFailureAsync(
                parentStepId,
                parentRunId,
                $"workflow_call lifecycle must be {WorkflowCallLifecycle.AllowedValuesText}",
                ct);
            return;
        }

        var lifecycle = WorkflowCallLifecycle.Normalize(request.Lifecycle);
        var invocationId = string.IsNullOrWhiteSpace(request.InvocationId)
            ? WorkflowCallInvocationIdFactory.Build(parentRunId, parentStepId)
            : request.InvocationId.Trim();
        var childRunId = invocationId;

        try
        {
            var childActor = await ResolveOrCreateSubWorkflowActorAsync(
                workflowName,
                lifecycle,
                state,
                childRunId,
                ct);

            await _persistDomainEventAsync(new SubWorkflowInvocationRegisteredEvent
            {
                InvocationId = invocationId,
                ParentRunId = parentRunId,
                ParentStepId = parentStepId,
                WorkflowName = workflowName,
                ChildActorId = childActor.Id,
                ChildRunId = childRunId,
                Lifecycle = lifecycle,
            }, ct);

            var start = new StartWorkflowEvent
            {
                WorkflowName = workflowName,
                Input = request.Input ?? string.Empty,
                RunId = childRunId,
            };
            start.Parameters[WorkflowCallInvocationIdMetadataKey] = invocationId;
            start.Parameters[WorkflowCallParentRunIdMetadataKey] = parentRunId;
            start.Parameters[WorkflowCallParentStepIdMetadataKey] = parentStepId;
            start.Parameters[WorkflowCallWorkflowNameMetadataKey] = workflowName;
            start.Parameters[WorkflowCallLifecycleMetadataKey] = lifecycle;

            await _sendToAsync(childActor.Id, start);
        }
        catch (Exception ex)
        {
            _loggerAccessor().LogWarning(
                ex,
                "workflow_call failed: workflow={WorkflowName} parentRun={ParentRunId} parentStep={ParentStepId}",
                workflowName,
                parentRunId,
                parentStepId);
            await PublishWorkflowCallFailureAsync(
                parentStepId,
                parentRunId,
                $"workflow_call invocation failed: {ex.Message}",
                ct);
        }
    }

    public async Task<bool> TryHandleCompletionAsync(
        WorkflowCompletedEvent completed,
        string? publisherActorId,
        WorkflowRunState state,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(state);

        var childRunId = completed.RunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(childRunId))
            return false;

        if (!TryGetPendingInvocationByChildRunId(state, childRunId, out var pending))
            return false;

        var expectedChildActorId = pending.ChildActorId?.Trim() ?? string.Empty;
        var completionPublisherId = publisherActorId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(expectedChildActorId) &&
            !string.Equals(expectedChildActorId, completionPublisherId, StringComparison.Ordinal))
        {
            _loggerAccessor().LogWarning(
                "Ignore workflow_call completion due to publisher mismatch. childRun={ChildRunId} expectedPublisher={ExpectedPublisherId} actualPublisher={ActualPublisherId}",
                childRunId,
                expectedChildActorId,
                completionPublisherId);
            return true;
        }

        if (string.IsNullOrWhiteSpace(expectedChildActorId))
        {
            _loggerAccessor().LogWarning(
                "workflow_call completion matched by child run only because child actor id is missing. childRun={ChildRunId} publisher={PublisherId}",
                childRunId,
                completionPublisherId);
        }

        await _persistDomainEventAsync(new SubWorkflowInvocationCompletedEvent
        {
            InvocationId = pending.InvocationId,
            ChildRunId = childRunId,
            Success = completed.Success,
            Output = completed.Output ?? string.Empty,
            Error = completed.Error ?? string.Empty,
        }, ct);

        var parentCompleted = new StepCompletedEvent
        {
            StepId = pending.ParentStepId,
            RunId = pending.ParentRunId,
            Success = completed.Success,
            Output = completed.Output,
            Error = completed.Error,
        };
        parentCompleted.Annotations[WorkflowCallInvocationIdMetadataKey] = pending.InvocationId;
        parentCompleted.Annotations[WorkflowCallWorkflowNameMetadataKey] = pending.WorkflowName;
        parentCompleted.Annotations[WorkflowCallLifecycleMetadataKey] = WorkflowCallLifecycle.Normalize(pending.Lifecycle);
        parentCompleted.Annotations[WorkflowCallChildActorIdMetadataKey] = pending.ChildActorId;
        parentCompleted.Annotations[WorkflowCallChildRunIdMetadataKey] = childRunId;

        await _publishAsync(parentCompleted, BroadcastDirection.Self, ct);
        await TryFinalizeNonSingletonChildAsync(pending, ct);
        return true;
    }

    public async Task CleanupPendingInvocationsForRunAsync(string runId, WorkflowRunState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        if (string.IsNullOrWhiteSpace(normalizedRunId))
            return;

        var staleInvocations = CollectPendingInvocationsByParentRunId(state, normalizedRunId);
        var cleanupEvents = staleInvocations
            .Select(pending => (IMessage)new SubWorkflowInvocationCompletedEvent
            {
                InvocationId = pending.InvocationId,
                ChildRunId = pending.ChildRunId,
                Success = false,
                Error = "parent workflow completed before child workflow completion",
            })
            .ToList();

        if (cleanupEvents.Count == 0)
            return;

        await _persistDomainEventsAsync(cleanupEvents, ct);

        foreach (var staleInvocation in staleInvocations)
            await TryFinalizeNonSingletonChildAsync(staleInvocation, ct);
    }

    public static WorkflowRunState ApplySubWorkflowBindingUpserted(WorkflowRunState current, SubWorkflowBindingUpsertedEvent evt)
    {
        var next = current.Clone();
        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(evt.WorkflowName);
        var childActorId = evt.ChildActorId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(workflowName) || string.IsNullOrWhiteSpace(childActorId))
            return next;

        var lifecycle = WorkflowCallLifecycle.Normalize(evt.Lifecycle);
        for (var i = 0; i < next.SubWorkflowBindings.Count; i++)
        {
            var existing = next.SubWorkflowBindings[i];
            if (string.Equals(existing.WorkflowName, workflowName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(WorkflowCallLifecycle.Normalize(existing.Lifecycle), lifecycle, StringComparison.OrdinalIgnoreCase))
            {
                next.SubWorkflowBindings[i] = new WorkflowRunState.Types.SubWorkflowBinding
                {
                    WorkflowName = workflowName,
                    ChildActorId = childActorId,
                    Lifecycle = lifecycle,
                };
                return next;
            }
        }

        next.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = workflowName,
            ChildActorId = childActorId,
            Lifecycle = lifecycle,
        });
        return next;
    }

    public static WorkflowRunState ApplySubWorkflowInvocationRegistered(WorkflowRunState current, SubWorkflowInvocationRegisteredEvent evt)
    {
        var next = current.Clone();
        var invocationId = evt.InvocationId?.Trim() ?? string.Empty;
        var childRunId = evt.ChildRunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(invocationId) || string.IsNullOrWhiteSpace(childRunId))
            return next;

        var pending = new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = invocationId,
            ParentRunId = WorkflowRunIdNormalizer.Normalize(evt.ParentRunId),
            ParentStepId = evt.ParentStepId?.Trim() ?? string.Empty,
            WorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(evt.WorkflowName),
            ChildActorId = evt.ChildActorId?.Trim() ?? string.Empty,
            ChildRunId = childRunId,
            Lifecycle = WorkflowCallLifecycle.Normalize(evt.Lifecycle),
        };
        RemovePendingInvocation(next, invocationId, childRunId);
        AddPendingInvocation(next, pending);
        return next;
    }

    public static WorkflowRunState ApplySubWorkflowInvocationCompleted(WorkflowRunState current, SubWorkflowInvocationCompletedEvent evt)
    {
        var next = current.Clone();
        RemovePendingInvocation(
            next,
            evt.InvocationId?.Trim() ?? string.Empty,
            evt.ChildRunId?.Trim() ?? string.Empty);
        return next;
    }

    public static void PruneIdleSubWorkflowBindings(WorkflowRunState state, WorkflowDefinition workflow)
    {
        if (state.SubWorkflowBindings.Count == 0)
            return;

        var referencedSingletonWorkflows = CollectReferencedSingletonWorkflowNames(workflow.Steps);
        for (var i = state.SubWorkflowBindings.Count - 1; i >= 0; i--)
        {
            if (ShouldEvictBinding(state, state.SubWorkflowBindings[i], referencedSingletonWorkflows))
                state.SubWorkflowBindings.RemoveAt(i);
        }
    }

    private async Task<IActor> ResolveOrCreateSubWorkflowActorAsync(
        string workflowName,
        string lifecycle,
        WorkflowRunState state,
        string childRunId,
        CancellationToken ct)
    {
        var normalizedWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(workflowName);
        var normalizedLifecycle = WorkflowCallLifecycle.Normalize(lifecycle);
        if (!string.Equals(normalizedLifecycle, WorkflowCallLifecycle.Singleton, StringComparison.OrdinalIgnoreCase))
        {
            return await CreateSubWorkflowActorAsync(
                normalizedWorkflowName,
                normalizedLifecycle,
                state,
                childRunId,
                persistBinding: false,
                ct);
        }

        var existingBinding = state.SubWorkflowBindings.FirstOrDefault(x =>
            string.Equals(x.WorkflowName, normalizedWorkflowName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(WorkflowCallLifecycle.Normalize(x.Lifecycle), normalizedLifecycle, StringComparison.OrdinalIgnoreCase));

        if (existingBinding != null && !string.IsNullOrWhiteSpace(existingBinding.ChildActorId))
        {
            var existingActorId = existingBinding.ChildActorId.Trim();
            if (await _runtime.ExistsAsync(existingActorId))
            {
                var existingActor = await _runtime.GetAsync(existingActorId);
                if (existingActor != null)
                    return existingActor;
            }
        }

        return await CreateSubWorkflowActorAsync(
            normalizedWorkflowName,
            normalizedLifecycle,
            state,
            childRunId,
            persistBinding: true,
            ct);
    }

    private async Task<IActor> CreateSubWorkflowActorAsync(
        string workflowName,
        string lifecycle,
        WorkflowRunState state,
        string childRunId,
        bool persistBinding,
        CancellationToken ct)
    {
        var workflowYaml = await ResolveWorkflowYamlAsync(workflowName, state, ct);
        if (string.IsNullOrWhiteSpace(workflowYaml))
            throw new InvalidOperationException($"workflow_call references unregistered workflow '{workflowName}'");

        var childActorId = BuildSubWorkflowActorId(workflowName, lifecycle);
        var childActor = await ResolveOrCreateWorkflowActorByIdAsync(childActorId);
        await _runtime.LinkAsync(_ownerActorIdAccessor(), childActor.Id);
        await _dispatchPort.DispatchAsync(
            childActor.Id,
            CreateWorkflowRunBindEnvelope(workflowYaml, workflowName, childRunId, state),
            ct);

        if (persistBinding)
        {
            await _persistDomainEventAsync(new SubWorkflowBindingUpsertedEvent
            {
                WorkflowName = workflowName,
                ChildActorId = childActor.Id,
                Lifecycle = lifecycle,
            }, ct);
        }

        return childActor;
    }

    private async Task<string?> ResolveWorkflowYamlAsync(string workflowName, WorkflowRunState state, CancellationToken ct)
    {
        var inlineYaml = TryResolveInlineWorkflowYaml(workflowName, state);
        if (!string.IsNullOrWhiteSpace(inlineYaml))
            return inlineYaml;

        var resolver = ResolveWorkflowDefinitionResolver();
        if (resolver == null)
        {
            throw new InvalidOperationException(
                "workflow_call requires IWorkflowDefinitionResolver service registration.");
        }

        return await resolver.GetWorkflowYamlAsync(workflowName, ct);
    }

    private IWorkflowDefinitionResolver? ResolveWorkflowDefinitionResolver() =>
        _workflowDefinitionResolver ?? _serviceProviderAccessor()?.GetService<IWorkflowDefinitionResolver>();

    private static string? TryResolveInlineWorkflowYaml(string workflowName, WorkflowRunState state)
    {
        if (state.InlineWorkflowYamls.Count == 0)
            return null;

        foreach (var (registeredName, yaml) in state.InlineWorkflowYamls)
        {
            if (string.Equals(registeredName, workflowName, StringComparison.OrdinalIgnoreCase))
                return yaml;
        }

        return null;
    }

    private async Task PublishWorkflowCallFailureAsync(
        string parentStepId,
        string parentRunId,
        string error,
        CancellationToken ct)
    {
        await _publishAsync(new StepCompletedEvent
        {
            StepId = parentStepId ?? string.Empty,
            RunId = parentRunId ?? string.Empty,
            Success = false,
            Error = error ?? "workflow_call invocation failed",
        }, BroadcastDirection.Self, ct);
    }

    private async Task TryFinalizeNonSingletonChildAsync(
        WorkflowRunState.Types.PendingSubWorkflowInvocation pending,
        CancellationToken ct)
    {
        if (string.Equals(
                WorkflowCallLifecycle.Normalize(pending.Lifecycle),
                WorkflowCallLifecycle.Singleton,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(pending.ChildActorId))
            return;

        try
        {
            await _runtime.UnlinkAsync(pending.ChildActorId, ct);
            await _runtime.DestroyAsync(pending.ChildActorId, ct);
        }
        catch (Exception ex)
        {
            _loggerAccessor().LogWarning(
                ex,
                "Ignore non-singleton child cleanup failure for actor {ChildActorId}.",
                pending.ChildActorId);
        }
    }

    private string BuildSubWorkflowActorId(string workflowName, string lifecycle)
    {
        var workflowSegment = SanitizeActorSegment(workflowName);
        if (!string.Equals(lifecycle, WorkflowCallLifecycle.Singleton, StringComparison.OrdinalIgnoreCase))
            return $"{_ownerActorIdAccessor()}:workflow:{workflowSegment}:{Guid.NewGuid():N}";

        return $"{_ownerActorIdAccessor()}:workflow:{workflowSegment}";
    }

    private async Task<IActor> ResolveOrCreateWorkflowActorByIdAsync(string actorId)
    {
        var existing = await _runtime.GetAsync(actorId);
        if (existing != null)
            return existing;

        try
        {
            return await _runtime.CreateAsync(typeof(WorkflowRunGAgent), actorId);
        }
        catch (Exception ex)
        {
            var raced = await _runtime.GetAsync(actorId);
            if (raced != null)
            {
                _loggerAccessor().LogDebug(
                    ex,
                    "Ignore create race for workflow child actor {ActorId}; using existing actor instance.",
                    actorId);
                return raced;
            }

            throw new InvalidOperationException(
                $"workflow_call failed to create or get sub-workflow actor '{actorId}'.",
                ex);
        }
    }

    private EventEnvelope CreateWorkflowRunBindEnvelope(
        string workflowYaml,
        string workflowName,
        string runId,
        WorkflowRunState state)
    {
        var inlineWorkflowYamls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in state.InlineWorkflowYamls)
            inlineWorkflowYamls[key] = value;

        var bindDefinition = new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = string.Empty,
            WorkflowYaml = workflowYaml ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
            RunId = runId ?? string.Empty,
            InlineWorkflowYamls = { inlineWorkflowYamls },
        };

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(bindDefinition),
            Route = EnvelopeRouteSemantics.CreateBroadcast(_ownerActorIdAccessor(), BroadcastDirection.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };
    }

    private static bool ShouldEvictBinding(
        WorkflowRunState state,
        WorkflowRunState.Types.SubWorkflowBinding binding,
        ISet<string> referencedSingletonWorkflows)
    {
        var lifecycle = WorkflowCallLifecycle.Normalize(binding.Lifecycle);
        if (!string.Equals(lifecycle, WorkflowCallLifecycle.Singleton, StringComparison.OrdinalIgnoreCase))
            return true;

        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(binding.WorkflowName);
        if (string.IsNullOrWhiteSpace(workflowName))
            return true;
        if (referencedSingletonWorkflows.Contains(workflowName))
            return false;

        var childActorId = binding.ChildActorId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(childActorId) &&
            HasPendingInvocationForChildActor(state.PendingSubWorkflowInvocations, childActorId))
        {
            return false;
        }

        return true;
    }

    private static bool HasPendingInvocationForChildActor(
        IEnumerable<WorkflowRunState.Types.PendingSubWorkflowInvocation> pendingInvocations,
        string childActorId)
    {
        return pendingInvocations.Any(x =>
            string.Equals(x.ChildActorId, childActorId, StringComparison.Ordinal));
    }

    private static ISet<string> CollectReferencedSingletonWorkflowNames(IEnumerable<StepDefinition> steps)
    {
        var workflows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectReferencedSingletonWorkflowNamesRecursive(steps, workflows);
        return workflows;
    }

    private static void CollectReferencedSingletonWorkflowNamesRecursive(
        IEnumerable<StepDefinition> steps,
        ISet<string> workflows)
    {
        foreach (var step in steps)
        {
            var canonicalStepType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
            if (string.Equals(canonicalStepType, "workflow_call", StringComparison.OrdinalIgnoreCase))
            {
                var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(step.Parameters.GetValueOrDefault("workflow", string.Empty));
                var lifecycle = WorkflowCallLifecycle.Normalize(step.Parameters.GetValueOrDefault("lifecycle", string.Empty));
                if (!string.IsNullOrWhiteSpace(workflowName) &&
                    string.Equals(lifecycle, WorkflowCallLifecycle.Singleton, StringComparison.OrdinalIgnoreCase))
                {
                    workflows.Add(workflowName);
                }
            }

            if (step.Children is { Count: > 0 })
                CollectReferencedSingletonWorkflowNamesRecursive(step.Children, workflows);
        }
    }

    private static string SanitizeActorSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "workflow";

        var trimmed = value.Trim();
        return string.Create(trimmed.Length, trimmed, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var ch = source[i];
                span[i] = char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-';
            }
        });
    }

    private static List<WorkflowRunState.Types.PendingSubWorkflowInvocation> CollectPendingInvocationsByParentRunId(
        WorkflowRunState state,
        string parentRunId)
    {
        var pendingByRun = new List<WorkflowRunState.Types.PendingSubWorkflowInvocation>();
        var indexedChildRunIds = new HashSet<string>(StringComparer.Ordinal);
        if (state.PendingChildRunIdsByParentRunId.TryGetValue(parentRunId, out var childRunIdSet) &&
            childRunIdSet.ChildRunIds.Count > 0)
        {
            foreach (var childRunId in childRunIdSet.ChildRunIds)
            {
                if (TryGetPendingInvocationByChildRunId(state, childRunId, out var pending))
                {
                    pendingByRun.Add(pending);
                    indexedChildRunIds.Add(pending.ChildRunId);
                }
            }
        }

        foreach (var pending in state.PendingSubWorkflowInvocations)
        {
            if (!string.Equals(pending.ParentRunId, parentRunId, StringComparison.Ordinal))
                continue;

            if (indexedChildRunIds.Contains(pending.ChildRunId))
                continue;

            pendingByRun.Add(pending);
            indexedChildRunIds.Add(pending.ChildRunId);
        }

        return pendingByRun;
    }

    private static bool TryGetPendingInvocationByChildRunId(
        WorkflowRunState state,
        string childRunId,
        out WorkflowRunState.Types.PendingSubWorkflowInvocation pending)
    {
        if (TryGetPendingInvocationIndexByChildRunId(state, childRunId, out var index))
        {
            pending = state.PendingSubWorkflowInvocations[index];
            return true;
        }

        pending = null!;
        return false;
    }

    private static bool TryGetPendingInvocationIndexByChildRunId(
        WorkflowRunState state,
        string childRunId,
        out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(childRunId))
            return false;

        if (state.PendingSubWorkflowInvocationIndexByChildRunId.TryGetValue(childRunId, out var mappedIndex) &&
            mappedIndex >= 0 &&
            mappedIndex < state.PendingSubWorkflowInvocations.Count &&
            string.Equals(state.PendingSubWorkflowInvocations[mappedIndex].ChildRunId, childRunId, StringComparison.Ordinal))
        {
            index = mappedIndex;
            return true;
        }

        for (var i = 0; i < state.PendingSubWorkflowInvocations.Count; i++)
        {
            if (!string.Equals(state.PendingSubWorkflowInvocations[i].ChildRunId, childRunId, StringComparison.Ordinal))
                continue;

            index = i;
            return true;
        }

        return false;
    }

    private static void AddPendingInvocation(
        WorkflowRunState state,
        WorkflowRunState.Types.PendingSubWorkflowInvocation pending)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pending);

        state.PendingSubWorkflowInvocations.Add(pending);
        var addedIndex = state.PendingSubWorkflowInvocations.Count - 1;
        var childRunId = pending.ChildRunId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(childRunId))
            state.PendingSubWorkflowInvocationIndexByChildRunId[childRunId] = addedIndex;

        AddChildRunIdToParentIndex(
            state.PendingChildRunIdsByParentRunId,
            pending.ParentRunId,
            childRunId);
    }

    private static void RemovePendingInvocation(
        WorkflowRunState state,
        string invocationId,
        string childRunId)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!string.IsNullOrWhiteSpace(childRunId))
        {
            while (TryGetPendingInvocationIndexByChildRunId(state, childRunId, out var index))
                RemovePendingInvocationAt(state, index);
        }

        if (string.IsNullOrWhiteSpace(invocationId))
            return;

        var scanIndex = 0;
        while (scanIndex < state.PendingSubWorkflowInvocations.Count)
        {
            var pending = state.PendingSubWorkflowInvocations[scanIndex];
            if (!string.Equals(pending.InvocationId, invocationId, StringComparison.Ordinal))
            {
                scanIndex++;
                continue;
            }

            RemovePendingInvocationAt(state, scanIndex);
        }
    }

    private static void RemovePendingInvocationAt(WorkflowRunState state, int index)
    {
        var pendingInvocations = state.PendingSubWorkflowInvocations;
        if (index < 0 || index >= pendingInvocations.Count)
            return;

        var removed = pendingInvocations[index];
        var removedChildRunId = removed.ChildRunId?.Trim() ?? string.Empty;
        var removedParentRunId = removed.ParentRunId?.Trim() ?? string.Empty;
        var lastIndex = pendingInvocations.Count - 1;
        var movedTailChildRunId = string.Empty;
        var movedTail = false;

        if (index != lastIndex)
        {
            var tail = pendingInvocations[lastIndex];
            pendingInvocations[index] = tail;
            pendingInvocations.RemoveAt(lastIndex);

            movedTail = true;
            movedTailChildRunId = tail.ChildRunId?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(movedTailChildRunId))
                state.PendingSubWorkflowInvocationIndexByChildRunId[movedTailChildRunId] = index;
        }
        else
        {
            pendingInvocations.RemoveAt(lastIndex);
        }

        if (!string.IsNullOrWhiteSpace(removedChildRunId) &&
            (!movedTail || !string.Equals(removedChildRunId, movedTailChildRunId, StringComparison.Ordinal)))
        {
            state.PendingSubWorkflowInvocationIndexByChildRunId.Remove(removedChildRunId);
        }

        RemoveChildRunIdFromParentIndex(
            state.PendingChildRunIdsByParentRunId,
            removedParentRunId,
            removedChildRunId);
    }

    private static void AddChildRunIdToParentIndex(
        MapField<string, WorkflowRunState.Types.ChildRunIdSet> parentIndex,
        string parentRunId,
        string childRunId)
    {
        var normalizedParentRunId = WorkflowRunIdNormalizer.Normalize(parentRunId);
        var normalizedChildRunId = childRunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedParentRunId) ||
            string.IsNullOrWhiteSpace(normalizedChildRunId))
        {
            return;
        }

        if (!parentIndex.TryGetValue(normalizedParentRunId, out var childRuns))
        {
            childRuns = new WorkflowRunState.Types.ChildRunIdSet();
            parentIndex[normalizedParentRunId] = childRuns;
        }

        if (!childRuns.ChildRunIds.Contains(normalizedChildRunId))
            childRuns.ChildRunIds.Add(normalizedChildRunId);
    }

    private static void RemoveChildRunIdFromParentIndex(
        MapField<string, WorkflowRunState.Types.ChildRunIdSet> parentIndex,
        string parentRunId,
        string childRunId)
    {
        var normalizedParentRunId = WorkflowRunIdNormalizer.Normalize(parentRunId);
        var normalizedChildRunId = childRunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedParentRunId) ||
            string.IsNullOrWhiteSpace(normalizedChildRunId) ||
            !parentIndex.TryGetValue(normalizedParentRunId, out var childRuns))
        {
            return;
        }

        for (var i = childRuns.ChildRunIds.Count - 1; i >= 0; i--)
        {
            if (string.Equals(childRuns.ChildRunIds[i], normalizedChildRunId, StringComparison.Ordinal))
                childRuns.ChildRunIds.RemoveAt(i);
        }

        if (childRuns.ChildRunIds.Count == 0)
            parentIndex.Remove(normalizedParentRunId);
    }
}
