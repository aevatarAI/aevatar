using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowJsonTypeRegistry
{
    private static readonly FileDescriptor[] BaseFiles =
    [
        WorkflowRunEventEnvelope.Descriptor.File,
        WorkflowRunExecutionStartedEvent.Descriptor.File,
        WorkflowRunState.Descriptor.File,
        AnyReflection.Descriptor,
        StructReflection.Descriptor,
        TimestampReflection.Descriptor,
        WrappersReflection.Descriptor,
    ];

    public static TypeRegistry Default { get; } = Create();

    public static TypeRegistry Create(params FileDescriptor[] additionalFiles)
    {
        var filesByName = new Dictionary<string, FileDescriptor>(StringComparer.Ordinal);
        AddFiles(filesByName, additionalFiles);
        AddFiles(filesByName, BaseFiles);
        return TypeRegistry.FromFiles(filesByName.Values.ToArray());
    }

    private static void AddFiles(
        IDictionary<string, FileDescriptor> filesByName,
        IEnumerable<FileDescriptor> files)
    {
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            filesByName[file.Name] = file;
        }
    }
}
