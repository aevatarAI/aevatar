using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;

namespace Aevatar.Workflow.Core;

/// <summary>
/// Workflow definition actor. Owns definition YAML and compilation result only.
/// </summary>
public sealed class WorkflowGAgent : GAgentBase<WorkflowState>
{
    private readonly WorkflowParser _parser = new();
    private readonly WorkflowYamlBundleNormalizer _bundleNormalizer = new();

    public async Task BindWorkflowDefinitionAsync(
        string workflowYaml,
        string? workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        string? scopeId = null,
        CancellationToken ct = default)
    {
        EnsureWorkflowNameCanBind(workflowName);
        var bindDefinitionEvent = new BindWorkflowDefinitionEvent
        {
            WorkflowName = workflowName ?? string.Empty,
            WorkflowYaml = workflowYaml ?? string.Empty,
            ScopeId = scopeId?.Trim() ?? string.Empty,
        };
        if (inlineWorkflowYamls != null)
        {
            foreach (var (key, value) in inlineWorkflowYamls)
                bindDefinitionEvent.InlineWorkflowYamls[key] = value;
        }

        await PersistDomainEventAsync(bindDefinitionEvent, ct);
    }

    [EventHandler]
    public Task HandleBindWorkflowDefinition(BindWorkflowDefinitionEvent request) =>
        BindWorkflowDefinitionAsync(request.WorkflowYaml, request.WorkflowName, request.InlineWorkflowYamls, request.ScopeId);

    [EventHandler]
    public Task HandleSubWorkflowDefinitionResolveRequested(SubWorkflowDefinitionResolveRequestedEvent request) =>
        HandleSubWorkflowDefinitionResolveRequestedAsync(request, CancellationToken.None);

    public override Task<string> GetDescriptionAsync()
    {
        var status = State.Compiled ? "compiled" : "invalid";
        return Task.FromResult($"WorkflowGAgent[{State.WorkflowName}] v{State.Version} ({status})");
    }

    protected override WorkflowState TransitionState(WorkflowState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<BindWorkflowDefinitionEvent>(ApplyBindWorkflowDefinition)
            .OrCurrent();

    private WorkflowState ApplyBindWorkflowDefinition(WorkflowState current, BindWorkflowDefinitionEvent evt)
    {
        var next = current.Clone();
        var rawWorkflowYaml = evt.WorkflowYaml ?? string.Empty;
        var rawInlineWorkflowYamls = NormalizeInlineWorkflowYamls(evt.InlineWorkflowYamls);

        try
        {
            var normalizedBundle = _bundleNormalizer.Normalize(rawWorkflowYaml, rawInlineWorkflowYamls);
            next.WorkflowYaml = normalizedBundle.WorkflowYaml;
            ReplaceInlineWorkflowYamls(next.InlineWorkflowYamls, normalizedBundle.InlineWorkflowYamls);
            var compileResult = EvaluateWorkflowCompilation(next.WorkflowYaml);
            next.Compiled = compileResult.Compiled;
            next.CompilationError = compileResult.CompilationError;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ApplyBindWorkflowDefinition: normalization failed.");
            next.WorkflowYaml = rawWorkflowYaml;
            ReplaceInlineWorkflowYamls(next.InlineWorkflowYamls, rawInlineWorkflowYamls);
            next.Compiled = false;
            next.CompilationError = ex.Message;
        }

        var incomingWorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName)
            ? string.Empty
            : evt.WorkflowName.Trim();
        if (!string.IsNullOrWhiteSpace(incomingWorkflowName))
            next.WorkflowName = incomingWorkflowName;
        if (!string.IsNullOrWhiteSpace(evt.ScopeId))
            next.ScopeId = evt.ScopeId.Trim();
        next.Version = current.Version + 1;
        return next;
    }

