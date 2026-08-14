using System.Collections;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;

internal static class DurableCallbackEnvelopeCredentialGuard
{
    private const string TypeUrlPrefix = "type.googleapis.com/";
    private const int MaxDepth = 64;
    private static readonly object DescriptorIndexLock = new();
    private static Dictionary<string, MessageDescriptor>? DescriptorIndex;

    public static void ThrowIfContainsRuntimeCredential(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ThrowIfContainsRuntimeCredential(envelope, nameof(EventEnvelope));
    }

    internal static bool TryFindRuntimeCredential(EventEnvelope envelope, out string violationPath)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return TryFindRuntimeCredential(envelope, nameof(EventEnvelope), depth: 0, out violationPath);
    }

    internal static void ThrowIfContainsRuntimeCredential(IMessage message, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        if (TryFindRuntimeCredential(message, rootPath, depth: 0, out var violationPath))
        {
            throw new InvalidOperationException(
                $"Durable callback trigger envelope contains runtime credential field '{violationPath}'. " +
                "Callback payloads must carry stable actor-owned identifiers only.");
        }
    }

    private static bool TryFindRuntimeCredential(
        IMessage message,
        string path,
        int depth,
        out string violationPath)
    {
        if (depth > MaxDepth)
        {
            violationPath = path;
            return false;
        }

        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            if (IsRuntimeCredentialField(field))
            {
                var credentialValue = field.Accessor.GetValue(message);
                if (!IsDefaultCredentialValue(credentialValue))
                {
                    violationPath = string.Concat(path, ".", field.Name);
                    return true;
                }
            }

            if (field.FieldType != FieldType.Message && !field.IsMap)
                continue;

            var value = field.Accessor.GetValue(message);
            var fieldPath = string.Concat(path, ".", field.Name);
            if (TryFindRuntimeCredentialInValue(field, value, fieldPath, depth + 1, out violationPath))
                return true;
        }

        violationPath = string.Empty;
        return false;
    }

    private static bool TryFindRuntimeCredentialInValue(
        FieldDescriptor field,
        object? value,
        string path,
        int depth,
        out string violationPath)
    {
        if (value is null)
        {
            violationPath = string.Empty;
            return false;
        }

        if (field.IsMap)
        {
            if (TryFindRuntimeCredentialInMap(field, value, path, depth, out violationPath))
                return true;

            violationPath = string.Empty;
            return false;
        }

        if (field.IsRepeated)
        {
            var index = 0;
            foreach (var item in (IEnumerable)value)
            {
                if (item is IMessage repeatedMessage &&
                    TryFindRuntimeCredentialMessage(repeatedMessage, string.Concat(path, "[", index, "]"), depth, out violationPath))
                {
                    return true;
                }

                index++;
            }

            violationPath = string.Empty;
            return false;
        }

        if (value is IMessage nestedMessage)
            return TryFindRuntimeCredentialMessage(nestedMessage, path, depth, out violationPath);

        violationPath = string.Empty;
        return false;
    }

    private static bool TryFindRuntimeCredentialInMap(
        FieldDescriptor field,
        object mapValue,
        string path,
        int depth,
        out string violationPath)
    {
        var keyField = field.MessageType.FindFieldByNumber(1);
        var valueField = field.MessageType.FindFieldByNumber(2);

        if (valueField?.FieldType != FieldType.Message)
        {
            violationPath = string.Empty;
            return false;
        }

        foreach (var entry in (IEnumerable)mapValue)
        {
            var entryType = entry.GetType();
            var key = entryType.GetProperty("Key")?.GetValue(entry);
            var value = entryType.GetProperty("Value")?.GetValue(entry);
            if (value is IMessage nestedMessage &&
                TryFindRuntimeCredentialMessage(
                    nestedMessage,
                    string.Concat(path, "[", key?.ToString() ?? "<null>", "]"),
                    depth,
                    out violationPath))
            {
                return true;
            }
        }

        violationPath = string.Empty;
        return false;
    }

    private static bool TryFindRuntimeCredentialMessage(
        IMessage message,
        string path,
        int depth,
        out string violationPath)
    {
        if (message is Any any)
            return TryFindRuntimeCredentialInAny(any, path, depth, out violationPath);

        return TryFindRuntimeCredential(message, path, depth, out violationPath);
    }

    private static bool TryFindRuntimeCredentialInAny(Any any, string path, int depth, out string violationPath)
    {
        if (ResolveAnyDescriptor(any) is not { } descriptor)
        {
            violationPath = string.Empty;
            return false;
        }

        var unpacked = descriptor.Parser.ParseFrom(any.Value) as IMessage;
        if (unpacked is null)
        {
            violationPath = string.Empty;
            return false;
        }

        return TryFindRuntimeCredential(unpacked, string.Concat(path, "<", descriptor.FullName, ">"), depth, out violationPath);
    }

    private static MessageDescriptor? ResolveAnyDescriptor(Any any)
    {
        if (string.IsNullOrWhiteSpace(any.TypeUrl))
            return null;

        var fullName = any.TypeUrl.StartsWith(TypeUrlPrefix, StringComparison.Ordinal)
            ? any.TypeUrl[TypeUrlPrefix.Length..]
            : any.TypeUrl[(any.TypeUrl.LastIndexOf('/') + 1)..];
        return ResolveDescriptor(fullName);
    }

    private static MessageDescriptor? ResolveDescriptor(string fullName)
    {
        lock (DescriptorIndexLock)
        {
            DescriptorIndex ??= BuildDescriptorIndex();
            if (DescriptorIndex.TryGetValue(fullName, out var descriptor))
                return descriptor;

            DescriptorIndex = BuildDescriptorIndex();
            return DescriptorIndex.GetValueOrDefault(fullName);
        }
    }

    private static Dictionary<string, MessageDescriptor> BuildDescriptorIndex()
    {
        var descriptors = new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal);
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(static assembly => !assembly.IsDynamic).ToArray();
        foreach (var assembly in assemblies)
        {
            foreach (var fileDescriptor in EnumerateFileDescriptors(assembly))
                IndexFileDescriptor(fileDescriptor, descriptors);
        }

        foreach (var assembly in assemblies)
        {
            foreach (var messageDescriptor in EnumerateMessageDescriptors(assembly))
                IndexMessageDescriptor(messageDescriptor, descriptors);
        }

        return descriptors;
    }

    private static IEnumerable<FileDescriptor> EnumerateFileDescriptors(Assembly assembly)
    {
        System.Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(static type => type is not null).Cast<System.Type>().ToArray();
        }

        foreach (var type in types)
        {
            var property = type.GetProperty(
                "Descriptor",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (property?.PropertyType != typeof(FileDescriptor))
                continue;

            if (property.GetValue(null) is FileDescriptor descriptor)
                yield return descriptor;
        }
    }

    private static IEnumerable<MessageDescriptor> EnumerateMessageDescriptors(Assembly assembly)
    {
        System.Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(static type => type is not null).Cast<System.Type>().ToArray();
        }

        foreach (var type in types)
        {
            if (!typeof(IMessage).IsAssignableFrom(type))
                continue;

            var property = type.GetProperty(
                "Descriptor",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (property?.PropertyType != typeof(MessageDescriptor))
                continue;

            var descriptor = TryGetMessageDescriptor(property);
            if (descriptor is not null)
                yield return descriptor;
        }
    }

    private static MessageDescriptor? TryGetMessageDescriptor(PropertyInfo property)
    {
        try
        {
            return property.GetValue(null) as MessageDescriptor;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
        catch (TypeInitializationException)
        {
            return null;
        }
    }

    private static void IndexFileDescriptor(
        FileDescriptor fileDescriptor,
        IDictionary<string, MessageDescriptor> descriptors)
    {
        foreach (var message in fileDescriptor.MessageTypes)
            IndexMessageDescriptor(message, descriptors);
    }

    private static void IndexMessageDescriptor(
        MessageDescriptor message,
        IDictionary<string, MessageDescriptor> descriptors)
    {
        descriptors[message.FullName] = message;
        foreach (var nested in message.NestedTypes)
            IndexMessageDescriptor(nested, descriptors);
    }

    private static bool IsRuntimeCredentialField(FieldDescriptor field)
    {
        if (IsStableActorOwnedCallbackIdentifierField(field))
            return false;

        var name = field.Name;
        return string.Equals(name, "reply_token", StringComparison.Ordinal) ||
               string.Equals(name, "reply_token_expires_at_unix_ms", StringComparison.Ordinal) ||
               string.Equals(name, "nyx_user_access_token", StringComparison.Ordinal) ||
               name.EndsWith("_token", StringComparison.Ordinal);
    }

    private static bool IsStableActorOwnedCallbackIdentifierField(FieldDescriptor field) =>
        (field.ContainingType.FullName, field.FieldNumber) is
            ("aevatar.workflow.WorkflowToolCallAttemptCompletedEvent", 9) or
            ("aevatar.workflow.WorkflowToolCallTimeoutFiredEvent", 6) or
            ("aevatar.workflow.WorkflowToolCallRetryFiredEvent", 6) or
            ("aevatar.workflow.WorkflowToolCallExecutionRecoveryFiredEvent", 6) or
            ("aevatar.workflow.WorkflowLeaseExpirationFiredEvent", 2);

    private static bool IsDefaultCredentialValue(object? value)
    {
        return value switch
        {
            null => true,
            string stringValue => string.IsNullOrEmpty(stringValue),
            ByteString bytes => bytes.Length == 0,
            int intValue => intValue == 0,
            long longValue => longValue == 0,
            uint uintValue => uintValue == 0,
            ulong ulongValue => ulongValue == 0,
            float floatValue => floatValue == 0,
            double doubleValue => doubleValue == 0,
            bool boolValue => !boolValue,
            System.Enum enumValue => Convert.ToInt64(enumValue) == 0,
            IEnumerable enumerable => !enumerable.Cast<object>().Any(),
            _ => false,
        };
    }
}
