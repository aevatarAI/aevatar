using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.AI.Abstractions.ToolProviders;

public interface IAgentToolAuthorizationIdentity
{
    string AuthorizationIdentity { get; }
}

public sealed record AgentToolCapability(string Name, string ContractDigest)
{
    public static AgentToolCapability Capture(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return new AgentToolCapability(tool.Name.Trim(), ComputeContractDigest(tool));
    }

    public bool Matches(IAgentTool tool) =>
        string.Equals(Name, tool.Name?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ContractDigest, ComputeContractDigest(tool), StringComparison.Ordinal);

    public static IReadOnlyList<IAgentTool> Resolve(
        IEnumerable<IAgentTool>? candidates,
        IEnumerable<AgentToolCapability>? capabilities)
    {
        var exactCandidates = ExactAgentToolSet.Create(candidates);
        var capabilitiesByName = new Dictionary<string, AgentToolCapability>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var capability in capabilities ?? [])
        {
            if (string.IsNullOrWhiteSpace(capability.Name) ||
                string.IsNullOrWhiteSpace(capability.ContractDigest))
            {
                continue;
            }

            var name = capability.Name.Trim();
            if (conflicts.Contains(name))
                continue;
            if (!capabilitiesByName.TryGetValue(name, out var existing))
            {
                capabilitiesByName.Add(name, capability with { Name = name });
                continue;
            }
            if (string.Equals(existing.ContractDigest, capability.ContractDigest, StringComparison.Ordinal))
                continue;

            capabilitiesByName.Remove(name);
            conflicts.Add(name);
        }

        var resolved = new List<IAgentTool>();
        foreach (var (name, capability) in capabilitiesByName)
        {
            if (exactCandidates.ToolsByName.TryGetValue(name, out var tool) && capability.Matches(tool))
                resolved.Add(tool);
        }
        return resolved;
    }

    private static string ComputeContractDigest(IAgentTool tool)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, tool.GetType().Assembly.GetName().Name);
        Append(hash, tool.GetType().FullName);
        Append(hash, tool.GetType().Module.ModuleVersionId.ToString("D"));
        Append(hash, tool.Name?.Trim());
        Append(hash, tool.Description);
        Append(hash, tool.ParametersSchema);
        Append(hash, ((int)tool.ApprovalMode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, tool.IsReadOnly ? "1" : "0");
        Append(hash, tool.IsDestructive ? "1" : "0");
        Append(hash, tool.SideEffectKind);
        if (tool is IAgentToolCapabilityDescriptor descriptor)
        {
            foreach (var capability in descriptor.Capabilities.Order(StringComparer.Ordinal))
                Append(hash, capability);
        }
        if (tool is IAgentToolAuthorizationIdentity authorizationIdentity)
            Append(hash, authorizationIdentity.AuthorizationIdentity);

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
