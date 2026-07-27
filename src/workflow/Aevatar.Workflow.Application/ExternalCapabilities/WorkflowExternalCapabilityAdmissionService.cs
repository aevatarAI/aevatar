using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;

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
            cancellationToken);

        var admissions = new List<WorkflowCapabilityInvocationAdmission>();
        var sources = new List<ExternalCapabilitySourceStamp>();
        foreach (var invocation in definition.Invocations)
        {
            var readiness = await _readinessPort.InspectAsync(
                new InspectExternalWorkflowCapabilityReadinessRequest(
                    request.Access,
                    invocation.Selector,
                    request.ExecutionMode),
                cancellationToken);
            if (readiness.Status != ExternalCapabilityReadinessStatus.Ready)
                throw new WorkflowExternalCapabilityAdmissionException(readiness);
            var proofFailure = ValidateReadinessProof(
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
            admissions.Add(new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = invocation.CallSiteId,
                Capability = readiness.SelectedCapability.Clone(),
            });
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
                admissions.Select(static admission => admission.Capability)));
    }

    public async Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
        PersistedWorkflowCapabilityAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var definition = await ParseDefinitionAsync(
            request.WorkflowYaml,
            request.InlineWorkflowYamls,
            request.WorkflowYamls,
            cancellationToken);
        WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
            request.Plan,
            definition.WorkflowYaml,
            definition.InlineWorkflowYamls,
            request.ExpectedExecutionMode,
            definition.Invocations);
        EnsureDurableCatalogMatchesPlanOwner(request.Plan);
        EnsureSourcesAreFresh(request.Plan);
        return request.Plan.Clone();
    }

    private static ExternalCapabilityReadiness? ValidateReadinessProof(
        ExternalWorkflowCapabilityAccessContext access,
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
        CancellationToken cancellationToken)
    {
        if (workflowYamls is not null)
            return await ParseWorkflowBundleAsync(workflowYamls, cancellationToken);

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
