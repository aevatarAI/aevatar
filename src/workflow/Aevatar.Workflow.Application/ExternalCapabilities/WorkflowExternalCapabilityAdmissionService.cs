using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Application.ExternalCapabilities;

public sealed class WorkflowExternalCapabilityAdmissionService :
    IWorkflowExternalCapabilityAdmissionService
{
    private readonly IWorkflowDefinitionParser _parser;
    private readonly IExternalWorkflowCapabilityReadinessPort _readinessPort;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyDictionary<
        ExternalWorkflowCapabilitySelector.SelectorOneofCase,
        IExternalWorkflowCapabilityAdmissionPreparer> _preparers;

    public WorkflowExternalCapabilityAdmissionService(
        IWorkflowDefinitionParser parser,
        IExternalWorkflowCapabilityReadinessPort readinessPort,
        TimeProvider? timeProvider = null,
        IEnumerable<IExternalWorkflowCapabilityAdmissionPreparer>? preparers = null)
    {
        _parser = parser;
        _readinessPort = readinessPort;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _preparers = (preparers ?? [])
            .ToDictionary(static preparer => preparer.SelectorKind);
    }

    public async Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
        WorkflowExternalCapabilityAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExecutionMode == ExternalCapabilityExecutionMode.Unspecified)
            throw new InvalidOperationException("External capability execution mode is required.");
        if (request.ExplicitRequestGrantMode == ExternalCapabilityExecutionMode.Unspecified)
            throw new InvalidOperationException("Explicit request grant mode is required.");
        if (request.ExecutionMode == ExternalCapabilityExecutionMode.Durable &&
            request.ExplicitRequestGrantMode != ExternalCapabilityExecutionMode.Durable)
        {
            throw new InvalidOperationException(
                "Durable execution requires a durable explicit request grant mode.");
        }

        var definition = await ParseDefinitionAsync(
            request.WorkflowYaml,
            request.InlineWorkflowYamls,
            request.WorkflowYamls,
            request.ExecutionMode,
            cancellationToken);

        EnsureConfirmationBindingsMatch(request);
        EnsureConfirmationCallSitesAreExpected(request, definition.Invocations);

        foreach (var invocation in definition.Invocations)
            EnsureSelectorIsAdmissible(invocation, request.ExecutionMode);

        var invocations = definition.Invocations.ToArray();
        var initialReadiness = new ExternalCapabilityReadiness[invocations.Length];
        for (var index = 0; index < invocations.Length; index++)
        {
            initialReadiness[index] = await InspectAsync(
                request,
                invocations[index],
                cancellationToken);
        }

        var verifiedInvocations = new VerifiedInvocationAdmission?[invocations.Length];
        var pendingConvergence = new List<PendingInvocationConvergence>();
        for (var index = 0; index < invocations.Length; index++)
        {
            var invocation = invocations[index];
            var readiness = initialReadiness[index];
            if (readiness.Status == ExternalCapabilityReadinessStatus.Ready)
            {
                verifiedInvocations[index] = VerifyReadiness(request, invocation, readiness);
                continue;
            }

            var selectorKind = invocation.Selector.SelectorCase;
            if (!_preparers.TryGetValue(selectorKind, out var preparer) ||
                !preparer.CanConverge(readiness))
            {
                throw new WorkflowExternalCapabilityAdmissionException(readiness);
            }

            var proofFailure = ValidateConvergenceReadinessProof(
                invocation.Selector,
                request.ExecutionMode,
                readiness);
            if (proofFailure is not null)
                throw new WorkflowExternalCapabilityAdmissionException(proofFailure);
            EnsureSourcesAreFresh(
                readiness.Sources,
                request.ExecutionMode,
                invocation.Selector,
                readiness.SelectedCapability);
            pendingConvergence.Add(new PendingInvocationConvergence(invocation, preparer));
        }

        foreach (var pending in pendingConvergence
                     .GroupBy(
                         static item => WorkflowCapabilityAdmissionPlanIntegrity.SelectorKey(
                             item.Invocation.Selector),
                         StringComparer.Ordinal)
                     .Select(static group => group.First()))
        {
            await pending.Preparer.PrepareAsync(
                request.Access,
                pending.Invocation.Selector,
                request.ExecutionMode,
                cancellationToken);
        }

        if (pendingConvergence.Count > 0)
        {
            for (var index = 0; index < invocations.Length; index++)
            {
                verifiedInvocations[index] = await InspectAndVerifyAsync(
                    request,
                    invocations[index],
                    cancellationToken);
            }
        }

        var admissions = new List<WorkflowCapabilityInvocationAdmission>();
        var sources = new List<ExternalCapabilitySourceStamp>();
        for (var index = 0; index < invocations.Length; index++)
        {
            var verified = verifiedInvocations[index] ?? throw new InvalidOperationException(
                "Workflow external capability readiness was not verified.");
            admissions.Add(verified.Admission);
            sources.AddRange(verified.Readiness.Sources.Select(static source => source.Clone()));
        }

        return WorkflowCapabilityAdmissionPlanIntegrity.Create(
            definition.WorkflowYaml,
            definition.InlineWorkflowYamls,
            request.ExecutionMode,
            admissions,
            sources,
            BuildDurableAuthorizationOwner(
                request.Access,
                request.ExecutionMode,
                admissions.Select(static admission => admission.Capability)),
            request.WorkflowId,
            request.RevisionId);
    }

    private async Task<VerifiedInvocationAdmission> InspectAndVerifyAsync(
        WorkflowExternalCapabilityAdmissionRequest request,
        ExternalToolInvocationSpec invocation,
        CancellationToken cancellationToken)
    {
        var readiness = await InspectAsync(request, invocation, cancellationToken);
        return VerifyReadiness(request, invocation, readiness);
    }

    private Task<ExternalCapabilityReadiness> InspectAsync(
        WorkflowExternalCapabilityAdmissionRequest request,
        ExternalToolInvocationSpec invocation,
        CancellationToken cancellationToken) =>
        _readinessPort.InspectAsync(
            new InspectExternalWorkflowCapabilityReadinessRequest(
                request.Access,
                invocation.Selector,
                request.ExecutionMode),
            cancellationToken);

    private VerifiedInvocationAdmission VerifyReadiness(
        WorkflowExternalCapabilityAdmissionRequest request,
        ExternalToolInvocationSpec invocation,
        ExternalCapabilityReadiness readiness)
    {
        if (readiness.Status != ExternalCapabilityReadinessStatus.Ready)
            throw new WorkflowExternalCapabilityAdmissionException(readiness);
        var proofFailure = ValidateReadinessIdentityProof(
            invocation.Selector,
            request.ExecutionMode,
            readiness);
        if (proofFailure is not null)
            throw new WorkflowExternalCapabilityAdmissionException(proofFailure);

        var admission = BuildInvocationAdmission(request, invocation, readiness.SelectedCapability);
        proofFailure = ValidateReadinessSourceProof(
            request.Access,
            invocation.Selector,
            request.ExecutionMode,
            readiness);
        if (proofFailure is not null)
            throw new WorkflowExternalCapabilityAdmissionException(proofFailure);
        EnsureSourcesAreFresh(
            readiness.Sources,
            request.ExecutionMode,
            invocation.Selector,
            readiness.SelectedCapability);
        return new VerifiedInvocationAdmission(readiness, admission);
    }

    private static void EnsureSelectorIsAdmissible(
        ExternalToolInvocationSpec invocation,
        ExternalCapabilityExecutionMode executionMode)
    {
        if (!WorkflowAuthorizationDependencyEvaluator.RequiresExternalCapabilityAdmission(invocation.ToolName) ||
            invocation.Selector.SelectorCase !=
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.None)
        {
            return;
        }

        throw new WorkflowExternalCapabilityAdmissionException(
            BuildNyxIdOperationSelectionRequiredReadiness(executionMode));
    }

    private sealed record VerifiedInvocationAdmission(
        ExternalCapabilityReadiness Readiness,
        WorkflowCapabilityInvocationAdmission Admission);

    private sealed record PendingInvocationConvergence(
        ExternalToolInvocationSpec Invocation,
        IExternalWorkflowCapabilityAdmissionPreparer Preparer);

    private static WorkflowCapabilityInvocationAdmission BuildInvocationAdmission(
        WorkflowExternalCapabilityAdmissionRequest request,
        ExternalToolInvocationSpec invocation,
        ExternalWorkflowCapabilityRef capability)
    {
        if (capability.CapabilityCase != ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest)
        {
            return new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = invocation.CallSiteId,
                Capability = capability.Clone(),
                ResponseProjection = invocation.ResponseProjection?.Clone(),
            };
        }

        var confirmations = request.ExplicitRequestConfirmations
            .Where(confirmation => string.Equals(
                confirmation.CallSiteId,
                invocation.CallSiteId,
                StringComparison.Ordinal))
            .ToArray();
        if (confirmations.Length == 0)
        {
            throw ExplicitRequestConfirmationFailure(
                invocation,
                capability,
                request.ExecutionMode,
                "NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED",
                "Confirm the exact explicit request contract before binding this workflow.");
        }
        if (confirmations.Length != 1)
        {
            throw ExplicitRequestConfirmationFailure(
                invocation,
                capability,
                request.ExecutionMode,
                "NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH",
                "Exactly one explicit request confirmation is required for this workflow call site.");
        }

        var confirmation = confirmations[0];
        var requestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeNyxIdRequestContractDigest(capability.NyxIdUserRequest.Request);
        if (!string.Equals(
                confirmation.RequestContractDigest,
                requestContractDigest,
                StringComparison.Ordinal))
        {
            throw ExplicitRequestConfirmationFailure(
                invocation,
                capability,
                request.ExecutionMode,
                "NYXID_EXPLICIT_REQUEST_CONFIRMATION_DIGEST_MISMATCH",
                "The explicit request contract changed after it was confirmed.");
        }
        if (confirmation.AttestedRisk != capability.NyxIdUserRequest.ExecutionPolicy?.Risk ||
            !IsAttestedRiskAllowed(capability.NyxIdUserRequest.Request, confirmation.AttestedRisk))
        {
            throw ExplicitRequestConfirmationFailure(
                invocation,
                capability,
                request.ExecutionMode,
                "NYXID_EXPLICIT_REQUEST_CONFIRMATION_RISK_MISMATCH",
                "The explicit request risk confirmation does not satisfy the request method policy.");
        }
        if (request.ExplicitRequestGrantMode == ExternalCapabilityExecutionMode.Durable &&
            (capability.NyxIdUserRequest.ExecutionPolicy is not { } executionPolicy ||
             !NyxIdRequestSelectorContract.SupportsDurableExecution(
                 capability.NyxIdUserRequest.Request.Method,
                 executionPolicy.Risk)))
        {
            throw ExplicitRequestConfirmationFailure(
                invocation,
                capability,
                request.ExecutionMode,
                "NYXID_EXPLICIT_REQUEST_INTERACTIVE_REQUIRED",
                "This explicit request can only be admitted for interactive execution.");
        }
        if (capability.NyxIdUserRequest.ExecutionPolicy?.AllowedExecutionModes.Contains(
                request.ExplicitRequestGrantMode) != true)
        {
            throw ExplicitRequestConfirmationFailure(
                invocation,
                capability,
                request.ExecutionMode,
                "NYXID_EXPLICIT_REQUEST_INTERACTIVE_REQUIRED",
                "The current explicit request capability does not allow the requested grant mode.");
        }
        if (string.IsNullOrWhiteSpace(request.Access.CallerId))
        {
            throw ExplicitRequestConfirmationFailure(
                invocation,
                capability,
                request.ExecutionMode,
                "NYXID_EXPLICIT_REQUEST_BINDER_REQUIRED",
                "An authenticated workflow binder is required to confirm an explicit request.");
        }

        var grant = new NyxIdExplicitRequestGrant
        {
            CallSiteId = invocation.CallSiteId,
            RequestContractDigest = requestContractDigest,
            GrantorAuthority = NyxIdExplicitRequestGrantorAuthority.AevatarWorkflowBinder,
            GrantorOwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            GrantorOwnerSubject = request.Access.CallerId,
            Risk = confirmation.AttestedRisk,
            WorkflowId = request.WorkflowId!,
            RevisionId = request.RevisionId!,
        };
        grant.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
        if (request.ExplicitRequestGrantMode == ExternalCapabilityExecutionMode.Durable)
            grant.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);

        var admittedCapability = capability.Clone();
        admittedCapability.NyxIdUserRequest.ExecutionPolicy = new NyxIdOperationExecutionPolicy
        {
            Risk = grant.Risk,
            Approval = NyxIdOperationApproval.None,
            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
        };
        admittedCapability.NyxIdUserRequest.ExecutionPolicy.AllowedExecutionModes.Add(
            grant.AllowedExecutionModes);
        admittedCapability.NyxIdUserRequest.ExplicitRequestGrantDigest =
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdExplicitRequestGrantDigest(grant);
        return new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = invocation.CallSiteId,
            Capability = admittedCapability,
            NyxIdExplicitRequestGrant = grant,
            ResponseProjection = invocation.ResponseProjection?.Clone(),
        };
    }

    private static void EnsureConfirmationCallSitesAreExpected(
        WorkflowExternalCapabilityAdmissionRequest request,
        IReadOnlyCollection<ExternalToolInvocationSpec> invocations)
    {
        var expectedCallSites = invocations
            .Where(static invocation =>
                invocation.Selector.SelectorCase ==
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest)
            .Select(static invocation => invocation.CallSiteId)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = request.ExplicitRequestConfirmations.FirstOrDefault(confirmation =>
            !expectedCallSites.Contains(confirmation.CallSiteId));
        if (unknown is null)
            return;

        throw ExplicitRequestConfirmationFailure(
            new ExternalToolInvocationSpec
            {
                CallSiteId = unknown.CallSiteId,
                Selector = new ExternalWorkflowCapabilitySelector(),
            },
            null,
            request.ExecutionMode,
            "NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH",
            "The explicit request confirmation does not match an explicit request call site in this workflow.");
    }

    private static void EnsureConfirmationBindingsMatch(
        WorkflowExternalCapabilityAdmissionRequest request)
    {
        var mismatch = request.ExplicitRequestConfirmations.FirstOrDefault(confirmation =>
            string.IsNullOrWhiteSpace(request.WorkflowId) ||
            string.IsNullOrWhiteSpace(request.RevisionId) ||
            string.IsNullOrWhiteSpace(confirmation.WorkflowId) ||
            !string.Equals(confirmation.WorkflowId, confirmation.WorkflowId.Trim(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(confirmation.RevisionId) ||
            !string.Equals(confirmation.RevisionId, confirmation.RevisionId.Trim(), StringComparison.Ordinal) ||
            !string.Equals(confirmation.WorkflowId, request.WorkflowId, StringComparison.Ordinal) ||
            !string.Equals(confirmation.RevisionId, request.RevisionId, StringComparison.Ordinal));
        if (mismatch is null)
            return;

        throw ExplicitRequestConfirmationFailure(
            new ExternalToolInvocationSpec
            {
                CallSiteId = mismatch.CallSiteId,
                Selector = new ExternalWorkflowCapabilitySelector(),
            },
            null,
            request.ExecutionMode,
            "NYXID_EXPLICIT_REQUEST_CONFIRMATION_BINDING_MISMATCH",
            "The explicit request confirmation does not match this workflow revision.");
    }

    private static bool IsAttestedRiskAllowed(
        NyxIdRequestSelector request,
        NyxIdOperationRisk risk) =>
        NyxIdRequestSelectorContract.IsRiskAttestationSatisfied(
            request.Method,
            request.Risk,
            risk);

    private static WorkflowExternalCapabilityAdmissionException ExplicitRequestConfirmationFailure(
        ExternalToolInvocationSpec invocation,
        ExternalWorkflowCapabilityRef? capability,
        ExternalCapabilityExecutionMode executionMode,
        string code,
        string safeMessage) =>
        new(ReadinessProofFailure(
            invocation.Selector,
            capability,
            executionMode,
            ExternalCapabilityReadinessStatus.ContractDrift,
            code,
            safeMessage));

    public async Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
        PersistedWorkflowCapabilityAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ValidatePersistedIntegrityAsync(request, cancellationToken);
        EnsureSourcesAreFresh(request.Plan);
        return request.Plan.Clone();
    }

    public async Task<WorkflowCapabilityAdmissionPlan> RefreshPersistedAsync(
        RefreshPersistedWorkflowCapabilityAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var persisted = request.Persisted;
        await ValidatePersistedIntegrityAsync(persisted, cancellationToken);

        var confirmations = request.ExplicitRequestConfirmations.Count > 0
            ? request.ExplicitRequestConfirmations
            : RestorePersistedExplicitRequestConfirmations(persisted, request.Access);
        var liveRequest = persisted.WorkflowYamls is { Count: > 0 } workflowYamls
            ? WorkflowExternalCapabilityAdmissionRequest.FromWorkflowYamls(
                request.Access,
                workflowYamls,
                persisted.SourceKind,
                persisted.ExpectedExecutionMode,
                confirmations,
                persisted.WorkflowId,
                persisted.RevisionId,
                persisted.ExpectedExecutionMode)
            : new WorkflowExternalCapabilityAdmissionRequest(
                request.Access,
                persisted.WorkflowYaml,
                persisted.InlineWorkflowYamls,
                persisted.SourceKind,
                persisted.ExpectedExecutionMode,
                confirmations,
                persisted.WorkflowId,
                persisted.RevisionId,
                persisted.ExpectedExecutionMode);
        return await AdmitAsync(liveRequest, cancellationToken);
    }

    private async Task ValidatePersistedIntegrityAsync(
        PersistedWorkflowCapabilityAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        if (WorkflowCapabilityAdmissionPlanIntegrity.RequiresRebind(request.Plan.SchemaVersion))
        {
            throw new WorkflowExternalCapabilityAdmissionException(
                BuildRebindRequiredReadiness(request.ExpectedExecutionMode));
        }

        var definition = await ParseDefinitionAsync(
            request.WorkflowYaml,
            request.InlineWorkflowYamls,
            request.WorkflowYamls,
            request.ExpectedExecutionMode,
            cancellationToken);
        try
        {
            WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
                request.Plan,
                definition.WorkflowYaml,
                definition.InlineWorkflowYamls,
                request.ExpectedExecutionMode,
                definition.Invocations,
                request.WorkflowId,
                request.RevisionId);
        }
        catch (WorkflowCapabilityAdmissionRebindRequiredException)
        {
            throw new WorkflowExternalCapabilityAdmissionException(
                BuildRebindRequiredReadiness(request.ExpectedExecutionMode));
        }
        EnsureDurableCatalogMatchesPlanOwner(request.Plan);
    }

    private static IReadOnlyList<NyxIdExplicitRequestConfirmation>
        RestorePersistedExplicitRequestConfirmations(
            PersistedWorkflowCapabilityAdmissionRequest request,
            ExternalWorkflowCapabilityAccessContext access)
    {
        var confirmations = new List<NyxIdExplicitRequestConfirmation>();
        foreach (var admission in request.Plan.InvocationAdmissions.Where(static admission =>
                     admission.Capability?.CapabilityCase ==
                     ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest))
        {
            var grant = admission.NyxIdExplicitRequestGrant;
            if (grant is null ||
                grant.GrantorAuthority !=
                NyxIdExplicitRequestGrantorAuthority.AevatarWorkflowBinder ||
                grant.GrantorOwnerKind != ExternalCapabilityAuthorizationOwnerKind.Personal ||
                string.IsNullOrWhiteSpace(access.CallerId) ||
                !string.Equals(grant.GrantorOwnerSubject, access.CallerId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(request.WorkflowId) ||
                string.IsNullOrWhiteSpace(request.RevisionId) ||
                !string.Equals(grant.WorkflowId, request.WorkflowId, StringComparison.Ordinal) ||
                !string.Equals(grant.RevisionId, request.RevisionId, StringComparison.Ordinal))
            {
                throw new WorkflowExternalCapabilityAdmissionException(
                    BuildRebindRequiredReadiness(request.ExpectedExecutionMode));
            }

            confirmations.Add(new NyxIdExplicitRequestConfirmation
            {
                CallSiteId = grant.CallSiteId,
                RequestContractDigest = grant.RequestContractDigest,
                AttestedRisk = grant.Risk,
                WorkflowId = grant.WorkflowId,
                RevisionId = grant.RevisionId,
            });
        }

        return confirmations;
    }

    private static ExternalCapabilityReadiness BuildNyxIdOperationSelectionRequiredReadiness(
        ExternalCapabilityExecutionMode executionMode)
    {
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = ExternalCapabilityReadinessStatus.OperationSelectionRequired,
        };
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = ExternalCapabilityReadinessStatus.OperationSelectionRequired,
            Code = "NYXID_OPERATION_SELECTION_REQUIRED",
            SafeMessage = "Select an exact connected service operation.",
        });
        readiness.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = ExternalCapabilityRemediationActionKind.SelectOperation,
            Label = "Select operation",
        });
        return readiness;
    }

    private static ExternalCapabilityReadiness BuildRebindRequiredReadiness(
        ExternalCapabilityExecutionMode executionMode)
    {
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
        };
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
            Code = WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode,
            SafeMessage = "The persisted workflow capability admission plan must be rebound.",
        });
        readiness.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = ExternalCapabilityRemediationActionKind.RebindWorkflow,
            Label = "Rebind workflow",
        });
        return readiness;
    }

    private static ExternalCapabilityReadiness? ValidateReadinessIdentityProof(
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadiness readiness)
    {
        if (readiness.ExecutionMode != executionMode)
        {
            return ReadinessProofFailure(
                selector,
                readiness.SelectedCapability,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "READINESS_EXECUTION_MODE_MISMATCH",
                "External capability readiness was evaluated for a different execution mode.");
        }

        if (readiness.SelectedSelector is null ||
            !string.Equals(
                WorkflowCapabilityAdmissionPlanIntegrity.SelectorKey(readiness.SelectedSelector),
                WorkflowCapabilityAdmissionPlanIntegrity.SelectorKey(selector),
                StringComparison.Ordinal) ||
            !WorkflowCapabilityAdmissionPlanIntegrity.SelectorMatchesCapability(
                selector,
                readiness.SelectedCapability))
        {
            return ReadinessProofFailure(
                selector,
                readiness.SelectedCapability,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "READINESS_SELECTOR_PROOF_MISMATCH",
                "External capability readiness proof does not match the selected operation.");
        }

        if (readiness.Sources.Count == 0)
        {
            return ReadinessProofFailure(
                selector,
                readiness.SelectedCapability,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "READINESS_SOURCE_REQUIRED",
                "External capability convergence requires current source evidence.");
        }

        return null;
    }

    private static ExternalCapabilityReadiness? ValidateConvergenceReadinessProof(
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadiness readiness)
    {
        if (readiness.ExecutionMode != executionMode)
        {
            return ReadinessProofFailure(
                selector,
                readiness.SelectedCapability,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "READINESS_EXECUTION_MODE_MISMATCH",
                "External capability readiness was evaluated for a different execution mode.");
        }

        if (readiness.SelectedSelector is null ||
            !string.Equals(
                WorkflowCapabilityAdmissionPlanIntegrity.SelectorKey(readiness.SelectedSelector),
                WorkflowCapabilityAdmissionPlanIntegrity.SelectorKey(selector),
                StringComparison.Ordinal))
        {
            return ReadinessProofFailure(
                selector,
                readiness.SelectedCapability,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "READINESS_SELECTOR_PROOF_MISMATCH",
                "External capability readiness proof does not match the selected operation.");
        }

        return null;
    }

    private static ExternalCapabilityReadiness? ValidateReadinessSourceProof(
        ExternalWorkflowCapabilityAccessContext access,
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadiness readiness)
    {
        var capability = readiness.SelectedCapability;

        if (WorkflowCapabilityAdmissionPlanIntegrity.RequiresDurableAuthorizationCatalog(
                executionMode,
                [capability]) &&
            !WorkflowCapabilityAdmissionPlanIntegrity.HasDurableAuthorizationCatalogSource(readiness.Sources))
        {
            return ReadinessProofFailure(
                selector,
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
                "DURABLE_AUTHORIZATION_SOURCE_REQUIRED",
                "Durable NyxID capability admission requires current authorization catalog evidence.");
        }

        if (WorkflowCapabilityAdmissionPlanIntegrity.RequiresDurableAuthorizationCatalog(
                executionMode,
                [capability]) &&
            !WorkflowCapabilityAdmissionPlanIntegrity.HasDurableAuthorizationCatalogSource(
                readiness.Sources,
                ExpectedDurableCatalogSourceId(access)))
        {
            return ReadinessProofFailure(
                selector,
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
                "DURABLE_AUTHORIZATION_SOURCE_MISMATCH",
                "Durable NyxID authorization evidence belongs to a different caller.");
        }

        if (!WorkflowCapabilityAdmissionPlanIntegrity.HasRequiredSourceEvidence(
                executionMode,
                [capability],
                readiness.Sources))
        {
            return ReadinessProofFailure(
                selector,
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "READINESS_SOURCE_REQUIRED",
                "External capability readiness is missing required source evidence.");
        }

        return null;
    }

    private static ExternalCapabilityReadiness ReadinessProofFailure(
        ExternalWorkflowCapabilitySelector selector,
        ExternalWorkflowCapabilityRef? capability,
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadinessStatus status,
        string code,
        string safeMessage)
    {
        var failure = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = status,
            SelectedSelector = selector.Clone(),
            SelectedCapability = capability?.Clone(),
        };
        failure.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = status,
            Code = code,
            SafeMessage = safeMessage,
        });
        failure.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = ExternalCapabilityRemediationActionKind.RefreshSource,
            Label = "Re-evaluate capability",
        });
        return failure;
    }

    private async Task<ParsedAdmissionDefinition> ParseDefinitionAsync(
        string workflowYaml,
        IReadOnlyDictionary<string, string> inlineWorkflowYamls,
        IReadOnlyList<string>? workflowYamls,
        ExternalCapabilityExecutionMode executionMode,
        CancellationToken cancellationToken)
    {
        if (workflowYamls is not null)
            return await ParseWorkflowBundleAsync(workflowYamls, executionMode, cancellationToken);

        var definitions = new List<(string Key, string Yaml)>
        {
            ("root", workflowYaml),
        };
        definitions.AddRange(inlineWorkflowYamls
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => (item.Key, item.Value)));

        var invocations = new List<ExternalToolInvocationSpec>();
        foreach (var (key, yaml) in definitions)
        {
            var parse = await _parser.ParseWorkflowYamlAsync(yaml, cancellationToken);
            if (!parse.Succeeded)
            {
                ThrowTypedAuthoringFailure(parse, executionMode);
                throw new InvalidOperationException(
                    $"Workflow definition '{key}' is invalid: {parse.Error}");
            }

            AddInvocations(invocations, parse);
        }

        return new ParsedAdmissionDefinition(
            workflowYaml,
            inlineWorkflowYamls,
            SortInvocations(invocations));
    }

    private async Task<ParsedAdmissionDefinition> ParseWorkflowBundleAsync(
        IReadOnlyList<string> workflowYamls,
        ExternalCapabilityExecutionMode executionMode,
        CancellationToken cancellationToken)
    {
        string? rootYaml = null;
        var inlineWorkflowYamls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var workflowNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invocations = new List<ExternalToolInvocationSpec>();

        for (var index = 0; index < workflowYamls.Count; index++)
        {
            var yaml = workflowYamls[index]?.Trim() ?? string.Empty;
            if (yaml.Length == 0)
                throw new InvalidOperationException("Workflow YAML bundle must not contain empty definitions.");

            var parse = await _parser.ParseWorkflowYamlAsync(yaml, cancellationToken);
            if (!parse.Succeeded)
            {
                ThrowTypedAuthoringFailure(parse, executionMode);
                throw new InvalidOperationException(
                    $"Workflow definition at index {index} is invalid: {parse.Error}");
            }

            var workflowName = parse.WorkflowName?.Trim() ?? string.Empty;
            if (workflowName.Length == 0)
                throw new InvalidOperationException($"Workflow definition at index {index} has no workflow name.");
            if (!workflowNames.Add(workflowName))
                throw new InvalidOperationException($"Duplicate workflow name '{workflowName}' in workflow YAML bundle.");

            if (index == 0)
                rootYaml = yaml;
            else
                inlineWorkflowYamls.Add(workflowName, yaml);

            AddInvocations(invocations, parse);
        }

        return new ParsedAdmissionDefinition(
            rootYaml ?? throw new InvalidOperationException("Workflow YAML bundle has no root definition."),
            inlineWorkflowYamls,
            SortInvocations(invocations));
    }

    private static void ThrowTypedAuthoringFailure(
        WorkflowYamlParseResult parse,
        ExternalCapabilityExecutionMode executionMode)
    {
        if (parse.ExternalCapabilityReadiness is null)
            return;

        var readiness = parse.ExternalCapabilityReadiness.Clone();
        readiness.ExecutionMode = executionMode;
        throw new WorkflowExternalCapabilityAdmissionException(readiness);
    }

    private static void AddInvocations(
        ICollection<ExternalToolInvocationSpec> invocations,
        WorkflowYamlParseResult parse)
    {
        foreach (var invocation in parse.AuthorizationDependencies?.ExternalInvocations ?? [])
            invocations.Add(invocation.Clone());
    }

    private static IReadOnlyList<ExternalToolInvocationSpec> SortInvocations(
        IEnumerable<ExternalToolInvocationSpec> invocations)
    {
        var sorted = invocations
            .OrderBy(static invocation => invocation.CallSiteId, StringComparer.Ordinal)
            .ToArray();
        var duplicate = sorted
            .GroupBy(static invocation => invocation.CallSiteId, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Workflow external capability call site '{duplicate.Key}' is duplicated.");
        }

        return sorted;
    }

    private static void EnsureDurableCatalogMatchesPlanOwner(
        WorkflowCapabilityAdmissionPlan plan)
    {
        if (!WorkflowCapabilityAdmissionPlanIntegrity.RequiresDurableAuthorizationCatalog(
                plan.ExecutionMode,
                WorkflowCapabilityAdmissionPlanIntegrity.DistinctCapabilities(plan)))
        {
            return;
        }

        if (!WorkflowCapabilityAdmissionPlanIntegrity.HasDurableAuthorizationCatalogSource(
                plan.SourceStamps,
                ExpectedDurableCatalogSourceId(plan.DurableAuthorizationOwner)))
        {
            throw new InvalidOperationException(
                "Workflow capability admission durable authorization catalog source does not match the persisted owner.");
        }
    }

    private static string ExpectedDurableCatalogSourceId(ExternalWorkflowCapabilityAccessContext access)
    {
        if (string.IsNullOrWhiteSpace(access.CallerId))
            return string.Empty;

        return ExpectedDurableCatalogSourceId(new ExternalCapabilityAuthorizationOwner
        {
            Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
            OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            OwnerSubject = access.CallerId,
        });
    }

    private static string ExpectedDurableCatalogSourceId(
        ExternalCapabilityAuthorizationOwner? owner)
    {
        if (!WorkflowCapabilityAdmissionPlanIntegrity.IsCanonicalDurableAuthorizationOwner(owner))
            return string.Empty;

        return NyxIdAuthorizationCatalogActorIds.Build(new AuthorizationOwnerIdentity
        {
            Authority = owner!.Authority,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = owner.OwnerSubject,
        });
    }

    private static ExternalCapabilityAuthorizationOwner? BuildDurableAuthorizationOwner(
        ExternalWorkflowCapabilityAccessContext access,
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<ExternalWorkflowCapabilityRef> capabilities)
    {
        if (!WorkflowCapabilityAdmissionPlanIntegrity.RequiresDurableAuthorizationCatalog(
                executionMode,
                capabilities))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(access.CallerId))
            throw new InvalidOperationException("Verified caller is required for durable NyxID admission.");

        return new ExternalCapabilityAuthorizationOwner
        {
            Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
            OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            OwnerSubject = access.CallerId,
        };
    }

    private void EnsureSourcesAreFresh(WorkflowCapabilityAdmissionPlan plan) =>
        EnsureSourcesAreFresh(plan.SourceStamps, plan.ExecutionMode);

    private void EnsureSourcesAreFresh(
        IEnumerable<ExternalCapabilitySourceStamp> sources,
        ExternalCapabilityExecutionMode executionMode,
        ExternalWorkflowCapabilitySelector? selectedSelector = null,
        ExternalWorkflowCapabilityRef? selectedCapability = null)
    {
        var now = _timeProvider.GetUtcNow();
        var stale = sources.FirstOrDefault(source => !IsFresh(source, now));
        if (stale is null)
            return;

        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = ExternalCapabilityReadinessStatus.SourceStale,
            SelectedSelector = selectedSelector?.Clone(),
            SelectedCapability = selectedCapability?.Clone(),
        };
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = readiness.Status,
            Code = "ADMISSION_SOURCE_STALE",
            SafeMessage = "External capability admission evidence is stale.",
        });
        readiness.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = ExternalCapabilityRemediationActionKind.RefreshSource,
            Label = "Refresh capability readiness",
        });
        throw new WorkflowExternalCapabilityAdmissionException(readiness);
    }

    private static bool IsFresh(ExternalCapabilitySourceStamp source, DateTimeOffset now)
    {
        if (source.ObservedAt is null || source.FreshUntil is null)
            return false;

        try
        {
            return source.ObservedAt.ToDateTimeOffset() <= now &&
                   source.FreshUntil.ToDateTimeOffset() > now;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed record ParsedAdmissionDefinition(
        string WorkflowYaml,
        IReadOnlyDictionary<string, string> InlineWorkflowYamls,
        IReadOnlyList<ExternalToolInvocationSpec> Invocations);
}
