using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

public sealed class InspectExternalWorkflowCapabilityReadinessTool : IAgentTool
{
    private readonly IExternalWorkflowCapabilityReadinessPort _readinessPort;

    public InspectExternalWorkflowCapabilityReadinessTool(
        IExternalWorkflowCapabilityReadinessPort readinessPort)
    {
        _readinessPort = readinessPort;
    }

    public string Name => "inspect_external_workflow_capability_readiness";

    public string Description =>
        "Inspect point-in-time readiness for one exact workflow capability and execution mode. " +
        "Returns typed blockers and trusted remediation locators without returning credentials.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "selector": {
              "type": "object",
              "description": "Exact selector copied from a list_external_workflow_capabilities descriptor",
              "properties": {
                "host_connector": { "type": "object" },
                "nyx_id_operation": {
                  "type": "object",
                  "properties": {
                    "user_service_id": { "type": "string" },
                    "endpoint_id": { "type": "string" }
                  },
                  "required": ["user_service_id", "endpoint_id"],
                  "additionalProperties": false
                }
              },
              "minProperties": 1,
              "maxProperties": 1
            },
            "execution_mode": {
              "type": "string",
              "enum": ["interactive", "durable"]
            }
          },
          "required": ["selector", "execution_mode"],
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

            if (!TryParseExecutionMode(args.Str("execution_mode"), out var executionMode))
                return JsonDefaults.Error("execution_mode must be interactive or durable");

            var selectorJson = args.RawOrStr("selector");
            if (string.IsNullOrWhiteSpace(selectorJson))
                return JsonDefaults.Error("selector is required");

            ExternalWorkflowCapabilitySelector selector;
            try
            {
                selector = JsonParser.Default.Parse<ExternalWorkflowCapabilitySelector>(selectorJson);
            }
            catch (InvalidProtocolBufferException)
            {
                return JsonDefaults.Error("selector must be an exact typed capability selector");
            }

            if (selector.SelectorCase == ExternalWorkflowCapabilitySelector.SelectorOneofCase.None)
                return JsonDefaults.Error("selector must select exactly one capability kind");

            if (!ExternalWorkflowCapabilityToolSupport.TryResolveAccess(out var access, out var error))
                return JsonDefaults.Error(error!);

            var readiness = await _readinessPort.InspectAsync(
                new InspectExternalWorkflowCapabilityReadinessRequest(
                    access!,
                    selector,
                    executionMode),
                ct);
            return ExternalWorkflowCapabilityToolSupport.ProtoJsonFormatter.Format(readiness);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return JsonDefaults.Error($"External capability readiness inspection failed: {exception.GetType().Name}");
        }
    }

    private static bool TryParseExecutionMode(
        string? value,
        out ExternalCapabilityExecutionMode executionMode)
    {
        executionMode = value?.Trim().ToLowerInvariant() switch
        {
            "interactive" => ExternalCapabilityExecutionMode.Interactive,
            "durable" => ExternalCapabilityExecutionMode.Durable,
            _ => ExternalCapabilityExecutionMode.Unspecified,
        };
        return executionMode != ExternalCapabilityExecutionMode.Unspecified;
    }
}
