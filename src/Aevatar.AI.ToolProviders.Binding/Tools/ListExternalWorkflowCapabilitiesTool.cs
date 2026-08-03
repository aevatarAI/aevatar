using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

public sealed class ListExternalWorkflowCapabilitiesTool : ExternalWorkflowCapabilityReadOnlyTool
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

    public override string Name => "list_external_workflow_capabilities";

    public override string Description =>
        "List exact Host Connector operations and caller-visible NyxID UserService operations " +
        "available for workflow authoring in the current scope. Identical display slugs may identify distinct services.";

    public override string ParametersSchema => """
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

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
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

    protected override bool IsVerifiedResult(JsonElement result) =>
        result.TryGetProperty("scope_id", out var scopeId) &&
        scopeId.ValueKind == JsonValueKind.String &&
        result.TryGetProperty("count", out var count) &&
        count.TryGetInt32(out _) &&
        result.TryGetProperty("total", out var total) &&
        total.TryGetInt32(out _) &&
        result.TryGetProperty("candidate_count", out var candidateCount) &&
        candidateCount.TryGetInt32(out _) &&
        result.TryGetProperty("rejected_count", out var rejectedCount) &&
        rejectedCount.TryGetInt32(out _) &&
        result.TryGetProperty("capabilities", out var capabilities) &&
        capabilities.ValueKind == JsonValueKind.Array &&
        result.TryGetProperty("diagnostics", out var diagnostics) &&
        diagnostics.ValueKind == JsonValueKind.Array;

    private static JsonElement ToJsonElement(Google.Protobuf.IMessage descriptor)
    {
        using var document = JsonDocument.Parse(
            ExternalWorkflowCapabilityToolSupport.ProtoJsonFormatter.Format(descriptor));
        return document.RootElement.Clone();
    }
}
