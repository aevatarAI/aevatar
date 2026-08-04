using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Application.Responses;

public interface IResponsesDirectToolPlanService
{
    ResponsesDirectToolPlan Build(ChatRouteAction? routeAction);
}

public sealed record ResponsesDirectToolPlan(
    IReadOnlyList<IResponsesToolProvider> AdditionalToolProviders,
    ResponsesToolChoiceHintPlan ToolChoiceHintPlan,
    ResponsesCommandError? Error,
    string ResolvedToolSetName = "")
{
    public static ResponsesDirectToolPlan Empty { get; } = new(
        [],
        ResponsesToolChoiceHintPlan.Empty,
        null);

    public static ResponsesDirectToolPlan FromError(ResponsesCommandError error) =>
        new([], ResponsesToolChoiceHintPlan.Empty, error);

    public static ResponsesDirectToolPlan Success(
        IReadOnlyList<IResponsesToolProvider> additionalToolProviders,
        ResponsesToolChoiceHintPlan toolChoiceHintPlan,
        string resolvedToolSetName = "") =>
        new(additionalToolProviders, toolChoiceHintPlan, null, resolvedToolSetName);
}

public sealed class ResponsesDirectToolPlanService(
    IToolSetRegistry toolSetRegistry,
    ILogger<ResponsesDirectToolPlanService>? logger = null) : IResponsesDirectToolPlanService
{
    private readonly ILogger _logger = logger ?? NullLogger<ResponsesDirectToolPlanService>.Instance;

    public ResponsesDirectToolPlan Build(ChatRouteAction? routeAction)
    {
        var forwardToModel = routeAction?.ForwardToModel;
        if (forwardToModel is null)
            return ResponsesDirectToolPlan.Empty;

        var additionalProviders = new List<IResponsesToolProvider>();
        var resolvedToolSetName = string.Empty;
        if (forwardToModel.ToolSetRef is not null &&
            !string.IsNullOrWhiteSpace(forwardToModel.ToolSetRef.Name))
        {
            var toolSet = toolSetRegistry.Resolve(forwardToModel.ToolSetRef.Name);
            if (!toolSet.IsSuccess)
            {
                var error = toolSet.Error!;
                return ResponsesDirectToolPlan.FromError(new ResponsesCommandError(
                    500,
                    error.Code,
                    error.Message));
            }

            // Canonical name the run executor will re-resolve against (SSOT for the persisted
            // command's tool_set_name). Prefer the registry's resolved name so it matches whatever
            // the registry canonicalized to; same value drives the provider built right below, so
            // the facade's classification and the off-grain run materialize identical sources.
            resolvedToolSetName = toolSet.Name ?? forwardToModel.ToolSetRef.Name.Trim();
            additionalProviders.Add(new ToolSetResponsesToolProvider(toolSet.Sources, _logger));
        }

        return ResponsesDirectToolPlan.Success(
            additionalProviders,
            ResponsesToolChoiceHints.Create(
                forwardToModel.ToolChoiceHint?.ToolName,
                forwardToModel.ToolChoiceHint?.PrefilledArguments),
            resolvedToolSetName);
    }
}
