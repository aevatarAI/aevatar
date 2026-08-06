using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowRunGAgent
{
    [EventHandler(EndpointName = "ensureWorkflowRunDefinition")]
    public async Task HandleEnsureWorkflowRunDefinitionAsync(EnsureWorkflowRunDefinitionEvent command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var binding = command.Binding
            ?? throw new InvalidOperationException("workflow Run ensure binding is required.");

        if (string.IsNullOrWhiteSpace(State.WorkflowYaml) &&
            string.IsNullOrWhiteSpace(State.DefinitionActorId) &&
            string.IsNullOrWhiteSpace(State.RunId))
        {
            await BindWorkflowRunDefinitionAsync(
                binding.DefinitionActorId,
                binding.WorkflowYaml,
                binding.WorkflowName,
                binding.InlineWorkflowYamls,
                binding.RunId,
                binding.ScopeId,
                binding.RunOrigin,
                binding.ScheduleId,
                binding.CapabilityAdmissionPlan,
                binding.ExpectedExecutionMode);
        }
        else
        {
            EnsureExistingRunBindingMatches(binding);
        }

        if (string.IsNullOrWhiteSpace(binding.DefinitionActorId))
            throw new InvalidOperationException("workflow Run ensure definition actor id is required.");
        await _runtime.LinkAsync(binding.DefinitionActorId.Trim(), Id);

        if (command.ExecutionRequest != null)
            await HandleChatRequest(command.ExecutionRequest);
    }

    private void EnsureExistingRunBindingMatches(BindWorkflowRunDefinitionEvent binding)
    {
        var requestedRunId = string.IsNullOrWhiteSpace(binding.RunId)
            ? Id
            : WorkflowRunIdNormalizer.Normalize(binding.RunId);
        var currentRunId = string.IsNullOrWhiteSpace(State.RunId)
            ? Id
            : WorkflowRunIdNormalizer.Normalize(State.RunId);
        var same =
            string.Equals(State.DefinitionActorId, binding.DefinitionActorId?.Trim(), StringComparison.Ordinal) &&
            string.Equals(State.WorkflowName, binding.WorkflowName?.Trim(), StringComparison.Ordinal) &&
            string.Equals(State.WorkflowYaml, binding.WorkflowYaml, StringComparison.Ordinal) &&
            string.Equals(currentRunId, requestedRunId, StringComparison.Ordinal) &&
            string.Equals(State.ScopeId, binding.ScopeId?.Trim(), StringComparison.Ordinal) &&
            string.Equals(State.RunOrigin, binding.RunOrigin?.Trim(), StringComparison.Ordinal) &&
            string.Equals(State.ScheduleId, binding.ScheduleId?.Trim(), StringComparison.Ordinal) &&
            State.ExpectedExecutionMode == binding.ExpectedExecutionMode &&
            string.Equals(
                State.CapabilityAdmissionPlan?.AdmissionDigest ?? string.Empty,
                binding.CapabilityAdmissionPlan?.AdmissionDigest ?? string.Empty,
                StringComparison.Ordinal) &&
            InlineWorkflowYamlsEqual(State.InlineWorkflowYamls, binding.InlineWorkflowYamls);
        if (!same)
        {
            throw new InvalidOperationException(
                $"workflow Run '{Id}' is already bound to a different definition or identity.");
        }
    }

    private static bool InlineWorkflowYamlsEqual(
        IDictionary<string, string> current,
        IDictionary<string, string> incoming)
    {
        var normalizedIncoming = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (workflowName, workflowYaml) in incoming)
        {
            var normalizedName = WorkflowRunIdNormalizer.NormalizeWorkflowName(workflowName);
            if (string.IsNullOrWhiteSpace(normalizedName) || string.IsNullOrWhiteSpace(workflowYaml))
                continue;
            normalizedIncoming[normalizedName] = workflowYaml;
        }

        if (current.Count != normalizedIncoming.Count)
            return false;

        return current.All(entry =>
            normalizedIncoming.TryGetValue(entry.Key, out var yaml) &&
            string.Equals(entry.Value, yaml, StringComparison.Ordinal));
    }
}
