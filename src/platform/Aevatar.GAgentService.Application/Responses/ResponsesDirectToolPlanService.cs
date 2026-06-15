using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;

namespace Aevatar.GAgentService.Application.Responses;

public interface IResponsesDirectToolPlanService
{
    ResponsesDirectToolPlan Build(ChatRouteAction? routeAction);
}

public sealed record ResponsesDirectToolPlan(
    IReadOnlyList<IResponsesToolProvider> AdditionalToolProviders,
    ResponsesToolChoiceHintPlan ToolChoiceHintPlan,
    ResponsesCommandError? Error)
{
    public static ResponsesDirectToolPlan Empty { get; } = new(
        [],
        ResponsesToolChoiceHintPlan.Empty,
        null);

    public static ResponsesDirectToolPlan FromError(ResponsesCommandError error) =>
        new([], ResponsesToolChoiceHintPlan.Empty, error);

    public static ResponsesDirectToolPlan Success(
        IReadOnlyList<IResponsesToolProvider> additionalToolProviders,
        ResponsesToolChoiceHintPlan toolChoiceHintPlan) =>
        new(additionalToolProviders, toolChoiceHintPlan, null);
}

public sealed class ResponsesDirectToolPlanService(
    IToolSetRegistry toolSetRegistry) : IResponsesDirectToolPlanService
{
    public ResponsesDirectToolPlan Build(ChatRouteAction? routeAction)
    {
        var forwardToModel = routeAction?.ForwardToModel;
        if (forwardToModel is null)
            return ResponsesDirectToolPlan.Empty;

        var additionalProviders = new List<IResponsesToolProvider>();
        if (forwardToModel.ToolSetRef is not null &&
            !string.IsNullOrWhiteSpace(forwardToModel.ToolSetRef.Name))
        {
            var toolSet = toolSetRegistry.Resolve(forwardToModel.ToolSetRef);
            if (!toolSet.IsSuccess)
            {
                var error = toolSet.Error!;
                return ResponsesDirectToolPlan.FromError(new ResponsesCommandError(
                    500,
                    error.Code,
                    error.Message));
            }

            additionalProviders.Add(new ToolSetResponsesToolProvider(toolSet.Sources));
        }

        return ResponsesDirectToolPlan.Success(
            additionalProviders,
            ResponsesToolChoiceHints.Create(
                forwardToModel.ToolChoiceHint?.ToolName,
                forwardToModel.ToolChoiceHint?.PrefilledArguments));
    }

    private sealed class ToolSetResponsesToolProvider : IResponsesToolProvider
    {
        private readonly IReadOnlyList<IAgentToolSource> _sources;

        public ToolSetResponsesToolProvider(IReadOnlyList<IAgentToolSource> sources)
        {
            _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        }

        public async ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default)
        {
            var tools = new List<IAgentTool>();
            foreach (var source in _sources)
                tools.AddRange(await source.DiscoverToolsAsync(ct));

            return tools;
        }
    }
}
