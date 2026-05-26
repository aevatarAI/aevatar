using System.Collections.Concurrent;
using System.Reflection;
using Aevatar.Foundation.Abstractions.Compatibility;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Compatibility;

public static class ProtobufContractCompatibility
{
    private const string TypeUrlPrefix = "type.googleapis.com/";
    private static readonly ConcurrentDictionary<System.Type, CompatibilityInfo> Cache = new();

    public static bool MatchesPayload(Any? payload, System.Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        if (payload == null)
            return false;

        return Cache.GetOrAdd(messageType, BuildInfo).TypeUrls.Contains(payload.TypeUrl);
    }

    public static bool TryUnpack<TMessage>(Any? payload, out TMessage? message)
        where TMessage : class, IMessage<TMessage>, new()
    {
        if (payload == null)
        {
            message = null;
            return false;
        }

        if (payload.TryUnpack<TMessage>(out var unpacked))
        {
            message = unpacked;
            return true;
        }

        var compatibility = Cache.GetOrAdd(typeof(TMessage), BuildInfo);
        if (!compatibility.TypeUrls.Contains(payload.TypeUrl))
        {
            message = null;
            return false;
        }

        try
        {
            var parsed = new TMessage();
            parsed.MergeFrom(payload.Value);
            message = parsed;
            return true;
        }
        catch
        {
            message = null;
            return false;
        }
    }

    public static bool IsCompatibleClrTypeName<TMessage>(string? storedTypeName)
        where TMessage : class, IMessage<TMessage>, new() =>
        IsCompatibleClrTypeName(typeof(TMessage), storedTypeName);

    public static bool IsCompatibleClrTypeName(System.Type messageType, string? storedTypeName)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        if (string.IsNullOrWhiteSpace(storedTypeName))
            return false;

        return Cache.GetOrAdd(messageType, BuildInfo).ClrTypeNames.Contains(storedTypeName.Trim());
    }

    private static CompatibilityInfo BuildInfo(System.Type messageType)
    {
        var primaryClrTypeName = messageType.FullName ?? messageType.Name;
        var clrTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            primaryClrTypeName,
        };

        foreach (var alias in messageType.GetCustomAttributes<LegacyClrTypeNameAttribute>())
        {
            if (!string.IsNullOrWhiteSpace(alias.FullName))
                clrTypeNames.Add(alias.FullName.Trim());
        }

        var typeUrls = new HashSet<string>(StringComparer.Ordinal)
        {
            ResolvePrimaryTypeUrl(messageType, primaryClrTypeName),
        };

        foreach (var alias in messageType.GetCustomAttributes<LegacyProtoFullNameAttribute>())
        {
            if (!string.IsNullOrWhiteSpace(alias.FullName))
                typeUrls.Add(TypeUrlPrefix + alias.FullName.Trim());
        }

        return new CompatibilityInfo(typeUrls, clrTypeNames);
    }

    private static string ResolvePrimaryTypeUrl(System.Type messageType, string primaryClrTypeName)
    {
        var descriptor = ResolveDescriptor(messageType);
        return descriptor != null
            ? TypeUrlPrefix + descriptor.FullName
            : TypeUrlPrefix + primaryClrTypeName;
    }

    private static MessageDescriptor? ResolveDescriptor(System.Type messageType)
    {
        var descriptorProperty = messageType.GetProperty(
            "Descriptor",
            BindingFlags.Public | BindingFlags.Static);
        return descriptorProperty?.GetValue(null) as MessageDescriptor;
    }

    private sealed record CompatibilityInfo(
        HashSet<string> TypeUrls,
        HashSet<string> ClrTypeNames);
}
