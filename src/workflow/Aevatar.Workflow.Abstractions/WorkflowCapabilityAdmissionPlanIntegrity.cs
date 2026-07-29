using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;

namespace Aevatar.Workflow.Abstractions;

public static class WorkflowCapabilityAdmissionPlanIntegrity
{
    public const string SchemaVersion = "external-capability-admission.v3";
    public const string LegacySchemaVersion = "external-capability-admission.v2";
    public const string RebindRequiredCode = "CAPABILITY_ADMISSION_REBIND_REQUIRED";
    public const string NyxIdAuthority = "nyxid";

    public static WorkflowCapabilityAdmissionPlan Create(
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<WorkflowCapabilityInvocationAdmission> invocationAdmissions,
        IEnumerable<ExternalCapabilitySourceStamp> sourceStamps,
        ExternalCapabilityAuthorizationOwner? durableAuthorizationOwner = null)
    {
        var plan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = SchemaVersion,
            DefinitionDigest = ComputeDefinitionDigest(workflowYaml, inlineWorkflowYamls),
            ExecutionMode = executionMode,
            DurableAuthorizationOwner = durableAuthorizationOwner?.Clone(),
        };
        var admissions = invocationAdmissions
            .Select(static admission => admission.Clone())
            .OrderBy(static admission => admission.CallSiteId, StringComparer.Ordinal)
            .ToArray();
        ValidateInvocationAdmissions(admissions, executionMode);
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

    public static string ComputeAdmissionDigest(WorkflowCapabilityAdmissionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var canonical = plan.Clone();
        canonical.AdmissionDigest = string.Empty;
        return Convert.ToHexStringLower(SHA256.HashData(canonical.ToByteArray()));
    }