    private static Dictionary<string, string> NormalizeInlineWorkflowYamls(
        IEnumerable<KeyValuePair<string, string>> inlineWorkflowYamls)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (workflowNameKey, workflowYamlValue) in inlineWorkflowYamls)
        {
            var normalizedWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(workflowNameKey);
            if (string.IsNullOrWhiteSpace(normalizedWorkflowName) ||
                string.IsNullOrWhiteSpace(workflowYamlValue))
            {
                continue;
            }

            normalized[normalizedWorkflowName] = workflowYamlValue;
        }

        return normalized;
    }

    private static void ReplaceInlineWorkflowYamls(
        IDictionary<string, string> target,
        IEnumerable<KeyValuePair<string, string>> source)
    {
        target.Clear();
        foreach (var (key, value) in source)
            target[key] = value;
    }

    private WorkflowCompilationResult EvaluateWorkflowCompilation(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return WorkflowCompilationResult.Invalid("workflow yaml is empty");

        try
        {
            var workflow = _parser.Parse(yaml);
            var errors = WorkflowValidator.Validate(
                workflow,
                new WorkflowValidator.WorkflowValidationOptions
                {
                    RequireKnownStepTypes = false,
                },
                availableWorkflowNames: null);
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

    private readonly record struct WorkflowCompilationResult(bool Compiled, string CompilationError, WorkflowDefinition? Workflow)
    {
        public static WorkflowCompilationResult Success(WorkflowDefinition workflow) =>
            new(true, string.Empty, workflow);

        public static WorkflowCompilationResult Invalid(string error) =>
            new(false, error ?? string.Empty, null);
    }

    private async Task HandleSubWorkflowDefinitionResolveRequestedAsync(
        SubWorkflowDefinitionResolveRequestedEvent request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var invocationId = request.InvocationId?.Trim() ?? string.Empty;
        var parentActorId = request.ParentActorId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(invocationId) || string.IsNullOrWhiteSpace(parentActorId))
            return;

        var requestedDefinitionActorId = request.RequestedDefinitionActorId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(requestedDefinitionActorId) &&
            !string.Equals(requestedDefinitionActorId, Id, StringComparison.Ordinal))
        {
            await SendResolveFailedAsync(
                parentActorId,
                invocationId,
                request.WorkflowName,
                requestedDefinitionActorId,
                $"workflow_call requested definition actor '{requestedDefinitionActorId}', but current actor is '{Id}'.",
                ct);
            return;
        }

        var requestedWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(request.WorkflowName);
        var currentWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(State.WorkflowName);
        if (string.IsNullOrWhiteSpace(currentWorkflowName))
        {
            await SendResolveFailedAsync(
                parentActorId,
                invocationId,
                request.WorkflowName,
                Id,
                $"WorkflowGAgent '{Id}' is not bound to a workflow definition.",
                ct);
            return;
        }

        if (!string.Equals(currentWorkflowName, requestedWorkflowName, StringComparison.OrdinalIgnoreCase))
        {
            await SendResolveFailedAsync(
                parentActorId,
                invocationId,
                request.WorkflowName,
                Id,
                $"WorkflowGAgent '{Id}' is bound to workflow '{State.WorkflowName}', not '{request.WorkflowName}'.",
                ct);
            return;
        }

        if (!State.Compiled || string.IsNullOrWhiteSpace(State.WorkflowYaml))
        {
            await SendResolveFailedAsync(
                parentActorId,
                invocationId,
                request.WorkflowName,
                Id,
                string.IsNullOrWhiteSpace(State.CompilationError)
                    ? $"WorkflowGAgent '{Id}' does not have a compiled workflow definition."
                    : State.CompilationError,
                ct);
            return;
        }

        var resolved = new SubWorkflowDefinitionResolvedEvent
        {
            InvocationId = invocationId,
            Definition = BuildDefinitionSnapshot(),
        };

        await SendToAsync(parentActorId, resolved, ct);
    }

    private WorkflowDefinitionSnapshot BuildDefinitionSnapshot()
    {
        var snapshot = new WorkflowDefinitionSnapshot
        {
            DefinitionActorId = Id,
            WorkflowName = State.WorkflowName ?? string.Empty,
            WorkflowYaml = State.WorkflowYaml ?? string.Empty,
            ScopeId = State.ScopeId ?? string.Empty,
            DefinitionVersion = State.Version,
        };

        foreach (var (workflowName, workflowYaml) in State.InlineWorkflowYamls)
            snapshot.InlineWorkflowYamls[workflowName] = workflowYaml;

        return snapshot;
    }

    private Task SendResolveFailedAsync(
        string parentActorId,
        string invocationId,
        string? workflowName,
        string? definitionActorId,
        string error,
        CancellationToken ct)
    {
        return SendToAsync(
            parentActorId,
            new SubWorkflowDefinitionResolveFailedEvent
            {
                InvocationId = invocationId,
                WorkflowName = workflowName ?? string.Empty,
                DefinitionActorId = definitionActorId ?? string.Empty,
                Error = error ?? string.Empty,
            },
            ct);
    }
}
