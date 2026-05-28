using Aevatar.AI.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Sdk.Internal;

internal static class WorkflowRunEventJsonBoundaryCodec
{
    // Refactor (iter104/cluster-2): Old pattern: SDK exposed WorkflowOutputFrame as JSON semantic contract (internal serialization not protobuf). New principle: SDK uses WorkflowRunEventEnvelope proto; JSON only at external wire boundary adapter.
    private static readonly JsonParser Parser = new(
        JsonParser.Settings.Default
            .WithIgnoreUnknownFields(true)
            .WithTypeRegistry(CreateTypeRegistry()));

    public static WorkflowRunEventEnvelope Parse(string payload) =>
        Parser.Parse<WorkflowRunEventEnvelope>(payload);

    private static TypeRegistry CreateTypeRegistry()
    {
        var filesByName = new Dictionary<string, FileDescriptor>(StringComparer.Ordinal);
        AddFiles(
            filesByName,
            AiMessagesReflection.Descriptor,
            WorkflowRunEventEnvelope.Descriptor.File,
            WorkflowRunExecutionStartedEvent.Descriptor.File,
            AnyReflection.Descriptor,
            StructReflection.Descriptor,
            TimestampReflection.Descriptor,
            WrappersReflection.Descriptor);
        return TypeRegistry.FromFiles(filesByName.Values);
    }

    private static void AddFiles(
        IDictionary<string, FileDescriptor> filesByName,
        params FileDescriptor[] files)
    {
        foreach (var file in files)
        {
            filesByName[file.Name] = file;
        }
    }
}
