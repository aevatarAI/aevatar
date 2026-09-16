using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Responses;

/// <summary>Maps a configured model to the exact NyxID service target used by the LLM provider.</summary>
internal sealed class ResponsesRouteResolver : IResponsesRouteResolver
{
    private readonly ILLMModelRouteApplicationService _routeService;
    private readonly ILogger<ResponsesRouteResolver> _logger;

    public ResponsesRouteResolver(
        ILLMModelRouteApplicationService routeService,
        ILogger<ResponsesRouteResolver> logger)
    {
        _routeService = routeService ?? throw new ArgumentNullException(nameof(routeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<LLMRouteTarget?> ResolveRouteTargetAsync(
        string serviceSlug,
        string upstreamModelId,
        ResponsesCallerScope callerScope,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamModelId);
        ArgumentNullException.ThrowIfNull(callerScope);

        try
        {
            var source = await _routeService
                .ResolveAsync(callerScope.ScopeId, serviceSlug, upstreamModelId, ct)
                .ConfigureAwait(false);
            return source is null ? null : BuildRouteTarget(source);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMModelCatalogApplicationException ex)
        {
            _logger.LogWarning(
                ex,
                "Model source resolution failed; qualified model routing is unavailable.");
            throw new ResponsesRouteUnavailableException(ex.Message, ex.InnerException ?? ex);
        }
    }

    internal static LLMRouteTarget BuildRouteTarget(NyxIdResolvedModelSource source)
    {
        var target = source switch
        {
            NyxIdResolvedCatalogModelSource catalog =>
                new LLMRouteTarget
                {
                    CatalogServiceId = catalog.CatalogServiceId,
                    ServiceSlugSnapshot = catalog.ServiceSlug,
                },
            NyxIdResolvedUserModelSource user =>
                new LLMRouteTarget
                {
                    UserServiceId = user.UserServiceId,
                    ServiceSlugSnapshot = user.ServiceSlug,
                },
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        LLMSelectionPolicy.ValidateRouteTarget(target);
        return target;
    }
}

// Host adapter from the Responses command contract to the shared chat-routing application policy.
internal sealed class ResponsesChatRouteDecisionPort(
    IChatRoutePolicyQueryPort queryPort,
    ChatRouteResolver resolver) : IResponsesChatRouteDecisionPort
{
    public async Task<ChatRouteDecision> ResolveAsync(
        ResponsesChatRouteDecisionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ownerScope = OwnerScope.ForNyxIdNative(request.CallerScope.ScopeId);
        var snapshot = await queryPort.LookupForCallerAsync(ownerScope, ct);
        return resolver.Resolve(snapshot, new ChatRouteInput
        {
            SourceKind = ChatSourceKind.NyxResponses,
            CallerScope = ownerScope.Clone(),
            Channel = string.Empty,
            CommandName = request.CommandName,
            ContentHint = request.ContentHint,
            ToolMode = request.ToolMode,
            Model = request.Model,
        });
    }
}
