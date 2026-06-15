using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

/// <summary>
/// Pure helpers used by <see cref="RuntimeActorGrain"/> for kind / CLR-name
/// identity resolution. Extracted so the activation logic is exercisable
/// without an Orleans test cluster.
/// </summary>
internal static class RuntimeActorIdentityResolution
{
    /// <summary>
    /// Strips the assembly-qualifier from a CLR type token so the registry
    /// can match against <see cref="Type.FullName"/>. Tests + bootstrap
    /// helpers historically pass <see cref="Type.AssemblyQualifiedName"/>.
    /// </summary>
    /// <remarks>
    /// Bracket-aware: a comma inside <c>[...]</c> belongs to a generic
    /// parameter spec (e.g. <c>Foo`1[[T, ParamAsm, ...]], OuterAsm, ...</c>)
    /// and must not be treated as the outer assembly separator. Naive
    /// <c>IndexOf(',')</c> truncates at the inner comma and produces a name
    /// that won't match <c>Type.FullName</c>.
    /// </remarks>
    internal static bool TryNormalizeClrTypeName(string clrTypeName, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(clrTypeName))
        {
            normalized = string.Empty;
            return false;
        }

        var depth = 0;
        var separator = -1;
        for (var i = 0; i < clrTypeName.Length; i++)
        {
            var c = clrTypeName[i];
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                if (depth > 0)
                    depth--;
            }
            else if (c == ',' && depth == 0)
            {
                separator = i;
                break;
            }
        }

        normalized = separator < 0
            ? clrTypeName.Trim()
            : clrTypeName[..separator].Trim();
        return normalized.Length > 0;
    }

    /// <summary>
    /// Returns true when <paramref name="requestedKind"/> resolves (directly
    /// or via legacy alias) to the same canonical kind that the grain is
    /// currently bound to. Used as the idempotency check for repeat calls
    /// to <c>InitializeAgentByKindAsync</c> — passing an alias must not
    /// surface as a different identity from the canonical form.
    /// </summary>
    internal static bool ResolvesToSameImplementation(
        IAgentKindRegistry? registry,
        string? activeKind,
        string requestedKind)
    {
        if (string.IsNullOrWhiteSpace(activeKind))
            return false;

        if (string.Equals(activeKind, requestedKind, StringComparison.Ordinal))
            return true;

        if (registry == null)
            return false;

        try
        {
            return string.Equals(
                registry.Resolve(requestedKind).Metadata.Kind,
                activeKind,
                StringComparison.Ordinal);
        }
        catch (UnknownAgentKindException)
        {
            return false;
        }
    }
}
