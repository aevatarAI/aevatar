using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;

namespace Aevatar.Workflow.Abstractions;

public enum WorkflowCapabilityAdmissionCompatibilityFailure
{
    None = 0,
    RebindRequiredSchema = 1,
    SchemaMismatch = 2,
    ExecutionModeMismatch = 3,
    DefinitionDigestMismatch = 4,
    InvocationMismatch = 5,
    InvocationOrderingInvalid = 6,
    AdmissionProofInvalid = 7,
    DurableOwnerInvalid = 8,
    RequiredSourceMissing = 9,
    AdmissionDigestMismatch = 10,
}

public sealed record WorkflowCapabilityAdmissionCompatibilityResult(
    WorkflowCapabilityAdmissionCompatibilityFailure Failure)
{
    public bool Succeeded => Failure == WorkflowCapabilityAdmissionCompatibilityFailure.None;
}

public static class WorkflowCapabilityAdmissionPlanIntegrity
{
    public const string SchemaVersion = "external-capability-admission.v6";
    public const string LegacySchemaVersion = "external-capability-admission.v2";
    public const string OpenApiSchemaVersion = "external-capability-admission.v3";
    public const string PreviousSchemaVersion = "external-capability-admission.v4";
    public const string CodeRouteSchemaVersion = "external-capability-admission.v5";
    public const string RebindRequiredCode = "CAPABILITY_ADMISSION_REBIND_REQUIRED";
    public const string NyxIdAuthority = "nyxid";

    public static bool RequiresExplicitRequestBindingIdentity(WorkflowCapabilityAdmissionPlan? plan) =>
        plan?.InvocationAdmissions.Any(static admission => admission.NyxIdExplicitRequestGrant is not null) == true;

