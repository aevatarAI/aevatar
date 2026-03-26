using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AppPlatform.Hosting.Serialization;

internal static class AppPlatformJsonTypeRegistry
{
    public static TypeRegistry CreateDefault()
    {
        var descriptors = new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;

            System.Type[] exportedTypes;
            try
            {
                exportedTypes = assembly.GetExportedTypes();
            }
            catch
            {
                continue;
            }

            foreach (var type in exportedTypes)
            {
                if (type.IsAbstract || type.IsInterface || !type.IsClass)
                    continue;

                var descriptorProperty = type.GetProperty(
                    "Descriptor",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null,
                    typeof(MessageDescriptor),
                    System.Type.EmptyTypes,
                    null);

                if (descriptorProperty?.GetValue(null) is not MessageDescriptor descriptor)
                    continue;

                descriptors[descriptor.FullName] = descriptor;
            }
        }

        descriptors[Any.Descriptor.FullName] = Any.Descriptor;
        descriptors[Struct.Descriptor.FullName] = Struct.Descriptor;
        descriptors[Value.Descriptor.FullName] = Value.Descriptor;
        descriptors[ListValue.Descriptor.FullName] = ListValue.Descriptor;
        descriptors[Timestamp.Descriptor.FullName] = Timestamp.Descriptor;
        descriptors[StringValue.Descriptor.FullName] = StringValue.Descriptor;
        descriptors[BoolValue.Descriptor.FullName] = BoolValue.Descriptor;
        descriptors[Int32Value.Descriptor.FullName] = Int32Value.Descriptor;
        descriptors[Int64Value.Descriptor.FullName] = Int64Value.Descriptor;
        descriptors[UInt32Value.Descriptor.FullName] = UInt32Value.Descriptor;
        descriptors[UInt64Value.Descriptor.FullName] = UInt64Value.Descriptor;
        descriptors[FloatValue.Descriptor.FullName] = FloatValue.Descriptor;
        descriptors[DoubleValue.Descriptor.FullName] = DoubleValue.Descriptor;
        descriptors[BytesValue.Descriptor.FullName] = BytesValue.Descriptor;
        descriptors[Empty.Descriptor.FullName] = Empty.Descriptor;

        return TypeRegistry.FromMessages(descriptors.Values.ToArray());
    }
}
