using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Capabilities.ExecutionActivity;

public interface IExecutionActivityScopeResolver
{
    string? Resolve(EventEnvelope? envelope);
}

public sealed class ExecutionActivityScopeResolver : IExecutionActivityScopeResolver
{
    private const string TypeUrlPrefix = "type.googleapis.com/";

    private static readonly Dictionary<string, MessageDescriptor> DescriptorIndex = BuildDescriptorIndex();

    public string? Resolve(EventEnvelope? envelope)
    {
        if (envelope?.Payload == null)
            return null;

        if (TryResolveKnownScope(envelope.Payload, out var knownScopeId))
            return knownScopeId;

        if (TryResolveByTypedScopeIdField(envelope.Payload, out var typedScopeId))
            return typedScopeId;

        return null;
    }

    private static bool TryResolveKnownScope(Any payload, out string? scopeId)
    {
        if (payload.Is(ChatRequestEvent.Descriptor))
        {
            var request = payload.Unpack<ChatRequestEvent>();
            scopeId = NormalizeScopeId(request.ScopeId)
                ?? NormalizeScopeId(request.ToolContext?.Caller?.ScopeId);
            return scopeId != null;
        }

        if (payload.Is(ServiceRunRecord.Descriptor))
        {
            var record = payload.Unpack<ServiceRunRecord>();
            scopeId = NormalizeScopeId(record.ScopeId)
                ?? NormalizeScopeId(record.Identity?.TenantId);
            return scopeId != null;
        }

        if (payload.Is(BindWorkflowRunDefinitionEvent.Descriptor))
        {
            scopeId = NormalizeScopeId(payload.Unpack<BindWorkflowRunDefinitionEvent>().ScopeId);
            return scopeId != null;
        }

        if (payload.Is(BindWorkflowDefinitionEvent.Descriptor))
        {
            scopeId = NormalizeScopeId(payload.Unpack<BindWorkflowDefinitionEvent>().ScopeId);
            return scopeId != null;
        }

        if (payload.Is(WorkflowRunExecutionStartedEvent.Descriptor))
        {
            scopeId = NormalizeScopeId(payload.Unpack<WorkflowRunExecutionStartedEvent>().ScopeId);
            return scopeId != null;
        }

        if (payload.Is(SubWorkflowInvocationRegisteredEvent.Descriptor))
        {
            scopeId = NormalizeScopeId(payload.Unpack<SubWorkflowInvocationRegisteredEvent>().ScopeId);
            return scopeId != null;
        }

        scopeId = null;
        return false;
    }

    private static bool TryResolveByTypedScopeIdField(Any payload, out string? scopeId)
    {
        scopeId = null;
        if (ResolveDescriptor(payload) is not { } descriptor)
            return false;

        if (descriptor.Parser.ParseFrom(payload.Value) is not IMessage message)
            return false;

        var field = descriptor.FindFieldByName("scope_id");
        if (field == null)
            return false;

        if (field.Accessor.GetValue(message) is not string rawScopeId)
            return false;

        scopeId = NormalizeScopeId(rawScopeId);
        return scopeId != null;
    }

    private static MessageDescriptor? ResolveDescriptor(Any any)
    {
        if (string.IsNullOrWhiteSpace(any.TypeUrl))
            return null;

        var fullName = any.TypeUrl.StartsWith(TypeUrlPrefix, StringComparison.Ordinal)
            ? any.TypeUrl[TypeUrlPrefix.Length..]
            : any.TypeUrl[(any.TypeUrl.LastIndexOf('/') + 1)..];

        return DescriptorIndex.GetValueOrDefault(fullName);
    }

    private static Dictionary<string, MessageDescriptor> BuildDescriptorIndex()
    {
        var files = new[]
        {
            AiMessagesReflection.Descriptor,
            ServiceRunsReflection.Descriptor,
            WorkflowExecutionMessagesReflection.Descriptor,
            AnyReflection.Descriptor,
        };

        var result = new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal);
        foreach (var file in files)
            AddFile(file, result);

        return result;
    }

    private static void AddFile(FileDescriptor file, IDictionary<string, MessageDescriptor> descriptors)
    {
        foreach (var messageType in file.MessageTypes)
            AddDescriptor(messageType, descriptors);

        foreach (var dependency in file.Dependencies)
            AddFile(dependency, descriptors);
    }

    private static void AddDescriptor(MessageDescriptor descriptor, IDictionary<string, MessageDescriptor> descriptors)
    {
        descriptors[descriptor.FullName] = descriptor;
        foreach (var nested in descriptor.NestedTypes)
            AddDescriptor(nested, descriptors);
    }

    private static string? NormalizeScopeId(string? scopeId)
    {
        var normalized = scopeId?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
