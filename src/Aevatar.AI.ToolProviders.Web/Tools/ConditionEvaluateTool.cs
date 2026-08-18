using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Tools;

namespace Aevatar.AI.ToolProviders.Web.Tools;

/// <summary>
/// Declares an effect-free condition proposal. NyxIdChat intercepts this tool
/// call and performs the comparison in the authoritative conversation actor;
/// this implementation is fail-closed if invoked outside that boundary.
/// </summary>
public sealed class ConditionEvaluateTool : IAgentTool
{
    public const string ToolName = "condition_evaluate";

    public string Name => ToolName;

    public string Description =>
        "Propose an integer observation for comparison with a previously committed numeric " +
        "threshold input. The server owns the threshold and branch decision. This tool never " +
        "performs the guarded external operation.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "source_input_request_id": {
              "type": "string",
              "description": "Exact request id of the committed numeric threshold input."
            },
            "observed_value": {
              "type": "integer",
              "description": "Observed integer value to compare with the server-owned threshold."
            },
            "guarded_tool_name": {
              "type": "string",
              "description": "Exact tool name that may run only when the condition is true."
            }
          },
          "required": ["source_input_request_id", "observed_value", "guarded_tool_name"]
        }
        """;

    public ToolPresentationDescriptor Presentation =>
        ToolPresentationDescriptors.BuiltIn(Name, "Evaluate condition", Description);

    public bool IsReadOnly => true;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        Task.FromResult("{\"error\":\"condition_requires_conversation_actor\"}");
}
