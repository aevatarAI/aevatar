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

        var sources = new List<ExternalCapabilitySourceStamp>();
        foreach (var capability in definition.Capabilities)
        {
            var readiness = await _readinessPort.InspectAsync(
                new InspectExternalWorkflowCapabilityReadinessRequest(
                    request.Access,
                    capability,
                    request.ExecutionMode),
                cancellationToken);
            if (readiness.Status != ExternalCapabilityReadinessStatus.Ready)
                throw new WorkflowExternalCapabilityAdmissionException(readiness);
            var proofFailure = ValidateReadinessProof(
                request.Access,
                capability,
                request.ExecutionMode,
                readiness);
            if (proofFailure is not null)
                throw new WorkflowExternalCapabilityAdmissionException(proofFailure);
            EnsureSourcesAreFresh(readiness.Sources, request.ExecutionMode, capability);
            sources.AddRange(readiness.Sources.Select(static source => source.Clone()));
        }

        return WorkflowCapabilityAdmissionPlanIntegrity.Create(
            definition.WorkflowYaml,
            definition.InlineWorkflowYamls,
            request.ExecutionMode,
            definition.Capabilities,
            sources,
            BuildDurableAuthorizationOwner(
                request.Access,
                request.ExecutionMode,
                definition.Capabilities));
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
            definition.Capabilities);
        EnsureDurableCatalogMatchesPlanOwner(request.Plan);
        EnsureSourcesAreFresh(request.Plan);
        return request.Plan.Clone();
    }

    private static ExternalCapabilityReadiness? ValidateReadinessProof(
        ExternalWorkflowCapabilityAccessContext access,
        ExternalWorkflowCapabilityRef capability,
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadiness readiness)
    {
        if (readiness.ExecutionMode != executionMode)
        {
            return ReadinessProofFailure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "READINESS_EXECUTION_MODE_MISMATCH",
                "External capability readiness was evaluated for a different execution mode.");
        }

        if (!string.Equals(
                WorkflowCapabilityAdmissionPlanIntegrity.CapabilityKey(readiness.SelectedCapability),
                WorkflowCapabilityAdmissionPlanIntegrity.CapabilityKey(capability),
                StringComparison.Ordinal))
        {
            return ReadinessProofFailure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "READINESS_CAPABILITY_MISMATCH",
                "External capability readiness was evaluated for a different capability.");
        }

        if (WorkflowCapabilityAdmissionPlanIntegrity.RequiresDurableAuthorizationCatalog(
                executionMode,
                [capability]) &&
            !WorkflowCapabilityAdmissionPlanIntegrity.HasDurableAuthorizationCatalogSource(readiness.Sources))
        {
            return ReadinessProofFailure(
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
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "READINESS_SOURCE_REQUIRED",
                "External capability readiness is missing required source evidence.");
        }

        return null;
    }

    private static ExternalCapabilityReadiness ReadinessProofFailure(
        ExternalWorkflowCapabilityRef capability,
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadinessStatus status,
        string code,
        string safeMessage)
    {
        var failure = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = status,
            SelectedCapability = capability.Clone(),
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

        var capabilities = new Dictionary<string, ExternalWorkflowCapabilityRef>(StringComparer.Ordinal);
        foreach (var (key, yaml) in definitions)
        {
            var parse = await _parser.ParseWorkflowYamlAsync(yaml, cancellationToken);
            if (!parse.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Workflow definition '{key}' is invalid: {parse.Error}");
            }

            AddCapabilities(capabilities, parse);
        }

        return new ParsedAdmissionDefinition(
            workflowYaml,
            inlineWorkflowYamls,
            SortCapabilities(capabilities));
    }

    private async Task<ParsedAdmissionDefinition> ParseWorkflowBundleAsync(
        IReadOnlyList<string> workflowYamls,
        CancellationToken cancellationToken)
    {
        string? rootYaml = null;
        var inlineWorkflowYamls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var workflowNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var capabilities = new Dictionary<string, ExternalWorkflowCapabilityRef>(StringComparer.Ordinal);

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

            AddCapabilities(capabilities, parse);
        }

        return new ParsedAdmissionDefinition(
            rootYaml ?? throw new InvalidOperationException("Workflow YAML bundle has no root definition."),
            inlineWorkflowYamls,
            SortCapabilities(capabilities));
    }

    private static void AddCapabilities(
        IDictionary<string, ExternalWorkflowCapabilityRef> capabilities,
        WorkflowYamlParseResult parse)
    {
        foreach (var capability in parse.AuthorizationDependencies?.ExternalCapabilities ?? [])
        {
            capabilities.TryAdd(
                WorkflowCapabilityAdmissionPlanIntegrity.CapabilityKey(capability),
                capability.Clone());
        }
    }

    private static IReadOnlyList<ExternalWorkflowCapabilityRef> SortCapabilities(
        IReadOnlyDictionary<string, ExternalWorkflowCapabilityRef> capabilities) =>
        capabilities.Values
            .OrderBy(WorkflowCapabilityAdmissionPlanIntegrity.CapabilityKey, StringComparer.Ordinal)
            .ToArray();

    private static void EnsureDurableCatalogMatchesPlanOwner(
        WorkflowCapabilityAdmissionPlan plan)
    {
        if (!WorkflowCapabilityAdmissionPlanIntegrity.RequiresDurableAuthorizationCatalog(
                plan.ExecutionMode,
                plan.ExternalCapabilities))
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
        IReadOnlyList<ExternalWorkflowCapabilityRef> Capabilities);
}
