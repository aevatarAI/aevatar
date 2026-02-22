namespace Aevatar.Foundation.Core.TypeSystem;

internal static class AgentTypeNameMatcher
{
    public static bool MatchesExpectedType(string? candidateTypeName, Type expectedType)
    {
        if (string.IsNullOrWhiteSpace(candidateTypeName))
            return false;

        ArgumentNullException.ThrowIfNull(expectedType);

        var resolved = Type.GetType(candidateTypeName, throwOnError: false);
        if (resolved != null)
            return expectedType.IsAssignableFrom(resolved);

        var expectedTypeName = expectedType.FullName;
        if (string.IsNullOrWhiteSpace(expectedTypeName))
            return false;

        var normalizedTypeName = NormalizeTypeName(candidateTypeName);
        return string.Equals(candidateTypeName, expectedTypeName, StringComparison.Ordinal) ||
               string.Equals(normalizedTypeName, expectedTypeName, StringComparison.Ordinal);
    }

    private static string NormalizeTypeName(string typeName)
    {
        var separatorIndex = typeName.IndexOf(',');
        return separatorIndex < 0 ? typeName : typeName[..separatorIndex].Trim();
    }
}
