using System.Security.Cryptography;
using Google.Protobuf;

namespace Aevatar.Foundation.Abstractions.Runtime;

public static class RuntimeFleetMembershipDigest
{
    public static string Compute(RuntimeFleetMembershipSnapshot membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        var canonical = membership.Clone();
        canonical.MembershipDigest = string.Empty;
        canonical.ObservedAt = null;
        canonical.ValidUntil = null;

        var members = canonical.ActiveMembers
            .OrderBy(static member => member.MemberId, StringComparer.Ordinal)
            .ThenBy(static member => member.Incarnation, StringComparer.Ordinal)
            .Select(static member =>
            {
                var clone = member.Clone();
                var capabilities = clone.Capabilities
                    .OrderBy(static capability => capability.Capability)
                    .ThenBy(static capability => capability.ContractId, StringComparer.Ordinal)
                    .ToArray();
                clone.Capabilities.Clear();
                clone.Capabilities.Add(capabilities);
                return clone;
            })
            .ToArray();
        canonical.ActiveMembers.Clear();
        canonical.ActiveMembers.Add(members);
        return Convert.ToHexStringLower(SHA256.HashData(canonical.ToByteArray()));
    }
}
