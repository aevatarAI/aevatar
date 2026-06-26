using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Aevatar.GAgentService.Abstractions.Services;

public static class ServiceProtocolDescriptorSetBuilder
{
    private static readonly IReadOnlyDictionary<string, FileDescriptor> KnownDependencies = new[]
    {
        Google.Protobuf.WellKnownTypes.Any.Descriptor.File,
        Google.Protobuf.WellKnownTypes.Duration.Descriptor.File,
        Google.Protobuf.WellKnownTypes.Empty.Descriptor.File,
        Google.Protobuf.WellKnownTypes.FieldMask.Descriptor.File,
        Google.Protobuf.WellKnownTypes.Struct.Descriptor.File,
        Google.Protobuf.WellKnownTypes.Timestamp.Descriptor.File,
        Google.Protobuf.WellKnownTypes.StringValue.Descriptor.File,
    }.ToDictionary(file => file.Name, StringComparer.Ordinal);

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
        if (KnownDependencies.TryGetValue(dependencyName, out var file))
            AddFile(file, files);
    }
}
