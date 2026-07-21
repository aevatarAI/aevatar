using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;

namespace Aevatar.Workflow.Abstractions;

public static class WorkflowCapabilityAdmissionPlanIntegrity
{
    public const string SchemaVersion = "external-capability-admission.v1";

    public static WorkflowCapabilityAdmissionPlan Create(
        string workflowYaml,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        ExternalCapabilityExecutionMode executionMode,
        IEnumerable<ExternalWorkflowCapabilityRef> capabilities,
        IEnumerable<ExternalCapabilitySourceStamp> sourceStamps)
    {
        var plan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = SchemaVersion,
            DefinitionDigest = ComputeDefinitionDigest(workflowYaml, inlineWorkflowYamls),
            ExecutionMode = executionMode,
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

        var expected = expectedCapabilities
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
