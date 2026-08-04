using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

public sealed class ListExternalWorkflowCapabilitiesTool : IAgentTool
{
    private readonly IExternalWorkflowCapabilityListPort _listPort;
    private readonly BindingToolOptions _options;

    public ListExternalWorkflowCapabilitiesTool(
        IExternalWorkflowCapabilityListPort listPort,
        BindingToolOptions? options = null)
    {
        _listPort = listPort;
        _options = options ?? new BindingToolOptions();
    }

    public string Name => "list_external_workflow_capabilities";

    public string Description =>
        "List exact Host Connector operations and caller-visible NyxID UserService operations " +
        "available for workflow authoring in the current scope. Identical display slugs may identify distinct services. " +
        "Returned NyxID selector fields use workflow YAML names (nyxid_operation/nyxid_request); " +
        "inspect_external_workflow_capability_readiness accepts the same selector shape.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "max_results": {
              "type": "integer",
              "description": "Maximum exact capability operations to return"
            }
          },
          "additionalProperties": false
        }
        """;

    public bool IsReadOnly => true;

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson) =>
        BindingToolResultReceipts.CreateCapabilityList(Name, callId, toolName, resultJson);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            if (args.ParseError is not null)
                return JsonDefaults.Error(args.ParseError);

            if (!ExternalWorkflowCapabilityToolSupport.TryResolveAccess(out var access, out var error))
                return JsonDefaults.Error(error!);

            var resolvedAccess = access!;
            var discovery = await _listPort.ListAsync(
                new ListExternalWorkflowCapabilitiesRequest(resolvedAccess),
                ct);
            var requestedMaxResults = args.Int("max_results", _options.MaxListResults);
            var maxResults = Math.Clamp(requestedMaxResults, 1, _options.MaxListResults);
            var limitedDescriptors = discovery.Capabilities.Take(maxResults).ToArray();
            var limited = limitedDescriptors.Select(ToListedCapabilityJsonElement).ToArray();

            return JsonSerializer.Serialize(new
            {
                scope_id = resolvedAccess.ScopeId,
                count = limited.Length,
                total = discovery.Capabilities.Count,
                candidate_count = discovery.CandidateCount,
                rejected_count = discovery.RejectedCount,
                capabilities = limited,
                diagnostics = discovery.Diagnostics.Select(ExternalWorkflowCapabilityToolSupport.ToProtoJsonElement).ToArray(),
            }, JsonDefaults.SnakeCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return JsonDefaults.Error($"External capability list failed: {exception.GetType().Name}");
        }
    }

    private static JsonElement ToListedCapabilityJsonElement(ExternalWorkflowCapabilityDescriptor descriptor)
    {
        var descriptorObject = new JsonObject();
        AddStringIfPresent(descriptorObject, "display_name", descriptor.DisplayName);
        if (descriptor.ReadOnly)
            descriptorObject["read_only"] = true;
        if (descriptor.Destructive)
            descriptorObject["destructive"] = true;

        var source = ExternalWorkflowCapabilityToolSupport.ToProtoJsonNode(descriptor.Source);
        if (source is JsonObject { Count: > 0 })
            descriptorObject["source"] = source;

        var selector = ExternalWorkflowCapabilityToolSupport.BuildAuthoringSelectorNode(descriptor.Selector);
        if (selector is not null)
            descriptorObject["selector"] = selector;

        using var document = JsonDocument.Parse(descriptorObject.ToJsonString());
        return document.RootElement.Clone();
    }

    private static void AddStringIfPresent(JsonObject target, string propertyName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            target[propertyName] = value;
    }
}
