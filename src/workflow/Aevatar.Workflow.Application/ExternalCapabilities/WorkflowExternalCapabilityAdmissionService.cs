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

    public WorkflowExternalCapabilityAdmissionService(
        IWorkflowDefinitionParser parser,
        IExternalWorkflowCapabilityReadinessPort readinessPort,
        TimeProvider? timeProvider = null)
    {
        _parser = parser;
        _readinessPort = readinessPort;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
        WorkflowExternalCapabilityAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExecutionMode == ExternalCapabilityExecutionMode.Unspecified)
            throw new InvalidOperationException("External capability execution mode is required.");

        var definition = await ParseDefinitionAsync(
            request.WorkflowYaml,
            request.InlineWorkflowYamls,
            request.WorkflowYamls,
            request.ExecutionMode,
            cancellationToken);

        EnsureConfirmationBindingsMatch(request);
        EnsureConfirmationCallSitesAreExpected(request, definition.Invocations);

        var admissions = new List<WorkflowCapabilityInvocationAdmission>();
        var sources = new List<ExternalCapabilitySourceStamp>();
        foreach (var invocation in definition.Invocations)
        {
            if (WorkflowAuthorizationDependencyEvaluator.RequiresExternalCapabilityAdmission(invocation.ToolName) &&
                invocation.Selector.SelectorCase ==
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.None)
            {
                throw new WorkflowExternalCapabilityAdmissionException(
                    BuildNyxIdOperationSelectionRequiredReadiness(request.ExecutionMode));
            }

            if (RequiresInteractiveExplicitRequest(request.ExecutionMode, invocation.Selector))
            {
                throw ExplicitRequestConfirmationFailure(
                    invocation,
                    null,
                    request.ExecutionMode,
                    "NYXID_EXPLICIT_REQUEST_INTERACTIVE_REQUIRED",
                    "This explicit request can only be admitted for interactive execution.");
            }

            var readiness = await _readinessPort.InspectAsync(
                new InspectExternalWorkflowCapabilityReadinessRequest(
                    request.Access,
                    invocation.Selector,
                    request.ExecutionMode),
                cancellationToken);
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
            admissions.Add(admission);
            sources.AddRange(readiness.Sources.Select(static source => source.Clone()));
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
            !IsAttestedRiskAllowed(capability.NyxIdUserRequest.Request.Method, confirmation.AttestedRisk))
        {
            throw ExplicitRequestConfirmationFailure(
                invocation,
                capability,
                request.ExecutionMode,
                "NYXID_EXPLICIT_REQUEST_CONFIRMATION_RISK_MISMATCH",
                "The explicit request risk confirmation does not satisfy the request method policy.");
        }
        if (request.ExecutionMode == ExternalCapabilityExecutionMode.Durable &&
            capability.NyxIdUserRequest.ExecutionPolicy?.Risk is
                NyxIdOperationRisk.Write or NyxIdOperationRisk.Destructive)
        {
            throw ExplicitRequestConfirmationFailure(
                invocation,
                capability,
                request.ExecutionMode,
                "NYXID_EXPLICIT_REQUEST_INTERACTIVE_REQUIRED",
                "This explicit request can only be admitted for interactive execution.");
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
        if (request.ExecutionMode == ExternalCapabilityExecutionMode.Durable)
            grant.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);

        var admittedCapability = capability.Clone();
        admittedCapability.NyxIdUserRequest.ExecutionPolicy = new NyxIdOperationExecutionPolicy
        {
            Risk = grant.Risk,
            Approval = grant.Risk == NyxIdOperationRisk.ReadOnly
                ? NyxIdOperationApproval.None
                : NyxIdOperationApproval.Required,
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

    private static bool RequiresInteractiveExplicitRequest(
        ExternalCapabilityExecutionMode executionMode,
        ExternalWorkflowCapabilitySelector selector) =>
        executionMode == ExternalCapabilityExecutionMode.Durable &&
        selector.SelectorCase == ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest &&
        selector.NyxIdRequest.Method is NyxIdRequestMethod.Post or NyxIdRequestMethod.Put or
            NyxIdRequestMethod.Patch or NyxIdRequestMethod.Delete;

    private static bool IsAttestedRiskAllowed(
        NyxIdRequestMethod method,
        NyxIdOperationRisk risk) => method switch
    {
        NyxIdRequestMethod.Get or NyxIdRequestMethod.Head or NyxIdRequestMethod.Options =>
            risk is NyxIdOperationRisk.ReadOnly or NyxIdOperationRisk.Write or NyxIdOperationRisk.Destructive,
        NyxIdRequestMethod.Post or NyxIdRequestMethod.Put or NyxIdRequestMethod.Patch =>
            risk is NyxIdOperationRisk.Write or NyxIdOperationRisk.Destructive,
        NyxIdRequestMethod.Delete => risk == NyxIdOperationRisk.Destructive,
        _ => false,
    };

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
        EnsureSourcesAreFresh(request.Plan);
        return request.Plan.Clone();
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
