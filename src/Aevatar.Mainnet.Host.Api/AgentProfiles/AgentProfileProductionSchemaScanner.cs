using System.Reflection;
using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions;
using Google.Protobuf.Reflection;

namespace Aevatar.Mainnet.Host.Api.AgentProfiles;

public static partial class AgentProfileProductionSchemaScanner
{
    private static readonly HashSet<string> AtomicDeniedTokens = new(StringComparer.Ordinal)
    {
        "token", "secret", "credential", "password", "passphrase", "authorization",
        "bearer", "cookie", "prompt", "body", "script", "markdown",
    };

    private static readonly HashSet<string> CompoundDeniedTokens = new(StringComparer.Ordinal)
    {
        "api_key", "access_key", "private_key", "client_secret", "system_prompt",
        "prompt_body", "skill_body", "skill_content", "skill_payload", "raw_prompt",
        "raw_content", "raw_payload",
    };

    public static IReadOnlyList<string> FindForbiddenNames()
    {
        var forbiddenNames = new HashSet<string>(StringComparer.Ordinal);
        VisitMessage(
            AgentProfileSnapshot.Descriptor,
            new HashSet<string>(StringComparer.Ordinal),
            forbiddenNames);
        VisitType(
            typeof(NyxIdChatAgentProfileOptions),
            new HashSet<Type>(),
            forbiddenNames);
        VisitType(
            typeof(NyxIdChatAgentProfileValidationBaseline),
            new HashSet<Type>(),
            forbiddenNames);
        return forbiddenNames.Order(StringComparer.Ordinal).ToArray();
    }

    private static void VisitMessage(
        MessageDescriptor descriptor,
        HashSet<string> visitedDescriptors,
        HashSet<string> forbiddenNames)
    {
        if (!visitedDescriptors.Add(descriptor.FullName))
            return;

        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            InspectName($"{descriptor.FullName}.{field.Name}", field.Name, forbiddenNames);
            InspectName($"{descriptor.FullName}.{field.JsonName}", field.JsonName, forbiddenNames);
            if (field.FieldType == FieldType.Message &&
                string.Equals(field.MessageType.File.Package, AgentProfileSnapshot.Descriptor.File.Package, StringComparison.Ordinal))
            {
                VisitMessage(field.MessageType, visitedDescriptors, forbiddenNames);
            }
        }
    }

    private static void VisitType(
        Type type,
        HashSet<Type> visitedTypes,
        HashSet<string> forbiddenNames)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (IsTerminal(type) || !visitedTypes.Add(type))
            return;

        if (TryGetCollectionElementType(type, out var elementType))
        {
            VisitType(elementType, visitedTypes, forbiddenNames);
            return;
        }

        if (!string.Equals(type.Namespace, typeof(NyxIdChatAgentProfileOptions).Namespace, StringComparison.Ordinal))
            return;

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            InspectName($"{type.FullName}.{property.Name}", property.Name, forbiddenNames);
            VisitType(property.PropertyType, visitedTypes, forbiddenNames);
        }
    }

    private static bool IsTerminal(Type type) =>
        type.IsPrimitive ||
        type.IsEnum ||
        type == typeof(string) ||
        type == typeof(decimal) ||
        type == typeof(DateTime) ||
        type == typeof(DateTimeOffset) ||
        type == typeof(TimeSpan) ||
        typeof(Google.Protobuf.IMessage).IsAssignableFrom(type);

    private static bool TryGetCollectionElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        var enumerable = type
            .GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate =>
                candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is not null)
        {
            elementType = enumerable.GetGenericArguments()[0];
            return true;
        }

        elementType = typeof(void);
        return false;
    }

    private static void InspectName(
        string qualifiedName,
        string identifier,
        HashSet<string> forbiddenNames)
    {
        if (IsForbiddenIdentifier(identifier))
            forbiddenNames.Add(qualifiedName);
    }

    internal static bool IsForbiddenIdentifier(string identifier)
    {
        var words = IdentifierWordPattern()
            .Matches(identifier)
            .Select(static match => match.Value.ToLowerInvariant())
            .ToArray();
        if (words.Any(AtomicDeniedTokens.Contains))
            return true;

        for (var start = 0; start < words.Length; start++)
        {
            for (var length = 2; start + length <= words.Length; length++)
            {
                if (CompoundDeniedTokens.Contains(string.Join('_', words, start, length)))
                    return true;
            }
        }

        return false;
    }

    [GeneratedRegex("[A-Z]+(?=[A-Z][a-z]|[0-9]|$)|[A-Z]?[a-z]+|[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierWordPattern();
}
