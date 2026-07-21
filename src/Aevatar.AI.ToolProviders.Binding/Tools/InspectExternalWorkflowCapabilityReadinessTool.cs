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
            "capability": {
              "type": "object",
              "description": "Exact capability candidate returned by list_external_workflow_capabilities",
              "properties": {
                "host_connector": { "type": "object" },
                "nyx_id_user_service": { "type": "object" }
              },
              "minProperties": 1,
              "maxProperties": 1
            },
            "execution_mode": {
              "type": "string",
              "enum": ["interactive", "durable"]
            }
          },
          "required": ["capability", "execution_mode"],
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

            var capabilityJson = args.RawOrStr("capability");
            if (string.IsNullOrWhiteSpace(capabilityJson))
                return JsonDefaults.Error("capability is required");

            ExternalWorkflowCapabilityRef capability;
            try
            {
                capability = JsonParser.Default.Parse<ExternalWorkflowCapabilityRef>(capabilityJson);
            }
            catch (InvalidProtocolBufferException)
            {
                return JsonDefaults.Error("capability must be an exact typed capability candidate");
            }

            if (capability.CapabilityCase == ExternalWorkflowCapabilityRef.CapabilityOneofCase.None)
                return JsonDefaults.Error("capability must select exactly one capability kind");

            if (!ExternalWorkflowCapabilityToolSupport.TryResolveAccess(out var access, out var error))
                return JsonDefaults.Error(error!);

            var readiness = await _readinessPort.InspectAsync(
                new InspectExternalWorkflowCapabilityReadinessRequest(
                    access!,
                    capability,
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
