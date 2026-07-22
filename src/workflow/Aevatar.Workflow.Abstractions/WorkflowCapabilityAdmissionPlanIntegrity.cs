using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;

namespace Aevatar.Workflow.Abstractions;

public static class WorkflowCapabilityAdmissionPlanIntegrity
{
    public const string SchemaVersion = "external-capability-admission.v2";
    public const string NyxIdAuthority = "nyxid";

    public static WorkflowCapabilityAdmissionPlan Create(
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<ExternalWorkflowCapabilityRef> capabilities,
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
        plan.ExternalCapabilities.Add(
            capabilities
                .Select(static capability => capability.Clone())
                .OrderBy(CapabilityKey, StringComparer.Ordinal));
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
        IEnumerable<ExternalWorkflowCapabilityRef> expectedCapabilities)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Workflow capability admission schema version is invalid.");

        var expectedDefinitionDigest = ComputeDefinitionDigest(workflowYaml, inlineWorkflowYamls);
        if (!FixedTimeEquals(plan.DefinitionDigest, expectedDefinitionDigest))
            throw new InvalidOperationException("Workflow capability admission definition digest does not match the bound definition.");

        if (executionMode == ExternalCapabilityExecutionMode.Unspecified ||
            plan.ExecutionMode != executionMode)
        {
            throw new InvalidOperationException("Workflow capability admission execution mode does not match the binding request.");
        }

        var expectedCapabilityArray = expectedCapabilities.ToArray();
        var expected = expectedCapabilityArray
            .Select(CapabilityKey)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        var actual = plan.ExternalCapabilities
            .Select(CapabilityKey)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new InvalidOperationException("Workflow capability admission capabilities do not match the bound definition.");

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
                capability.NyxIdUserService.ContractDigest),
            _ => "none",
        };
    }

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
