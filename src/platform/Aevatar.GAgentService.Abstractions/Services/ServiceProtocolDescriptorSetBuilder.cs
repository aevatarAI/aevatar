using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Aevatar.GAgentService.Abstractions.Services;

public static class ServiceProtocolDescriptorSetBuilder
{
    public static ByteString Build(params MessageDescriptor[] descriptors)
    {
        var files = new Dictionary<string, FileDescriptorProto>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
            AddFile(descriptor.File, files);

        var descriptorSet = new FileDescriptorSet();
        descriptorSet.File.Add(files.Values);
        return descriptorSet.ToByteString();
    }

    private static void AddFile(
        FileDescriptor file,
        IDictionary<string, FileDescriptorProto> files)
    {
        if (files.ContainsKey(file.Name))
            return;

        var proto = file.ToProto();
        foreach (var dependency in file.Dependencies)
            AddFile(dependency, files);
        foreach (var dependency in file.PublicDependencies)
            AddFile(dependency, files);
        foreach (var dependencyName in proto.Dependency)
            AddKnownDependency(dependencyName, files);

        files[file.Name] = proto;
    }

    private static void AddKnownDependency(
        string dependencyName,
        IDictionary<string, FileDescriptorProto> files)
    {
        var file = dependencyName switch
        {
            "google/protobuf/any.proto" => Google.Protobuf.WellKnownTypes.Any.Descriptor.File,
            "google/protobuf/duration.proto" => Google.Protobuf.WellKnownTypes.Duration.Descriptor.File,
            "google/protobuf/empty.proto" => Google.Protobuf.WellKnownTypes.Empty.Descriptor.File,
            "google/protobuf/field_mask.proto" => Google.Protobuf.WellKnownTypes.FieldMask.Descriptor.File,
            "google/protobuf/struct.proto" => Google.Protobuf.WellKnownTypes.Struct.Descriptor.File,
            "google/protobuf/timestamp.proto" => Google.Protobuf.WellKnownTypes.Timestamp.Descriptor.File,
            "google/protobuf/wrappers.proto" => Google.Protobuf.WellKnownTypes.StringValue.Descriptor.File,
            _ => null,
        };

        if (file != null)
            AddFile(file, files);
    }
}
