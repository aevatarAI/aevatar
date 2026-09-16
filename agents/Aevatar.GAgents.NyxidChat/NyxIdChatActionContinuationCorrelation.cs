namespace Aevatar.GAgents.NyxidChat;

internal sealed record NyxIdChatActionContinuationCorrelationMatch(
    NyxIdChatTaskStepState OperationStep,
    NyxIdChatTaskStepState PostconditionStep,
    NyxIdChatTaskStepState? SourceToolStep,
    NyxIdChatActionRequestState ActionRequest);

internal static class NyxIdChatActionContinuationCorrelation
{
    private const string AuthorizationReadinessToolName = "nyxid_require_service";

    public static bool TryMatch(
        NyxIdChatConversationGAgentState authorityState,
        NyxIdChatTaskState? candidateTask,
        NyxIdChatTurnState? candidateTurn,
        NyxIdChatOperationKey? key,
        out NyxIdChatActionContinuationCorrelationMatch match)
    {
        match = null!;
        if (authorityState.ActiveTask is null ||
            authorityState.ContinuationAdmission is not
            {
                Kind: NyxIdChatContinuationKind.Action,
                Status: NyxIdChatContinuationAdmissionStatus.Accepted,
            } admission ||
            candidateTask is null ||
            candidateTurn is null ||
            key is null ||
            string.IsNullOrWhiteSpace(key.ConversationActorId) ||
            string.IsNullOrWhiteSpace(key.TurnId) ||
            string.IsNullOrWhiteSpace(key.TaskId) ||
            string.IsNullOrWhiteSpace(key.StepId) ||
            string.IsNullOrWhiteSpace(key.OperationId) ||
            key.OperationGeneration <= 0 ||
            !string.Equals(
                authorityState.ConversationActorId,
                key.ConversationActorId,
                StringComparison.Ordinal) ||
            !string.Equals(admission.OriginTurnId, key.TurnId, StringComparison.Ordinal) ||
            !string.Equals(
                admission.ContinuationTurnId,
                candidateTurn.TurnId,
                StringComparison.Ordinal) ||
            !string.Equals(candidateTask.TurnId, candidateTurn.TurnId, StringComparison.Ordinal) ||
            !string.Equals(candidateTask.TaskId, key.TaskId, StringComparison.Ordinal) ||
            !string.Equals(candidateTurn.TaskId, key.TaskId, StringComparison.Ordinal))
        {
            return false;
        }

        var currentSteps = authorityState.ActiveTask.Steps
            .Where(step => KeysEqual(step.Operation?.Key, key))
            .Take(2)
            .ToArray();
        var candidateSteps = candidateTask.Steps
            .Where(step => KeysEqual(step.Operation?.Key, key))
            .Take(2)
            .ToArray();
        if (currentSteps.Length != 1 || candidateSteps.Length != 1)
            return false;

        var operationStep = currentSteps[0];
        var actionRequestId = ResolveActionRequestId(
            authorityState.ActiveTask,
            operationStep);
        if (actionRequestId is null ||
            !TryResolvePostcondition(
                authorityState.ActiveTask,
                operationStep,
                actionRequestId,
                out var postconditionStep))
        {
            return false;
        }

        var actions = authorityState.PendingActions
            .Concat(authorityState.RecentActions)
            .Where(action => string.Equals(
                action.ActionRequestId,
                actionRequestId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (actions.Length != 1)
            return false;

        var action = actions[0];
        if (!string.Equals(action.ConversationActorId, key.ConversationActorId, StringComparison.Ordinal) ||
            !string.Equals(action.OriginTurnId, key.TurnId, StringComparison.Ordinal) ||
            !string.Equals(action.TaskId, key.TaskId, StringComparison.Ordinal))
        {
            return false;
        }

        NyxIdChatTaskStepState? sourceToolStep = null;
        if (action.HasSourceToolStepId)
        {
            var sourceSteps = authorityState.ActiveTask.Steps
                .Where(step => string.Equals(
                    step.StepId,
                    action.SourceToolStepId,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (sourceSteps.Length != 1 || sourceSteps[0].Kind != NyxIdChatStepKind.Tool)
                return false;
            sourceToolStep = sourceSteps[0];
        }

        if (operationStep.Kind != NyxIdChatStepKind.Postcondition &&
            (action.PostconditionResult is not
             {
                 Verified: true,
                 Disposition: NyxIdChatActionDisposition.Completed,
             } verified ||
             !string.Equals(
                 verified.ActionRequestId,
                 actionRequestId,
                 StringComparison.Ordinal) ||
             postconditionStep.Status != NyxIdChatStepStatus.Done ||
             postconditionStep.ExternalEffect != NyxIdChatEffectEvidence.Confirmed))
        {
            return false;
        }

        match = new NyxIdChatActionContinuationCorrelationMatch(
            operationStep,
            postconditionStep,
            sourceToolStep,
            action);
        return true;
    }

    public static NyxIdChatVerifiedAuthorizationContinuation BuildVerifiedAuthorizationContinuation(
        NyxIdChatActionContinuationCorrelationMatch match,
        Google.Protobuf.WellKnownTypes.Timestamp now)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(now);

        var continuation = new NyxIdChatVerifiedAuthorizationContinuation
        {
            ActionRequestId = match.ActionRequest.ActionRequestId,
            OriginTurnId = match.ActionRequest.OriginTurnId,
            SourceToolStepId = match.ActionRequest.HasSourceToolStepId
                ? match.ActionRequest.SourceToolStepId
                : string.Empty,
            PostconditionStepId = match.PostconditionStep.StepId,
            ServiceSlug = ResolveServiceSlug(match.ActionRequest),
            VerifiedAt = match.PostconditionStep.Operation?.CompletedAt?.Clone() ?? now.Clone(),
            ResumeRequirement = match.OperationStep.Source?.Llm?.ResumeRequirement ??
                NyxIdChatAuthorizationResumeRequirement.Unspecified,
        };
        if (match.ActionRequest.PostconditionResult?.Resource is not null)
        {
            continuation.VerifiedResource =
                match.ActionRequest.PostconditionResult.Resource.Clone();
        }
        if (TryResolveAuthorizationReadiness(
                match.SourceToolStep,
                continuation.ServiceSlug,
                out var authorizationReadiness))
        {
            continuation.AuthorizationReadiness = authorizationReadiness;
        }

        return continuation;
    }

    private static bool TryResolveAuthorizationReadiness(
        NyxIdChatTaskStepState? sourceToolStep,
        string serviceSlug,
        out NyxIdChatAuthorizationReadinessInput authorizationReadiness)
    {
        authorizationReadiness = null!;
        var source = sourceToolStep?.Source?.Tool;
        var frozen = source?.AuthorizationReadiness;
        if (source is null ||
            frozen?.Params is null ||
            !string.Equals(
                source.ToolName,
                AuthorizationReadinessToolName,
                StringComparison.Ordinal) ||
            !string.Equals(
                frozen.ToolName,
                AuthorizationReadinessToolName,
                StringComparison.Ordinal) ||
            !string.Equals(
                frozen.Params.ServiceSlug,
                serviceSlug,
                StringComparison.Ordinal))
        {
            return false;
        }

        authorizationReadiness = frozen.Clone();
        return true;
    }

    private static string? ResolveActionRequestId(
        NyxIdChatTaskState task,
        NyxIdChatTaskStepState operationStep)
    {
        var pending = new Stack<(NyxIdChatTaskStepState Step, bool Complete)>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var actionRequestIds = new HashSet<string>(StringComparer.Ordinal);
        pending.Push((operationStep, false));
        while (pending.Count > 0)
        {
            var (candidate, complete) = pending.Pop();
            if (string.IsNullOrWhiteSpace(candidate.StepId))
                return null;
            if (complete)
            {
                visiting.Remove(candidate.StepId);
                visited.Add(candidate.StepId);
                continue;
            }
            if (visited.Contains(candidate.StepId))
                continue;
            if (!visiting.Add(candidate.StepId))
                return null;

            pending.Push((candidate, true));

            var candidateActionRequestId = ResolveDirectActionRequestId(candidate);
            if (candidateActionRequestId is not null)
            {
                actionRequestIds.Add(candidateActionRequestId);
                if (actionRequestIds.Count > 1)
                    return null;
            }

            foreach (var dependencyId in candidate.DependsOn)
            {
                var dependencies = task.Steps
                    .Where(step => string.Equals(
                        step.StepId,
                        dependencyId,
                        StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                if (dependencies.Length != 1)
                    return null;
                pending.Push((dependencies[0], false));
            }
        }

        return actionRequestIds.Count == 1
            ? actionRequestIds.Single()
            : null;
    }

    private static string? ResolveDirectActionRequestId(NyxIdChatTaskStepState step) =>
        step.Kind switch
        {
            NyxIdChatStepKind.Postcondition
                when !string.IsNullOrWhiteSpace(step.ActionRequestId) &&
                     string.Equals(
                         step.ActionRequestId,
                         step.Source?.Postcondition?.ActionRequestId,
                         StringComparison.Ordinal) => step.ActionRequestId,
            NyxIdChatStepKind.Llm
                when !string.IsNullOrWhiteSpace(step.Source?.Llm?.ActionRequestId) =>
                step.Source.Llm.ActionRequestId,
            _ => null,
        };

    private static bool TryResolvePostcondition(
        NyxIdChatTaskState task,
        NyxIdChatTaskStepState operationStep,
        string actionRequestId,
        out NyxIdChatTaskStepState postconditionStep)
    {
        postconditionStep = null!;
        if (operationStep.Kind == NyxIdChatStepKind.Postcondition)
        {
            postconditionStep = operationStep;
            return true;
        }

        var pending = new Stack<NyxIdChatTaskStepState>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<NyxIdChatTaskStepState>(2);
        pending.Push(operationStep);
        while (pending.Count > 0)
        {
            var candidate = pending.Pop();
            if (!visited.Add(candidate.StepId))
                continue;

            if (candidate.Kind == NyxIdChatStepKind.Postcondition &&
                string.Equals(candidate.ActionRequestId, actionRequestId, StringComparison.Ordinal) &&
                string.Equals(
                    candidate.Source?.Postcondition?.ActionRequestId,
                    actionRequestId,
                    StringComparison.Ordinal))
            {
                matches.Add(candidate);
                if (matches.Count > 1)
                    return false;
            }

            foreach (var dependencyId in candidate.DependsOn)
            {
                var dependencies = task.Steps
                    .Where(step => string.Equals(
                        step.StepId,
                        dependencyId,
                        StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                if (dependencies.Length != 1)
                    return false;
                pending.Push(dependencies[0]);
            }
        }

        if (matches.Count != 1)
            return false;
        postconditionStep = matches[0];
        return true;
    }

    private static string ResolveServiceSlug(NyxIdChatActionRequestState request) =>
        request.Params?.ParamsCase switch
        {
            NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect =>
                Normalize(request.Params.CatalogServiceConnect.ServiceSlug),
            NyxIdAssistantActionParams.ParamsOneofCase.ServiceAccessReview =>
                Normalize(request.Params.ServiceAccessReview.ServiceSlug),
            _ => string.Empty,
        };

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static bool KeysEqual(NyxIdChatOperationKey? left, NyxIdChatOperationKey? right) =>
        left is not null &&
        right is not null &&
        string.Equals(left.ConversationActorId, right.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) &&
        string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) &&
        left.OperationGeneration == right.OperationGeneration;
}