    public static WorkflowCapabilityAdmissionPlan Create(
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<WorkflowCapabilityInvocationAdmission> invocationAdmissions,
        IEnumerable<ExternalCapabilitySourceStamp> sourceStamps,
        ExternalCapabilityAuthorizationOwner? durableAuthorizationOwner = null,
        string? workflowId = null,
        string? revisionId = null)
    {
        var plan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = SchemaVersion,
            ExecutionMode = executionMode,
            DurableAuthorizationOwner = durableAuthorizationOwner?.Clone(),
        };
        var admissions = invocationAdmissions
            .Select(static admission => admission.Clone())
            .OrderBy(static admission => admission.CallSiteId, StringComparer.Ordinal)
            .ToArray();
        ValidateInvocationAdmissions(admissions, executionMode);
        var bindingIdentity = ResolveExplicitRequestBindingIdentity(
            admissions,
            workflowId,
            revisionId);
        plan.DefinitionDigest = bindingIdentity is null
            ? ComputeDefinitionDigest(workflowYaml, inlineWorkflowYamls)
            : ComputeDefinitionDigest(
                workflowYaml,
                inlineWorkflowYamls,
                bindingIdentity.Value.WorkflowId,
                bindingIdentity.Value.RevisionId);
        plan.InvocationAdmissions.Add(admissions);
        plan.SourceStamps.Add(
            sourceStamps
                .Select(static source => source.Clone())
                .GroupBy(SourceKey, StringComparer.Ordinal)
                .Select(static group => group.First())
                .OrderBy(SourceKey, StringComparer.Ordinal));
        plan.AdmissionDigest = ComputeAdmissionDigest(plan);
        return plan;
    }

    public static string ComputeDefinitionDigest(
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls)
    {
        var components = new List<string?>
        {
            "workflow-definition.v1",
            workflowYaml ?? string.Empty,
        };
        foreach (var (name, yaml) in (inlineWorkflowYamls ?? new Dictionary<string, string>())
                     .OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            components.Add(name);
            components.Add(yaml);
        }

        return ComputeLengthPrefixedDigest(components);
    }

    public static string ComputeDefinitionDigest(
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        string workflowId,
        string revisionId)
    {
        ValidateBindingIdentity(workflowId, nameof(workflowId));
        ValidateBindingIdentity(revisionId, nameof(revisionId));
        var components = new List<string?>
        {
            "workflow-definition.v2",
            workflowId,
            revisionId,
            workflowYaml ?? string.Empty,
        };
        foreach (var (name, yaml) in (inlineWorkflowYamls ?? new Dictionary<string, string>())
                     .OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            components.Add(name);
            components.Add(yaml);
        }

        return ComputeLengthPrefixedDigest(components);
    }

    public static string ComputeAdmissionDigest(WorkflowCapabilityAdmissionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var canonical = plan.Clone();
        canonical.AdmissionDigest = string.Empty;
        return Convert.ToHexStringLower(SHA256.HashData(canonical.ToByteArray()));
    }

    public static WorkflowCapabilityAdmissionPlan RebindExplicitRequestBindingIdentity(
        WorkflowCapabilityAdmissionPlan plan,
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        string workflowId,
        string revisionId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateBindingIdentity(workflowId, nameof(workflowId));
        ValidateBindingIdentity(revisionId, nameof(revisionId));
        if (!IsSupportedSchemaVersion(plan.SchemaVersion) ||
            plan.ExternalCapabilities.Count != 0 ||
            plan.ExecutionMode is not (ExternalCapabilityExecutionMode.Interactive or
                ExternalCapabilityExecutionMode.Durable) ||
            !FixedTimeEquals(plan.AdmissionDigest, ComputeAdmissionDigest(plan)))
        {
            throw new InvalidOperationException(
                "Workflow capability admission plan cannot be rebound because its integrity is invalid.");
        }

        var admissions = plan.InvocationAdmissions.ToArray();
        ValidateInvocationAdmissions(admissions, plan.ExecutionMode);
        if (!IsSortedByCallSite(admissions))
        {
            throw new InvalidOperationException(
                "Workflow capability invocation admissions are not canonically ordered.");
        }

        var firstGrant = admissions
            .Select(static admission => admission.NyxIdExplicitRequestGrant)
            .FirstOrDefault(static grant => grant is not null)
            ?? throw new InvalidOperationException(
                "Workflow NyxID explicit request binding identity is required.");
        var currentIdentity = ResolveExplicitRequestBindingIdentity(
            admissions,
            firstGrant.WorkflowId,
            firstGrant.RevisionId)!.Value;
        if (!FixedTimeEquals(
                plan.DefinitionDigest,
                ComputeDefinitionDigest(
                    workflowYaml,
                    inlineWorkflowYamls,
                    currentIdentity.WorkflowId,
                    currentIdentity.RevisionId)))
        {
            throw new InvalidOperationException(
                "Workflow capability admission definition digest does not match its current binding identity.");
        }

        var rebound = plan.Clone();
        foreach (var admission in rebound.InvocationAdmissions.Where(static admission =>
                     admission.NyxIdExplicitRequestGrant is not null))
        {
            var grant = admission.NyxIdExplicitRequestGrant;
            grant.WorkflowId = workflowId;
            grant.RevisionId = revisionId;
            admission.Capability.NyxIdUserRequest.ExplicitRequestGrantDigest =
                ComputeNyxIdExplicitRequestGrantDigest(grant);
        }

        rebound.DefinitionDigest = ComputeDefinitionDigest(
            workflowYaml,
            inlineWorkflowYamls,
            workflowId,
            revisionId);
        rebound.AdmissionDigest = ComputeAdmissionDigest(rebound);
        ValidateInvocationAdmissions(rebound.InvocationAdmissions.ToArray(), rebound.ExecutionMode);
        return rebound;
    }

    public static void ValidateOrThrow(
        WorkflowCapabilityAdmissionPlan plan,
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<ExternalToolInvocationSpec> expectedInvocations,
        string? workflowId = null,
        string? revisionId = null)
    {
        var evaluation = EvaluateCompatibility(
            plan,
            workflowYaml,
            inlineWorkflowYamls,
            executionMode,
            expectedInvocations,
            workflowId,
            revisionId);
        if (evaluation.Result.Succeeded)
            return;
        throw evaluation.Exception ??
              new InvalidOperationException("Workflow capability admission compatibility validation failed.");
    }

    public static WorkflowCapabilityAdmissionCompatibilityResult CheckCompatibility(
        WorkflowCapabilityAdmissionPlan plan,
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<ExternalToolInvocationSpec> expectedInvocations,
        string? workflowId = null,
        string? revisionId = null) =>
        EvaluateCompatibility(
            plan,
            workflowYaml,
            inlineWorkflowYamls,
            executionMode,
            expectedInvocations,
            workflowId,
            revisionId).Result;

    private static CompatibilityEvaluation EvaluateCompatibility(
        WorkflowCapabilityAdmissionPlan plan,
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<ExternalToolInvocationSpec> expectedInvocations,
        string? workflowId,
        string? revisionId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (RequiresRebind(plan.SchemaVersion))
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.RebindRequiredSchema,
                new WorkflowCapabilityAdmissionRebindRequiredException());
        }
        if (!IsSupportedSchemaVersion(plan.SchemaVersion))
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.SchemaMismatch,
                new InvalidOperationException("Workflow capability admission schema version is invalid."));
        }
        if (plan.ExternalCapabilities.Count != 0)
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.SchemaMismatch,
                new InvalidOperationException(
                    "Workflow capability admission cannot contain legacy external capabilities."));
        }

        if (executionMode == ExternalCapabilityExecutionMode.Unspecified ||
            plan.ExecutionMode != executionMode)
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.ExecutionModeMismatch,
                new InvalidOperationException(
                    "Workflow capability admission execution mode does not match the binding request."));
        }

        var expected = (expectedInvocations ?? throw new ArgumentNullException(nameof(expectedInvocations)))
            .Select(static invocation => invocation.Clone())
            .OrderBy(static invocation => invocation.CallSiteId, StringComparer.Ordinal)
            .ToArray();
        if (!string.Equals(plan.SchemaVersion, SchemaVersion, StringComparison.Ordinal) &&
            (expected.Any(static invocation => invocation.ResponseProjection is not null) ||
             plan.InvocationAdmissions.Any(static admission => admission.ResponseProjection is not null)))
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.RebindRequiredSchema,
                new WorkflowCapabilityAdmissionRebindRequiredException());
        }
        if (string.Equals(plan.SchemaVersion, PreviousSchemaVersion, StringComparison.Ordinal))
        {
            // V4 predates code-execution admission proofs. Preserve existing plans while runtime
            // still resolves and revalidates the canonical route for every execution.
            expected = expected
                .Where(static invocation => invocation.Selector.SelectorCase !=
                    ExternalWorkflowCapabilitySelector.SelectorOneofCase.CodeExecution)
                .ToArray();
        }
        try
        {
            ValidateExternalInvocations(expected);
        }
        catch (Exception exception) when (IsStructuralValidationException(exception))
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.InvocationMismatch,
                exception);
        }

        var actual = plan.InvocationAdmissions.ToArray();
        try
        {
            ValidateInvocationAdmissions(actual, executionMode);
        }
        catch (Exception exception) when (IsStructuralValidationException(exception))
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.AdmissionProofInvalid,
                exception);
        }

        (string WorkflowId, string RevisionId)? bindingIdentity;
        try
        {
            bindingIdentity = ResolveExplicitRequestBindingIdentity(actual, workflowId, revisionId);
        }
        catch (Exception exception) when (IsStructuralValidationException(exception))
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.AdmissionProofInvalid,
                exception);
        }

        var expectedDefinitionDigest = bindingIdentity is null
            ? ComputeDefinitionDigest(workflowYaml, inlineWorkflowYamls)
            : ComputeDefinitionDigest(
                workflowYaml,
                inlineWorkflowYamls,
                bindingIdentity.Value.WorkflowId,
                bindingIdentity.Value.RevisionId);
        if (!FixedTimeEquals(plan.DefinitionDigest, expectedDefinitionDigest))
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.DefinitionDigestMismatch,
                new InvalidOperationException(
                    "Workflow capability admission definition digest does not match the bound definition."));
        }
        if (!IsSortedByCallSite(actual))
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.InvocationOrderingInvalid,
                new InvalidOperationException(
                    "Workflow capability invocation admissions are not canonically ordered."));
        }
        if (!IsSortedBySourceStamp(plan.SourceStamps))
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.InvocationOrderingInvalid,
                new InvalidOperationException(
                    "Workflow capability admission source stamps are not canonically ordered."));
        }
        if (expected.Length != actual.Length)
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.InvocationMismatch,
                new InvalidOperationException(
                    "Workflow capability invocation admissions do not match the bound definition."));
        }
        for (var index = 0; index < expected.Length; index++)
        {
            bool selectorMatches;
            try
            {
                selectorMatches = SelectorMatchesCapability(expected[index].Selector, actual[index].Capability);
            }
            catch (Exception exception) when (IsStructuralValidationException(exception))
            {
                return Failed(
                    WorkflowCapabilityAdmissionCompatibilityFailure.InvocationMismatch,
                    exception);
            }

            if (!string.Equals(expected[index].CallSiteId, actual[index].CallSiteId, StringComparison.Ordinal) ||
                !selectorMatches ||
                !WorkflowToolResponseProjectionContract.AreEquivalent(
                    expected[index].ResponseProjection,
                    actual[index].ResponseProjection))
            {
                return Failed(
                    WorkflowCapabilityAdmissionCompatibilityFailure.InvocationMismatch,
                    new InvalidOperationException(
                        "Workflow capability invocation admissions do not match the bound definition."));
            }
        }

        var expectedCapabilityArray = actual
            .Select(static admission => admission.Capability)
            .ToArray();

        var requiresDurableAuthorizationCatalog = RequiresDurableAuthorizationCatalog(
            executionMode,
            expectedCapabilityArray);
        if (requiresDurableAuthorizationCatalog &&
            !HasDurableAuthorizationCatalogSource(plan.SourceStamps))
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.RequiredSourceMissing,
                new InvalidOperationException(
                    "Workflow capability admission durable authorization catalog source is required."));
        }
        if (!HasRequiredSourceEvidence(executionMode, expectedCapabilityArray, plan.SourceStamps))
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.RequiredSourceMissing,
                new InvalidOperationException(
                    "Workflow capability admission required source evidence is invalid."));
        }
        if (requiresDurableAuthorizationCatalog)
        {
            if (!IsCanonicalDurableAuthorizationOwner(plan.DurableAuthorizationOwner))
            {
                return Failed(
                    WorkflowCapabilityAdmissionCompatibilityFailure.DurableOwnerInvalid,
                    new InvalidOperationException(
                        "Workflow capability admission durable authorization owner is invalid."));
            }
        }
        else if (plan.DurableAuthorizationOwner is not null)
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.DurableOwnerInvalid,
                new InvalidOperationException(
                    "Workflow capability admission durable authorization owner is not applicable."));
        }

        if (!FixedTimeEquals(plan.AdmissionDigest, ComputeAdmissionDigest(plan)))
        {
            return Failed(
                WorkflowCapabilityAdmissionCompatibilityFailure.AdmissionDigestMismatch,
                new InvalidOperationException("Workflow capability admission digest is invalid."));
        }

        return new CompatibilityEvaluation(
            new WorkflowCapabilityAdmissionCompatibilityResult(
                WorkflowCapabilityAdmissionCompatibilityFailure.None),
            null);
    }

    public static IReadOnlyList<ExternalWorkflowCapabilityRef> DistinctCapabilities(
        WorkflowCapabilityAdmissionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.InvocationAdmissions
            .Select(static admission => admission.Capability)
            .Where(static capability => capability is not null)
            .GroupBy(CapabilityKey, StringComparer.Ordinal)
            .Select(static group => group.First().Clone())
            .OrderBy(CapabilityKey, StringComparer.Ordinal)
            .ToArray();
    }

    public static string SelectorKey(ExternalWorkflowCapabilitySelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return selector.SelectorCase switch
        {
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector => string.Join(
                "\n",
                "host",
                selector.HostConnector.ConnectorCapabilityRef,
                selector.HostConnector.OperationId,
                selector.HostConnector.ContractDigest),
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation => string.Join(
                "\n",
                "nyxid",
                selector.NyxIdOperation.UserServiceId,
                selector.NyxIdOperation.EndpointId),
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest => string.Join(
                "\n",
                "nyxid-request",
                ComputeNyxIdRequestContractDigest(selector.NyxIdRequest)),
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.CodeExecution =>
                "code-execution\ncanonical-platform-route",
            _ => "none",
        };
    }

    public static string ComputeNyxIdRequestContractDigest(NyxIdRequestSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (!NyxIdRequestSelectorContract.TryNormalize(selector, out var normalized, out var error))
            throw new InvalidOperationException($"Workflow NyxID {error}.");

        var components = new List<string?>
        {
            normalized.Risk == NyxIdOperationRisk.Unspecified
                ? "nyxid-explicit-request-contract.v1"
                : "nyxid-explicit-request-contract.v2",
            normalized.UserServiceId,
            ((int)normalized.Method).ToString(System.Globalization.CultureInfo.InvariantCulture),
            normalized.PathTemplate,
            string.Join("\n", NyxIdRequestSelectorContract.PathParameters(normalized).Order(StringComparer.Ordinal)),
            string.Join("\n", normalized.QueryParameters),
            string.Join("\n", normalized.HeaderParameters
                .Select(static value => value.ToLowerInvariant())
                .Order(StringComparer.Ordinal)),
            ((int)normalized.BodyMode).ToString(System.Globalization.CultureInfo.InvariantCulture),
            normalized.BodyRequired.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)normalized.ResponseMode).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (normalized.Risk != NyxIdOperationRisk.Unspecified)
        {
            components.Add(((int)normalized.Risk)
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return ComputeLengthPrefixedDigest(components);
    }

    public static string ComputeNyxIdExplicitRequestProofDigest(
        string requestContractDigest,
        string serviceSlugSnapshot) =>
        ComputeLengthPrefixedDigest([
            "nyxid-explicit-request-proof.v1",
            requestContractDigest,
            serviceSlugSnapshot,
        ]);

    public static string ComputeCodeExecutionCapabilityDigest(
        string userServiceId,
        string serviceSlugSnapshot,
        string catalogServiceId) =>
        ComputeLengthPrefixedDigest([
            "code-execution-capability.v1",
            userServiceId,
            serviceSlugSnapshot,
            catalogServiceId,
            "POST",
            "/execute",
        ]);

    public static string ComputeNyxIdExplicitRequestGrantDigest(NyxIdExplicitRequestGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return ComputeLengthPrefixedDigest([
            "nyxid-explicit-request-grant.v2",
            grant.WorkflowId,
            grant.RevisionId,
            grant.CallSiteId,
            grant.RequestContractDigest,
            ((int)grant.GrantorAuthority).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)grant.GrantorOwnerKind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            grant.GrantorOwnerSubject,
            ((int)grant.Risk).ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join("\n", grant.AllowedExecutionModes
                .Select(static mode => (int)mode)
                .Order()
                .Select(static mode => mode.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        ]);
    }

    public static bool SelectorMatchesCapability(
        ExternalWorkflowCapabilitySelector? selector,
        ExternalWorkflowCapabilityRef? capability)
    {
        if (selector is null || capability is null)
            return false;

        return (selector.SelectorCase, capability.CapabilityCase) switch
        {
            (ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector,
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector) =>
                string.Equals(
                    CapabilityKey(new ExternalWorkflowCapabilityRef
                    {
                        HostConnector = selector.HostConnector.Clone(),
                    }),
                    CapabilityKey(capability),
                    StringComparison.Ordinal),
            (ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation,
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService) =>
                string.Equals(
                    selector.NyxIdOperation.UserServiceId,
                    capability.NyxIdUserService.UserServiceId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    selector.NyxIdOperation.EndpointId,
                    capability.NyxIdUserService.EndpointId,
                    StringComparison.Ordinal),
            (ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest,
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest) =>
                capability.NyxIdUserRequest.Request is not null &&
                string.Equals(
                    ComputeNyxIdRequestContractDigest(selector.NyxIdRequest),
                    ComputeNyxIdRequestContractDigest(capability.NyxIdUserRequest.Request),
                    StringComparison.Ordinal),
            (ExternalWorkflowCapabilitySelector.SelectorOneofCase.CodeExecution,
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.CodeExecution) => true,
            _ => false,
        };
    }

    public static string CapabilityKey(ExternalWorkflowCapabilityRef capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return capability.CapabilityCase switch
        {
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector => string.Join(
                "\n",
                "host",
                capability.HostConnector.ConnectorCapabilityRef,
                capability.HostConnector.OperationId,
                capability.HostConnector.ContractDigest),
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService => string.Join(
                "\n",
                "nyxid",
                capability.NyxIdUserService.UserServiceId,
                capability.NyxIdUserService.ServiceSlugSnapshot,
                capability.NyxIdUserService.EndpointId,
                capability.NyxIdUserService.HttpMethod,
                capability.NyxIdUserService.PathTemplate,
                capability.NyxIdUserService.ContractDigest),
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest => string.Join(
                "\n",
                "nyxid-request",
                ComputeNyxIdRequestContractDigest(capability.NyxIdUserRequest.Request),
                capability.NyxIdUserRequest.ServiceSlugSnapshot,
                capability.NyxIdUserRequest.ContractDigest,
                capability.NyxIdUserRequest.ExplicitRequestGrantDigest,
                NyxIdExecutionPolicyKey(capability.NyxIdUserRequest.ExecutionPolicy)),
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.CodeExecution => string.Join(
                "\n",
                "code-execution",
                capability.CodeExecution.UserServiceId,
                capability.CodeExecution.ServiceSlugSnapshot,
                capability.CodeExecution.CatalogServiceId,
                capability.CodeExecution.ContractDigest,
                string.Join("\n", capability.CodeExecution.AllowedExecutionModes
                    .Select(static mode => (int)mode)
                    .Order())),
            _ => "none",
        };
    }

    private static void ValidateExternalInvocations(
        IReadOnlyList<ExternalToolInvocationSpec> invocations)
    {
        foreach (var invocation in invocations)
        {
            ValidateCallSiteId(invocation.CallSiteId);
            if (string.IsNullOrWhiteSpace(invocation.ToolName) ||
                !string.Equals(invocation.ToolName, invocation.ToolName.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Workflow external invocation tool name is invalid.");
            }
            ValidateSelector(invocation.Selector);
            if (invocation.ResponseProjection is not null)
            {
                if (!string.Equals(invocation.ToolName, "nyxid_proxy", StringComparison.OrdinalIgnoreCase) ||
                    invocation.Selector.SelectorCase is not (
                        ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation or
                        ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest))
                {
                    throw new InvalidOperationException(
                        "Workflow tool response projection is only valid for a NyxID proxy invocation.");
                }
                WorkflowToolResponseProjectionContract.ValidateOrThrow(invocation.ResponseProjection);
            }
        }
        EnsureUniqueCallSites(invocations.Select(static invocation => invocation.CallSiteId));
    }

    private static void ValidateInvocationAdmissions(
        IReadOnlyList<WorkflowCapabilityInvocationAdmission> admissions,
        ExternalCapabilityExecutionMode executionMode)
    {
        foreach (var admission in admissions)
        {
            ValidateInvocationAdmissionIntrinsicIntegrity(admission);
            ValidateInvocationAdmissionExecutionMode(admission, executionMode);
        }
        EnsureUniqueCallSites(admissions.Select(static admission => admission.CallSiteId));
    }

    /// <summary>
    /// Validates one admission's mode-independent internal consistency. This does not establish
    /// that the admission is allowed for any particular plan execution mode.
    /// </summary>
    public static void ValidateInvocationAdmissionIntrinsicIntegrity(
        WorkflowCapabilityInvocationAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ValidateCallSiteId(admission.CallSiteId);
        if (admission.Capability is null ||
            admission.Capability.CapabilityCase ==
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.None)
        {
            throw new InvalidOperationException(
                "Workflow capability invocation admission proof is required.");
        }

        if (admission.ResponseProjection is not null)
        {
            if (admission.Capability.CapabilityCase is not (
                    ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService or
                    ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest))
            {
                throw new InvalidOperationException(
                    "Workflow tool response projection is not valid for this capability proof.");
            }
            WorkflowToolResponseProjectionContract.ValidateOrThrow(admission.ResponseProjection);
        }

        switch (admission.Capability.CapabilityCase)
        {
            case ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService:
                ValidateNyxIdPublishedOperationAdmissionIntrinsicIntegrity(admission);
                break;
            case ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest:
                ValidateNyxIdExplicitRequestAdmissionIntrinsicIntegrity(admission);
                break;
            case ExternalWorkflowCapabilityRef.CapabilityOneofCase.CodeExecution:
                ValidateCodeExecutionAdmissionIntrinsicIntegrity(admission);
                break;
            default:
                if (admission.NyxIdExplicitRequestGrant is not null)
                {
                    throw new InvalidOperationException(
                        "Workflow NyxID explicit request grant is not applicable to this capability.");
                }
                break;
        }
    }

    private static void ValidateNyxIdPublishedOperationAdmissionIntrinsicIntegrity(
        WorkflowCapabilityInvocationAdmission admission)
    {
        if (admission.NyxIdExplicitRequestGrant is not null)
        {
            throw new InvalidOperationException(
                "Workflow NyxID explicit request grant is not applicable to a published operation proof.");
        }

        var proof = admission.Capability.NyxIdUserService;
        if (string.IsNullOrWhiteSpace(proof.EndpointId) ||
            !string.Equals(proof.EndpointId, proof.EndpointId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Workflow NyxID published endpoint identity is invalid.");
        }
        ValidateNyxIdExecutionPolicy(proof.ExecutionPolicy);
    }

    private static void ValidateCodeExecutionAdmissionIntrinsicIntegrity(
        WorkflowCapabilityInvocationAdmission admission)
    {
        if (admission.NyxIdExplicitRequestGrant is not null)
        {
            throw new InvalidOperationException(
                "Workflow NyxID explicit request grant is not applicable to code execution.");
        }

        var proof = admission.Capability.CodeExecution;
        if (!IsCanonicalIdentity(proof.UserServiceId) ||
            !IsSupportedCodeExecutionServiceSlug(proof.ServiceSlugSnapshot) ||
            !IsCanonicalIdentity(proof.CatalogServiceId) ||
            !FixedTimeEquals(
                proof.ContractDigest,
                ComputeCodeExecutionCapabilityDigest(
                    proof.UserServiceId,
                    proof.ServiceSlugSnapshot,
                    proof.CatalogServiceId)) ||
            proof.AllowedExecutionModes.Count != 2 ||
            !proof.AllowedExecutionModes.Contains(ExternalCapabilityExecutionMode.Interactive) ||
            !proof.AllowedExecutionModes.Contains(ExternalCapabilityExecutionMode.Durable) ||
            proof.AllowedExecutionModes.Distinct().Count() != proof.AllowedExecutionModes.Count)
        {
            throw new InvalidOperationException("Workflow code execution capability proof is invalid.");
        }
    }

    private static void ValidateNyxIdExplicitRequestAdmissionIntrinsicIntegrity(
        WorkflowCapabilityInvocationAdmission admission)
    {
        var proof = admission.Capability.NyxIdUserRequest;
        var grant = admission.NyxIdExplicitRequestGrant;
        if (grant is null)
            throw new InvalidOperationException("Workflow NyxID explicit request grant is required.");
        if (proof.Request is null)
            throw new InvalidOperationException("Workflow NyxID explicit request proof request is required.");

        ValidateCanonicalNyxIdRequest(proof.Request);
        if (string.IsNullOrWhiteSpace(proof.ServiceSlugSnapshot) ||
            !string.Equals(proof.ServiceSlugSnapshot, proof.ServiceSlugSnapshot.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Workflow NyxID explicit request service slug is invalid.");
        }

        var requestContractDigest = ComputeNyxIdRequestContractDigest(proof.Request);
        if (!FixedTimeEquals(
                proof.ContractDigest,
                ComputeNyxIdExplicitRequestProofDigest(requestContractDigest, proof.ServiceSlugSnapshot)))
        {
            throw new InvalidOperationException("Workflow NyxID explicit request proof digest is invalid.");
        }
        if (!string.Equals(grant.CallSiteId, admission.CallSiteId, StringComparison.Ordinal) ||
            !FixedTimeEquals(grant.RequestContractDigest, requestContractDigest))
        {
            throw new InvalidOperationException("Workflow NyxID explicit request grant scope is invalid.");
        }
        ValidateBindingIdentity(grant.WorkflowId, nameof(grant.WorkflowId));
        ValidateBindingIdentity(grant.RevisionId, nameof(grant.RevisionId));
        if (grant.GrantorAuthority != NyxIdExplicitRequestGrantorAuthority.AevatarWorkflowBinder ||
            grant.GrantorOwnerKind == ExternalCapabilityAuthorizationOwnerKind.Unspecified ||
            !System.Enum.IsDefined(grant.GrantorOwnerKind) ||
            string.IsNullOrWhiteSpace(grant.GrantorOwnerSubject) ||
            !string.Equals(grant.GrantorOwnerSubject, grant.GrantorOwnerSubject.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Workflow NyxID explicit request grantor is invalid.");
        }
        if (grant.Risk is not (NyxIdOperationRisk.ReadOnly or NyxIdOperationRisk.Write or
                NyxIdOperationRisk.Destructive) ||
            grant.AllowedExecutionModes.Count == 0 ||
            !grant.AllowedExecutionModes.Contains(ExternalCapabilityExecutionMode.Interactive) ||
            grant.AllowedExecutionModes.Any(static mode =>
                mode is not (ExternalCapabilityExecutionMode.Interactive or
                    ExternalCapabilityExecutionMode.Durable)) ||
            grant.AllowedExecutionModes.Distinct().Count() != grant.AllowedExecutionModes.Count)
        {
            throw new InvalidOperationException("Workflow NyxID explicit request grant policy is invalid.");
        }

        ValidateExplicitRequestRisk(proof.Request, grant.Risk);
        if (grant.AllowedExecutionModes.Contains(ExternalCapabilityExecutionMode.Durable) &&
            !NyxIdRequestSelectorContract.SupportsDurableExecution(
                proof.Request.Method,
                grant.Risk))
        {
            throw new InvalidOperationException(
                "Workflow NyxID explicit request durable admission is not allowed for the request risk.");
        }

        ValidateNyxIdExecutionPolicy(proof.ExecutionPolicy);
        if (proof.ExecutionPolicy.Risk != grant.Risk ||
            !proof.ExecutionPolicy.AllowedExecutionModes.Order()
                .SequenceEqual(grant.AllowedExecutionModes.Order()))
        {
            throw new InvalidOperationException(
                "Workflow NyxID explicit request proof policy does not match its grant.");
        }
        if (!FixedTimeEquals(
                proof.ExplicitRequestGrantDigest,
                ComputeNyxIdExplicitRequestGrantDigest(grant)))
        {
            throw new InvalidOperationException("Workflow NyxID explicit request grant digest is invalid.");
        }
    }

    private static void ValidateInvocationAdmissionExecutionMode(
        WorkflowCapabilityInvocationAdmission admission,
        ExternalCapabilityExecutionMode executionMode)
    {
        var policy = admission.Capability.CapabilityCase switch
        {
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService =>
                admission.Capability.NyxIdUserService.ExecutionPolicy,
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest =>
                admission.Capability.NyxIdUserRequest.ExecutionPolicy,
            _ => null,
        };
        if (policy is not null && !policy.AllowedExecutionModes.Contains(executionMode))
        {
            throw new InvalidOperationException(
                "Workflow capability admission execution mode is not allowed by the NyxID operation execution policy.");
        }
        if (admission.Capability.CapabilityCase ==
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.CodeExecution &&
            !admission.Capability.CodeExecution.AllowedExecutionModes.Contains(executionMode))
        {
            throw new InvalidOperationException(
                "Workflow capability admission execution mode is not allowed by the code execution capability.");
        }
    }

    private static void ValidateExplicitRequestRisk(
        NyxIdRequestSelector request,
        NyxIdOperationRisk risk)
    {
        if (!NyxIdRequestSelectorContract.IsRiskAttestationSatisfied(
                request.Method,
                request.Risk,
                risk))
        {
            throw new InvalidOperationException("Workflow NyxID explicit request grant risk is below the method floor.");
        }
    }

    public static bool IsValidNyxIdExecutionPolicy(NyxIdOperationExecutionPolicy? policy)
    {
        if (policy is null ||
            policy.Risk is not (NyxIdOperationRisk.ReadOnly or NyxIdOperationRisk.Write or NyxIdOperationRisk.Destructive) ||
            policy.Approval is not (NyxIdOperationApproval.None or NyxIdOperationApproval.Required) ||
            policy.EnforcementOwner != NyxIdOperationEnforcementOwner.Aevatar ||
            policy.AllowedExecutionModes.Count == 0 ||
            !policy.AllowedExecutionModes.Contains(ExternalCapabilityExecutionMode.Interactive) ||
            policy.AllowedExecutionModes.Any(static mode =>
                mode is not (ExternalCapabilityExecutionMode.Interactive or ExternalCapabilityExecutionMode.Durable)) ||
            policy.AllowedExecutionModes.Distinct().Count() != policy.AllowedExecutionModes.Count)
        {
            return false;
        }

        return policy.Risk switch
        {
            NyxIdOperationRisk.ReadOnly => policy.Approval == NyxIdOperationApproval.None,
            NyxIdOperationRisk.Write or NyxIdOperationRisk.Destructive =>
                policy.Approval == NyxIdOperationApproval.Required,
            _ => false,
        };
    }

    private static void ValidateNyxIdExecutionPolicy(NyxIdOperationExecutionPolicy? policy)
    {
        if (!IsValidNyxIdExecutionPolicy(policy))
            throw new InvalidOperationException("Workflow NyxID operation execution policy is invalid.");
    }

    private static void ValidateSelector(ExternalWorkflowCapabilitySelector? selector)
    {
        if (selector is null || selector.SelectorCase == ExternalWorkflowCapabilitySelector.SelectorOneofCase.None)
            throw new InvalidOperationException("Workflow external invocation selector is required.");

        var requiredValues = selector.SelectorCase switch
        {
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector => new[]
            {
                selector.HostConnector.ConnectorCapabilityRef,
                selector.HostConnector.OperationId,
            },
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation => new[]
            {
                selector.NyxIdOperation.UserServiceId,
                selector.NyxIdOperation.EndpointId,
            },
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.CodeExecution => [],
            _ => [],
        };
        if (requiredValues.Any(static value =>
                string.IsNullOrWhiteSpace(value) ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Workflow external invocation selector identity is invalid.");
        }

        if (selector.SelectorCase == ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector &&
            !string.Equals(
                selector.HostConnector.ContractDigest,
                selector.HostConnector.ContractDigest.Trim(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Workflow external invocation selector identity is invalid.");
        }

        if (selector.SelectorCase == ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest)
            ValidateCanonicalNyxIdRequest(selector.NyxIdRequest);
    }

    private static void ValidateCanonicalNyxIdRequest(NyxIdRequestSelector selector)
    {
        if (!NyxIdRequestSelectorContract.TryNormalize(selector, out var normalized, out var error))
            throw new InvalidOperationException($"Workflow NyxID {error}.");
        if (!selector.Equals(normalized))
            throw new InvalidOperationException("Workflow NyxID explicit request selector is not canonical.");
    }

    private static void ValidateCallSiteId(string? callSiteId)
    {
        if (string.IsNullOrWhiteSpace(callSiteId) ||
            !string.Equals(callSiteId, callSiteId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Workflow external invocation call site id is invalid.");
        }
    }

    public static bool RequiresRebind(string? schemaVersion) =>
        string.Equals(schemaVersion, LegacySchemaVersion, StringComparison.Ordinal) ||
        string.Equals(schemaVersion, OpenApiSchemaVersion, StringComparison.Ordinal) ||
        string.Equals(schemaVersion, CodeRouteSchemaVersion, StringComparison.Ordinal);

    public static bool IsSupportedSchemaVersion(string? schemaVersion) =>
        string.Equals(schemaVersion, SchemaVersion, StringComparison.Ordinal) ||
        string.Equals(schemaVersion, PreviousSchemaVersion, StringComparison.Ordinal);

    private static void EnsureUniqueCallSites(IEnumerable<string> callSiteIds)
    {
        if (callSiteIds.GroupBy(static id => id, StringComparer.Ordinal).Any(static group => group.Count() != 1))
            throw new InvalidOperationException("Workflow external invocation call site ids must be unique.");
    }

    private static bool IsSortedByCallSite(
        IReadOnlyList<WorkflowCapabilityInvocationAdmission> admissions) =>
        admissions.Select(static admission => admission.CallSiteId)
            .SequenceEqual(
                admissions.Select(static admission => admission.CallSiteId)
                    .OrderBy(static id => id, StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static bool IsSortedBySourceStamp(
        IEnumerable<ExternalCapabilitySourceStamp> sourceStamps)
    {
        var keys = sourceStamps.Select(SourceKey).ToArray();
        return keys.SequenceEqual(keys.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool IsStructuralValidationException(Exception exception) =>
        exception is InvalidOperationException or ArgumentException;

    private static CompatibilityEvaluation Failed(
        WorkflowCapabilityAdmissionCompatibilityFailure failure,
        Exception exception) =>
        new(new WorkflowCapabilityAdmissionCompatibilityResult(failure), exception);

    private sealed record CompatibilityEvaluation(
        WorkflowCapabilityAdmissionCompatibilityResult Result,
        Exception? Exception);

    public static bool RequiresDurableAuthorizationCatalog(
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<ExternalWorkflowCapabilityRef> capabilities) =>
        executionMode == ExternalCapabilityExecutionMode.Durable &&
        capabilities.Any(static capability =>
            capability.CapabilityCase is
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService or
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest or
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.CodeExecution);

    public static bool HasDurableAuthorizationCatalogSource(
        IEnumerable<ExternalCapabilitySourceStamp> sourceStamps,
        string? expectedSourceId = null)
    {
        var catalogs = sourceStamps
            .Where(static source =>
                source.SourceKind == ExternalCapabilitySourceKind.DurableAuthorizationCatalog)
            .ToArray();
        return catalogs.Length == 1 &&
               IsUsableSourceStamp(catalogs[0], requirePositiveVersion: true) &&
               (expectedSourceId is null ||
                string.Equals(catalogs[0].SourceId, expectedSourceId, StringComparison.Ordinal));
    }

    public static bool HasRequiredSourceEvidence(
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<ExternalWorkflowCapabilityRef> capabilities,
        IEnumerable<ExternalCapabilitySourceStamp> sourceStamps)
    {
        var capabilityArray = capabilities.ToArray();
        var sources = sourceStamps.ToArray();
        foreach (var capability in capabilityArray)
        {
            switch (capability.CapabilityCase)
            {
                case ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector:
                    if (!HasSource(sources, ExternalCapabilitySourceKind.ConnectorCatalog))
                        return false;
                    break;
                case ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService:
                    if (!HasSource(sources, ExternalCapabilitySourceKind.NyxIdMcpConfig))
                        return false;
                    break;
                case ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest:
                    if (!HasSource(sources, ExternalCapabilitySourceKind.NyxIdUserServices))
                        return false;
                    break;
                case ExternalWorkflowCapabilityRef.CapabilityOneofCase.CodeExecution:
                    if (!HasSource(sources, ExternalCapabilitySourceKind.NyxIdUserServices))
                        return false;
                    break;
                default:
                    return false;
            }
        }

        return !RequiresDurableAuthorizationCatalog(executionMode, capabilityArray) ||
               HasDurableAuthorizationCatalogSource(sources);
    }

    public static bool IsCanonicalDurableAuthorizationOwner(
        ExternalCapabilityAuthorizationOwner? owner) =>
        owner is not null &&
        string.Equals(owner.Authority, NyxIdAuthority, StringComparison.Ordinal) &&
        owner.OwnerKind == ExternalCapabilityAuthorizationOwnerKind.Personal &&
        !string.IsNullOrWhiteSpace(owner.OwnerSubject) &&
        string.Equals(owner.OwnerSubject, owner.OwnerSubject.Trim(), StringComparison.Ordinal);

    private static bool HasSource(
        IEnumerable<ExternalCapabilitySourceStamp> sources,
        ExternalCapabilitySourceKind sourceKind,
        string? expectedSourceId = null) =>
        sources.Any(source =>
            source.SourceKind == sourceKind &&
            IsUsableSourceStamp(source, requirePositiveVersion: false) &&
            (expectedSourceId is null ||
             string.Equals(source.SourceId, expectedSourceId, StringComparison.Ordinal)));

    private static bool IsUsableSourceStamp(
        ExternalCapabilitySourceStamp source,
        bool requirePositiveVersion)
    {
        if (string.IsNullOrWhiteSpace(source.SourceId) ||
            !string.Equals(source.SourceId, source.SourceId.Trim(), StringComparison.Ordinal) ||
            source.SourceVersion < 0 ||
            requirePositiveVersion && source.SourceVersion == 0 ||
            source.ObservedAt is null ||
            source.FreshUntil is null ||
            string.IsNullOrWhiteSpace(source.ContentDigest) ||
            !string.Equals(source.ContentDigest, source.ContentDigest.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return source.ObservedAt.ToDateTimeOffset() < source.FreshUntil.ToDateTimeOffset();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsCanonicalIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsSupportedCodeExecutionServiceSlug(string? value) =>
        string.Equals(value, "chrono-sandbox", StringComparison.Ordinal) ||
        string.Equals(value, "chrono-sandbox-aevatar", StringComparison.Ordinal);

    private static string SourceKey(ExternalCapabilitySourceStamp source) =>
        string.Join(
            "\n",
            ((int)source.SourceKind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            source.SourceId,
            source.SourceVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            source.ContentDigest);

    private static string NyxIdExecutionPolicyKey(NyxIdOperationExecutionPolicy? policy) =>
        policy is null
            ? "none"
            : string.Join(
                "\n",
                ((int)policy.Risk).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ((int)policy.Approval).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ((int)policy.EnforcementOwner).ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Join("\n", policy.AllowedExecutionModes
                    .Select(static mode => (int)mode)
                    .Order()
                    .Select(static mode => mode.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    private static (string WorkflowId, string RevisionId)? ResolveExplicitRequestBindingIdentity(
        IReadOnlyList<WorkflowCapabilityInvocationAdmission> admissions,
        string? expectedWorkflowId,
        string? expectedRevisionId)
    {
        var grants = admissions
            .Where(static admission => admission.NyxIdExplicitRequestGrant is not null)
            .Select(static admission => admission.NyxIdExplicitRequestGrant)
            .ToArray();
        if (grants.Length == 0)
            return null;

        var workflowId = grants[0].WorkflowId;
        var revisionId = grants[0].RevisionId;
        if (grants.Any(grant =>
                !string.Equals(grant.WorkflowId, workflowId, StringComparison.Ordinal) ||
                !string.Equals(grant.RevisionId, revisionId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Workflow NyxID explicit request grants do not share one workflow revision identity.");
        }

        if (string.IsNullOrWhiteSpace(expectedWorkflowId) ||
            string.IsNullOrWhiteSpace(expectedRevisionId))
        {
            throw new InvalidOperationException(
                "Workflow NyxID explicit request binding identity is required.");
        }
        ValidateBindingIdentity(expectedWorkflowId, nameof(expectedWorkflowId));
        ValidateBindingIdentity(expectedRevisionId, nameof(expectedRevisionId));
        if (!string.Equals(workflowId, expectedWorkflowId, StringComparison.Ordinal) ||
            !string.Equals(revisionId, expectedRevisionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Workflow NyxID explicit request grant does not match the bound workflow revision.");
        }

        return (workflowId, revisionId);
    }

    private static void ValidateBindingIdentity(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Workflow binding {parameterName} is invalid.");
        }
    }

    private static string ComputeLengthPrefixedDigest(IEnumerable<string?> components)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBuffer = stackalloc byte[sizeof(int)];
        foreach (var component in components)
        {
            if (component is null)
            {
                BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, -1);
                hash.AppendData(lengthBuffer);
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(component);
            BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, bytes.Length);
            hash.AppendData(lengthBuffer);
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool FixedTimeEquals(string? left, string? right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
        var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

public sealed class WorkflowCapabilityAdmissionRebindRequiredException : InvalidOperationException
{
    public WorkflowCapabilityAdmissionRebindRequiredException()
        : base("CAPABILITY_ADMISSION_REBIND_REQUIRED: Rebind the workflow to create a call-site capability admission plan.")
    {
    }

    public string Code => WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode;
}
