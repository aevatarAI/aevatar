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
        var publisherActorId = ActiveInboundEnvelope?.Route?.PublisherActorId;
        var decision = EvaluateRunDefinitionBind(State, binding, publisherActorId, verifyPublisher: true);
        if (decision.Disposition == RunDefinitionBindDisposition.Ignore)
            return;
        if (decision.Disposition == RunDefinitionBindDisposition.Reject)
            throw new InvalidOperationException(decision.Error);

        var requestedRunId = ResolveRequestedBindRunId(State, binding.RunId);
        var isCurrentGeneration = !string.IsNullOrWhiteSpace(State.RunId) &&
                                  string.Equals(
                                      WorkflowRunIdNormalizer.Normalize(State.RunId),
                                      requestedRunId,
                                      StringComparison.Ordinal) &&
                                  State.BindingGeneration == binding.BindingGeneration;
        if (isCurrentGeneration)
        {
            EnsureExistingRunBindingMatches(binding);
        }
        else
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
                binding.WorkflowId,
                binding.RevisionId,
                binding.DefinitionVersion,
                binding.CapabilityAdmissionPlan,
                binding.ExpectedExecutionMode,
                binding.InitialLineage,
                binding.ReusePolicy,
                binding.BindingGeneration,
                binding.ReuseAuthorityActorId,
                publisherActorId);
        }

        if (string.IsNullOrWhiteSpace(binding.DefinitionActorId))
            throw new InvalidOperationException("workflow Run ensure definition actor id is required.");
        await _runtime.LinkAsync(binding.DefinitionActorId.Trim(), Id);

        if (command.ExecutionRequest != null)
            await HandleChatRequest(command.ExecutionRequest);
    }

    private void EnsureExistingRunBindingMatches(BindWorkflowRunDefinitionEvent binding)
    {
        var requestedRunId = ResolveRequestedBindRunId(State, binding.RunId);
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
            string.Equals(State.WorkflowId, binding.WorkflowId?.Trim(), StringComparison.Ordinal) &&
            string.Equals(State.RevisionId?.Trim() ?? string.Empty, binding.RevisionId?.Trim() ?? string.Empty, StringComparison.Ordinal) &&
            Math.Max(0, State.DefinitionVersion) == Math.Max(0, binding.DefinitionVersion) &&
            NormalizeLiveReusePolicy(State.ReusePolicy) == NormalizeLiveReusePolicy(binding.ReusePolicy) &&
            State.BindingGeneration == binding.BindingGeneration &&
            string.Equals(
                State.ReuseAuthorityActorId,
                binding.ReuseAuthorityActorId?.Trim(),
                StringComparison.Ordinal) &&
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

    private static WorkflowRunActorReusePolicy NormalizeLiveReusePolicy(
        WorkflowRunActorReusePolicy policy) =>
        policy == WorkflowRunActorReusePolicy.Unspecified
            ? WorkflowRunActorReusePolicy.SingleRun
            : policy;
}
