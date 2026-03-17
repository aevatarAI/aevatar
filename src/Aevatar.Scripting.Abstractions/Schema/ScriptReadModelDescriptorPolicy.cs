using Google.Protobuf.Reflection;

namespace Aevatar.Scripting.Abstractions.Schema;

public static class ScriptReadModelDescriptorPolicy
{
    public static void ValidateNoUnsupportedWrapperFields(MessageDescriptor? readModelDescriptor)
    {
        if (readModelDescriptor == null)
            return;

        ValidateNoUnsupportedWrapperFieldsRecursive(readModelDescriptor, new HashSet<string>(StringComparer.Ordinal));
    }

    public static bool IsSupportedLeafMessage(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return string.Equals(
            descriptor.FullName,
            "google.protobuf.Timestamp",
            StringComparison.Ordinal);
    }

    public static bool IsUnsupportedWrapperLeaf(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.FullName switch
        {
            "google.protobuf.StringValue" => true,
            "google.protobuf.BoolValue" => true,
            "google.protobuf.Int32Value" => true,
            "google.protobuf.Int64Value" => true,
            "google.protobuf.UInt32Value" => true,
            "google.protobuf.UInt64Value" => true,
            "google.protobuf.DoubleValue" => true,
            "google.protobuf.FloatValue" => true,
            "google.protobuf.BytesValue" => true,
            _ => false,
        };
    }

    private static void ValidateNoUnsupportedWrapperFieldsRecursive(
        MessageDescriptor descriptor,
        ISet<string> visited)
    {
        if (!visited.Add(descriptor.FullName))
            return;

        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            if (field.FieldType != FieldType.Message || field.MessageType == null)
                continue;

            if (IsUnsupportedWrapperLeaf(field.MessageType))
            {
                throw new InvalidOperationException(
                    $"Scripting read model `{descriptor.FullName}` field `{field.Name}` uses unsupported protobuf wrapper type `{field.MessageType.FullName}`. " +
                    "Use scalar fields, proto3 optional fields, or typed sub-messages instead.");
            }

            if (!IsSupportedLeafMessage(field.MessageType))
                ValidateNoUnsupportedWrapperFieldsRecursive(field.MessageType, visited);
        }
    }
}
