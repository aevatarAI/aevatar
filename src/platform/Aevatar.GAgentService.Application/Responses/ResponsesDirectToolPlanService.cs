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

            additionalProviders.Add(new ToolSetResponsesToolProvider(toolSet.Sources, _logger));
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
        private readonly ILogger _logger;

        public ToolSetResponsesToolProvider(
            IReadOnlyList<IAgentToolSource> sources,
            ILogger logger)
        {
            _sources = sources ?? throw new ArgumentNullException(nameof(sources));
            _logger = logger ?? NullLogger.Instance;
        }

        public async ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default)
        {
            // Discovery runs on behalf of this request's caller, so the request's typed tool
            // context (NyxID access token, scope, channel) must be visible to context-aware
            // sources. IAgentToolSource.DiscoverToolsAsync has no context parameter, so publish
            // it through the AsyncLocal the tools already read at execution time.
            using var _ = AgentToolContextScope.Push(context.ToolContext);

            var tools = new List<IAgentTool>();
            foreach (var source in _sources)
            {
                try
                {
                    tools.AddRange(await source.DiscoverToolsAsync(ct).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Responses route tool source discovery failed for source {SourceType}; continuing without that source.",
                        source.GetType().Name);
                }
            }

            return tools;
        }
    }
}
