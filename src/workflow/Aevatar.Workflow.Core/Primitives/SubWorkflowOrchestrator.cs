using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// Encapsulates workflow_call runtime orchestration for <see cref="WorkflowGAgent"/>.
/// Keeps sub-workflow actor lifecycle and state transition helpers out of the root agent.
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
    private readonly IWorkflowDefinitionResolver? _workflowDefinitionResolver;
    private readonly Func<IServiceProvider?> _serviceProviderAccessor;
    private readonly Func<string> _ownerActorIdAccessor;
    private readonly Func<ILogger> _loggerAccessor;
    private readonly Func<IMessage, CancellationToken, Task> _persistDomainEventAsync;
    private readonly Func<IReadOnlyList<IMessage>, CancellationToken, Task> _persistDomainEventsAsync;
    private readonly Func<IMessage, EventDirection, CancellationToken, Task> _publishAsync;
    private readonly Func<string, IMessage, Task> _sendToAsync;

    public SubWorkflowOrchestrator(
        IActorRuntime runtime,
        IWorkflowDefinitionResolver? workflowDefinitionResolver,
        Func<IServiceProvider?> serviceProviderAccessor,
        Func<string> ownerActorIdAccessor,
        Func<ILogger> loggerAccessor,
        Func<IMessage, CancellationToken, Task> persistDomainEventAsync,
        Func<IReadOnlyList<IMessage>, CancellationToken, Task> persistDomainEventsAsync,
        Func<IMessage, EventDirection, CancellationToken, Task> publishAsync,
        Func<string, IMessage, Task> sendToAsync)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
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
        WorkflowState state,
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

        var lifecycle = WorkflowCallLifecycle.Normalize(request.Lifecycle);
        var invocationId = string.IsNullOrWhiteSpace(request.InvocationId)
            ? WorkflowCallInvocationIdFactory.Build(parentRunId, parentStepId)
            : request.InvocationId.Trim();

        try
        {
            var childActor = await ResolveOrCreateSubWorkflowActorAsync(workflowName, lifecycle, state, ct);
            var childRunId = invocationId;

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
        WorkflowState state,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(state);

        var childRunId = completed.RunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(childRunId))
            return false;

        // Keep lookup state in persisted repeated fields first; optimize with explicit indexes in a follow-up change.
        var pending = state.PendingSubWorkflowInvocations
            .FirstOrDefault(x => string.Equals(x.ChildRunId, childRunId, StringComparison.Ordinal));
        if (pending == null)
            return false;

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
        parentCompleted.Metadata[WorkflowCallInvocationIdMetadataKey] = pending.InvocationId;
        parentCompleted.Metadata[WorkflowCallWorkflowNameMetadataKey] = pending.WorkflowName;
        parentCompleted.Metadata[WorkflowCallLifecycleMetadataKey] = WorkflowCallLifecycle.Normalize(pending.Lifecycle);
        parentCompleted.Metadata[WorkflowCallChildActorIdMetadataKey] = pending.ChildActorId;
        parentCompleted.Metadata[WorkflowCallChildRunIdMetadataKey] = childRunId;

        await _publishAsync(parentCompleted, EventDirection.Self, ct);
        await TryFinalizeNonSingletonChildAsync(pending, ct);
        return true;
    }

    public async Task CleanupPendingInvocationsForRunAsync(string runId, WorkflowState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        if (string.IsNullOrWhiteSpace(normalizedRunId))
            return;

        var cleanupEvents = new List<IMessage>();
        var staleInvocations = new List<WorkflowState.Types.PendingSubWorkflowInvocation>();
        foreach (var pending in state.PendingSubWorkflowInvocations)
        {
            if (!string.Equals(pending.ParentRunId, normalizedRunId, StringComparison.Ordinal))
                continue;

            staleInvocations.Add(pending);
            cleanupEvents.Add(new SubWorkflowInvocationCompletedEvent
            {
                InvocationId = pending.InvocationId,
                ChildRunId = pending.ChildRunId,
                Success = false,
                Error = "parent workflow completed before child workflow completion",
            });
        }

        if (cleanupEvents.Count == 0)
            return;

        await _persistDomainEventsAsync(cleanupEvents, ct);

        foreach (var staleInvocation in staleInvocations)
            await TryFinalizeNonSingletonChildAsync(staleInvocation, ct);
    }

    public static WorkflowState ApplySubWorkflowBindingUpserted(WorkflowState current, SubWorkflowBindingUpsertedEvent evt)
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
                next.SubWorkflowBindings[i] = new WorkflowState.Types.SubWorkflowBinding
                {
                    WorkflowName = workflowName,
                    ChildActorId = childActorId,
                    Lifecycle = lifecycle,
                };
                return next;
            }
        }

        next.SubWorkflowBindings.Add(new WorkflowState.Types.SubWorkflowBinding
        {
            WorkflowName = workflowName,
            ChildActorId = childActorId,
            Lifecycle = lifecycle,
        });
        return next;
    }

    public static WorkflowState ApplySubWorkflowInvocationRegistered(WorkflowState current, SubWorkflowInvocationRegisteredEvent evt)
    {
        var next = current.Clone();
        var invocationId = evt.InvocationId?.Trim() ?? string.Empty;
        var childRunId = evt.ChildRunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(invocationId) || string.IsNullOrWhiteSpace(childRunId))
            return next;

        RemovePendingInvocation(next.PendingSubWorkflowInvocations, invocationId, childRunId);
        next.PendingSubWorkflowInvocations.Add(new WorkflowState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = invocationId,
            ParentRunId = WorkflowRunIdNormalizer.Normalize(evt.ParentRunId),
            ParentStepId = evt.ParentStepId?.Trim() ?? string.Empty,
            WorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(evt.WorkflowName),
            ChildActorId = evt.ChildActorId?.Trim() ?? string.Empty,
            ChildRunId = childRunId,
            Lifecycle = WorkflowCallLifecycle.Normalize(evt.Lifecycle),
        });
        return next;
    }

    public static WorkflowState ApplySubWorkflowInvocationCompleted(WorkflowState current, SubWorkflowInvocationCompletedEvent evt)
    {
        var next = current.Clone();
        RemovePendingInvocation(
            next.PendingSubWorkflowInvocations,
            evt.InvocationId?.Trim() ?? string.Empty,
            evt.ChildRunId?.Trim() ?? string.Empty);
        return next;
    }

    public static void PruneIdleSubWorkflowBindings(WorkflowState state, WorkflowDefinition workflow)
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
        WorkflowState state,
        CancellationToken ct)
    {
        var normalizedWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(workflowName);
        var normalizedLifecycle = WorkflowCallLifecycle.Normalize(lifecycle);
        if (!string.Equals(normalizedLifecycle, WorkflowCallLifecycle.Singleton, StringComparison.OrdinalIgnoreCase))
            return await CreateSubWorkflowActorAsync(normalizedWorkflowName, normalizedLifecycle, persistBinding: false, ct);

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

        return await CreateSubWorkflowActorAsync(normalizedWorkflowName, normalizedLifecycle, persistBinding: true, ct);
    }

    private async Task<IActor> CreateSubWorkflowActorAsync(
        string workflowName,
        string lifecycle,
        bool persistBinding,
        CancellationToken ct)
    {
        var workflowYaml = await ResolveWorkflowYamlAsync(workflowName, ct);
        if (string.IsNullOrWhiteSpace(workflowYaml))
            throw new InvalidOperationException($"workflow_call references unregistered workflow '{workflowName}'");

        var childActorId = BuildSubWorkflowActorId(workflowName, lifecycle);
        var childActor = await ResolveOrCreateWorkflowActorByIdAsync(childActorId);
        await _runtime.LinkAsync(_ownerActorIdAccessor(), childActor.Id);
        await childActor.HandleEventAsync(CreateWorkflowConfigureEnvelope(workflowYaml, workflowName));

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

    private async Task<string?> ResolveWorkflowYamlAsync(string workflowName, CancellationToken ct)
    {
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
        }, EventDirection.Self, ct);
    }

    private async Task TryFinalizeNonSingletonChildAsync(
        WorkflowState.Types.PendingSubWorkflowInvocation pending,
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
            return await _runtime.CreateAsync(typeof(WorkflowGAgent), actorId);
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

    private EventEnvelope CreateWorkflowConfigureEnvelope(string workflowYaml, string workflowName)
    {
        var configure = new ConfigureWorkflowEvent
        {
            WorkflowYaml = workflowYaml ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
        };

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(configure),
            PublisherId = _ownerActorIdAccessor(),
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
    }

    private static bool ShouldEvictBinding(
        WorkflowState state,
        WorkflowState.Types.SubWorkflowBinding binding,
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
        IEnumerable<WorkflowState.Types.PendingSubWorkflowInvocation> pendingInvocations,
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

    private static void RemovePendingInvocation(
        RepeatedField<WorkflowState.Types.PendingSubWorkflowInvocation> pendingInvocations,
        string invocationId,
        string childRunId)
    {
        for (var i = pendingInvocations.Count - 1; i >= 0; i--)
        {
            var pending = pendingInvocations[i];
            if ((!string.IsNullOrWhiteSpace(invocationId) &&
                 string.Equals(pending.InvocationId, invocationId, StringComparison.Ordinal)) ||
                (!string.IsNullOrWhiteSpace(childRunId) &&
                 string.Equals(pending.ChildRunId, childRunId, StringComparison.Ordinal)))
            {
                pendingInvocations.RemoveAt(i);
            }
        }
    }
}
