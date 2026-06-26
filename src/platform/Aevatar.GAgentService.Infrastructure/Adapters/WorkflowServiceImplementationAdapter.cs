using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

public sealed class WorkflowServiceImplementationAdapter : IServiceImplementationAdapter
{
    private readonly IWorkflowDefinitionParser _workflowDefinitionParser;

    public WorkflowServiceImplementationAdapter(IWorkflowDefinitionParser workflowDefinitionParser)
    {
        _workflowDefinitionParser = workflowDefinitionParser ?? throw new ArgumentNullException(nameof(workflowDefinitionParser));
    }

    public ServiceImplementationKind ImplementationKind => ServiceImplementationKind.Workflow;

    public async Task<PreparedServiceRevisionArtifact> PrepareRevisionAsync(
        PrepareServiceRevisionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var spec = request.Spec?.WorkflowSpec
            ?? throw new InvalidOperationException("workflow implementation_spec is required.");
        if (string.IsNullOrWhiteSpace(spec.WorkflowYaml))
            throw new InvalidOperationException("workflow_yaml is required.");

        var parse = await _workflowDefinitionParser.ParseWorkflowYamlAsync(spec.WorkflowYaml, ct);
        if (!parse.Succeeded)
            throw new InvalidOperationException(parse.Error);

        var resolvedWorkflowName = spec.WorkflowName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resolvedWorkflowName))
        {
            resolvedWorkflowName = parse.WorkflowName;
        }
        else if (!string.Equals(resolvedWorkflowName, parse.WorkflowName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("workflow_name must match workflow_yaml name.");
        }

        return new PreparedServiceRevisionArtifact
        {
            Identity = request.Spec.Identity.Clone(),
            RevisionId = request.Spec.RevisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            ProtocolDescriptorSet = BuildProtocolDescriptorSet(
                ChatRequestEvent.Descriptor,
                ChatResponseEvent.Descriptor),
            Endpoints =
            {
                new ServiceEndpointDescriptor
                {
                    EndpointId = "chat",
                    DisplayName = "chat",
                    Kind = ServiceEndpointKind.Chat,
                    RequestTypeUrl = GetTypeUrl(ChatRequestEvent.Descriptor),
                    ResponseTypeUrl = GetTypeUrl(ChatResponseEvent.Descriptor),
                    Description = "Workflow chat endpoint.",
                },
            },
            DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    WorkflowName = resolvedWorkflowName,
                    WorkflowYaml = spec.WorkflowYaml,
                    DefinitionActorId = spec.DefinitionActorId ?? string.Empty,
                    InlineWorkflowYamls = { spec.InlineWorkflowYamls },
                },
            },
        };
    }

    private static string GetTypeUrl(MessageDescriptor descriptor) =>
        $"type.googleapis.com/{descriptor.FullName}";

    private static ByteString BuildProtocolDescriptorSet(params MessageDescriptor[] descriptors)
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
