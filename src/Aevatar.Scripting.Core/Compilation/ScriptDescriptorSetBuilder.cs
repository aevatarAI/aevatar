using Aevatar.Scripting.Abstractions.Behaviors;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Aevatar.Scripting.Core.Compilation;

public static class ScriptDescriptorSetBuilder
{
    public static ByteString BuildFromDescriptors(IEnumerable<MessageDescriptor> descriptors) => Build(descriptors);

    public static ByteString Build(ScriptBehaviorDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var messageDescriptors = new List<MessageDescriptor>
        {
            descriptor.StateDescriptor,
            descriptor.ReadModelDescriptor,
        };
        messageDescriptors.AddRange(descriptor.Commands.Values.Select(static x => ScriptMessageTypes.GetDescriptor(x.MessageClrType)));
        messageDescriptors.AddRange(descriptor.Signals.Values.Select(static x => ScriptMessageTypes.GetDescriptor(x.MessageClrType)));
        messageDescriptors.AddRange(descriptor.DomainEvents.Values.Select(static x => ScriptMessageTypes.GetDescriptor(x.MessageClrType)));
        messageDescriptors.AddRange(descriptor.Queries.Values.Select(static x => ScriptMessageTypes.GetDescriptor(x.QueryClrType)));
        messageDescriptors.AddRange(descriptor.Queries.Values.Select(static x => ScriptMessageTypes.GetDescriptor(x.ResultClrType)));
        return Build(messageDescriptors);
    }

    public static ByteString Build(IEnumerable<MessageDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var files = new SortedDictionary<string, FileDescriptorProto>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors.Where(static x => x != null))
            AddFile(descriptor.File, files);

        if (files.Count == 0)
            return ByteString.Empty;

        var descriptorSet = new FileDescriptorSet();
        descriptorSet.File.Add(files.Values);
        return ByteString.CopyFrom(descriptorSet.ToByteArray());
    }

    private static void AddFile(
        FileDescriptor file,
        IDictionary<string, FileDescriptorProto> files)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(files);

        if (files.ContainsKey(file.Name))
            return;

        foreach (var dependency in file.Dependencies)
            AddFile(dependency, files);

        files[file.Name] = file.ToProto();
    }
}