    public static void ValidateOrThrow(
        WorkflowCapabilityAdmissionPlan plan,
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<ExternalToolInvocationSpec> expectedInvocations)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.Equals(plan.SchemaVersion, LegacySchemaVersion, StringComparison.Ordinal))
            throw new WorkflowCapabilityAdmissionRebindRequiredException();
        if (!string.Equals(plan.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Workflow capability admission schema version is invalid.");
        if (plan.ExternalCapabilities.Count != 0)
            throw new InvalidOperationException("Workflow capability admission v3 cannot contain legacy external capabilities.");

        var expectedDefinitionDigest = ComputeDefinitionDigest(workflowYaml, inlineWorkflowYamls);
        if (!FixedTimeEquals(plan.DefinitionDigest, expectedDefinitionDigest))
            throw new InvalidOperationException("Workflow capability admission definition digest does not match the bound definition.");

        if (executionMode == ExternalCapabilityExecutionMode.Unspecified ||
            plan.ExecutionMode != executionMode)
        {
            throw new InvalidOperationException("Workflow capability admission execution mode does not match the binding request.");
        }

        var expected = expectedInvocations
            .Select(static invocation => invocation.Clone())
            .OrderBy(static invocation => invocation.CallSiteId, StringComparer.Ordinal)
            .ToArray();
        ValidateExternalInvocations(expected);
        var actual = plan.InvocationAdmissions.ToArray();
        ValidateInvocationAdmissions(actual, executionMode);
        if (!IsSortedByCallSite(actual))
            throw new InvalidOperationException("Workflow capability invocation admissions are not canonically ordered.");
        if (expected.Length != actual.Length)
            throw new InvalidOperationException("Workflow capability invocation admissions do not match the bound definition.");
        for (var index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(expected[index].CallSiteId, actual[index].CallSiteId, StringComparison.Ordinal) ||
                !SelectorMatchesCapability(expected[index].Selector, actual[index].Capability))
            {
                throw new InvalidOperationException(
                    "Workflow capability invocation admissions do not match the bound definition.");
            }
        }

        var expectedCapabilityArray = actual
            .Select(static admission => admission.Capability)
            .ToArray();

        if (actual.Length > 0 && plan.SourceStamps.Count == 0)
            throw new InvalidOperationException("Workflow capability admission source evidence is required.");
        var requiresDurableAuthorizationCatalog = RequiresDurableAuthorizationCatalog(
            executionMode,
            expectedCapabilityArray);
        if (requiresDurableAuthorizationCatalog &&
            !HasDurableAuthorizationCatalogSource(plan.SourceStamps))
        {
            throw new InvalidOperationException(
                "Workflow capability admission durable authorization catalog source is required.");
        }
        if (!HasRequiredSourceEvidence(executionMode, expectedCapabilityArray, plan.SourceStamps))
            throw new InvalidOperationException("Workflow capability admission required source evidence is invalid.");
        if (requiresDurableAuthorizationCatalog)
        {
            if (!IsCanonicalDurableAuthorizationOwner(plan.DurableAuthorizationOwner))
            {
                throw new InvalidOperationException(
                    "Workflow capability admission durable authorization owner is invalid.");
            }
        }
        else if (plan.DurableAuthorizationOwner is not null)
        {
            throw new InvalidOperationException(
                "Workflow capability admission durable authorization owner is not applicable.");
        }

        if (!FixedTimeEquals(plan.AdmissionDigest, ComputeAdmissionDigest(plan)))
            throw new InvalidOperationException("Workflow capability admission digest is invalid.");
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
                selector.NyxIdOperation.OperationId),
            _ => "none",
        };
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
                    selector.NyxIdOperation.OperationId,
                    capability.NyxIdUserService.OperationId,
                    StringComparison.Ordinal),
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
                capability.NyxIdUserService.OperationId,
                capability.NyxIdUserService.HttpMethod,
                capability.NyxIdUserService.PathTemplate,
                capability.NyxIdUserService.ContractDigest,
                capability.NyxIdUserService.ServiceAuthority?.EndpointUrl,
                capability.NyxIdUserService.ServiceAuthority?.EndpointId,
                capability.NyxIdUserService.ServiceAuthority?.ProxySpecServiceId,
                capability.NyxIdUserService.ServiceAuthority?.ProxyRouteCase.ToString(),
                capability.NyxIdUserService.ServiceAuthority?.CatalogServiceId,
                capability.NyxIdUserService.ServiceAuthority?.ServiceSlug,
                capability.NyxIdUserService.ServiceAuthority?.HasNodeId == true
                    ? capability.NyxIdUserService.ServiceAuthority.NodeId
                    : string.Empty,
                capability.NyxIdUserService.ServiceAuthority?.CredentialSource.ToString()),
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
        }
        EnsureUniqueCallSites(invocations.Select(static invocation => invocation.CallSiteId));
    }

    private static void ValidateInvocationAdmissions(
        IReadOnlyList<WorkflowCapabilityInvocationAdmission> admissions,
        ExternalCapabilityExecutionMode executionMode)
    {
        foreach (var admission in admissions)
        {
            ValidateCallSiteId(admission.CallSiteId);
            if (admission.Capability is null ||
                admission.Capability.CapabilityCase ==
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.None)
            {
                throw new InvalidOperationException("Workflow capability invocation admission proof is required.");
            }

            if (admission.Capability.CapabilityCase ==
                ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService)
            {
                var nyxId = admission.Capability.NyxIdUserService;
                if (!IsCanonicalNyxIdServiceAuthority(
                        nyxId.UserServiceId,
                        nyxId.ServiceSlugSnapshot,
                        nyxId.ServiceAuthority))
                {
                    throw new InvalidOperationException(
                        "Workflow NyxID service authority is invalid.");
                }

                var policy = nyxId.ExecutionPolicy;
                ValidateNyxIdExecutionPolicy(policy);
                if (!policy.AllowedExecutionModes.Contains(executionMode))
                {
                    throw new InvalidOperationException(
                        "Workflow capability admission execution mode is not allowed by the NyxID operation execution policy.");
                }
            }
        }
        EnsureUniqueCallSites(admissions.Select(static admission => admission.CallSiteId));
    }

    public static bool IsCanonicalNyxIdServiceAuthority(
        string? userServiceId,
        string? serviceSlug,
        NyxIdUserServiceAuthoritySnapshot? authority)
    {
        if (!IsCanonicalValue(userServiceId) ||
            !IsCanonicalValue(serviceSlug) ||
            authority is null ||
            !IsCanonicalValue(authority.EndpointUrl) ||
            !Uri.TryCreate(authority.EndpointUrl, UriKind.Absolute, out _) ||
            !IsCanonicalValue(authority.EndpointId) ||
            !string.Equals(authority.ProxySpecServiceId, userServiceId, StringComparison.Ordinal) ||
            authority.CredentialSource is not (
                NyxIdUserServiceCredentialSource.Personal or
                NyxIdUserServiceCredentialSource.Organization) ||
            authority.HasNodeId && !IsCanonicalValue(authority.NodeId))
        {
            return false;
        }

        return authority.ProxyRouteCase switch
        {
            NyxIdUserServiceAuthoritySnapshot.ProxyRouteOneofCase.CatalogServiceId =>
                IsCanonicalValue(authority.CatalogServiceId),
            NyxIdUserServiceAuthoritySnapshot.ProxyRouteOneofCase.ServiceSlug =>
                string.Equals(authority.ServiceSlug, serviceSlug, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool IsCanonicalValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

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
                policy.Approval == NyxIdOperationApproval.Required &&
                !policy.AllowedExecutionModes.Contains(ExternalCapabilityExecutionMode.Durable),
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
                selector.NyxIdOperation.OperationId,
            },
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
    }

    private static void ValidateCallSiteId(string? callSiteId)
    {
        if (string.IsNullOrWhiteSpace(callSiteId) ||
            !string.Equals(callSiteId, callSiteId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Workflow external invocation call site id is invalid.");
        }
    }

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

    public static bool RequiresDurableAuthorizationCatalog(
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<ExternalWorkflowCapabilityRef> capabilities) =>
        executionMode == ExternalCapabilityExecutionMode.Durable &&
        capabilities.Any(static capability =>
            capability.CapabilityCase == ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService);

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
                    if (!HasSource(sources, ExternalCapabilitySourceKind.NyxIdUserServices) ||
                        !HasSource(
                            sources,
                            ExternalCapabilitySourceKind.NyxIdOpenApi,
                            capability.NyxIdUserService.UserServiceId))
                    {
                        return false;
                    }
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

    private static string SourceKey(ExternalCapabilitySourceStamp source) =>
        string.Join(
            "\n",
            ((int)source.SourceKind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            source.SourceId,
            source.SourceVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            source.ContentDigest);

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
