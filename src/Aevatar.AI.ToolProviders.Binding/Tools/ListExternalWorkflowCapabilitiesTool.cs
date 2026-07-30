using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
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
        "available for workflow authoring in the current scope. Identical display slugs may identify distinct services.";

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

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var args = ToolArgs.Parse(argumentsJson);
            if (args.ParseError is not null)
                return JsonDefaults.Error(args.ParseError);

            if (!ExternalWorkflowCapabilityToolSupport.TryResolveAccess(out var access, out var error))
                return JsonDefaults.Error(error!);

            var discovery = await _listPort.ListAsync(
                new ListExternalWorkflowCapabilitiesRequest(access!),
                ct);
            var maxResults = Math.Clamp(args.Int("max_results", _options.MaxListResults), 1, _options.MaxListResults);
            var limited = discovery.Capabilities.Take(maxResults).Select(ToJsonElement).ToArray();

            return JsonSerializer.Serialize(new
            {
                scope_id = access!.ScopeId,
                count = limited.Length,
                total = discovery.Capabilities.Count,
                candidate_count = discovery.CandidateCount,
                rejected_count = discovery.RejectedCount,
                capabilities = limited,
                diagnostics = discovery.Diagnostics.Select(ToJsonElement).ToArray(),
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

    private static JsonElement ToJsonElement(Google.Protobuf.IMessage descriptor)
    {
        using var document = JsonDocument.Parse(
            ExternalWorkflowCapabilityToolSupport.ProtoJsonFormatter.Format(descriptor));
        return document.RootElement.Clone();
    }
}
