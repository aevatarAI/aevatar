using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.Middleware;

/// <summary>
/// LLM 调用中间件：读取请求元数据中预加载的已连接服务上下文，追加到系统消息末尾。
/// 上下文由 NyxIdChatEndpoints 在请求入口处加载（含服务清单和 API hints），
/// 存储于 <see cref="LLMRequestMetadataKeys.ConnectedServicesContext"/> 键下。
/// </summary>
internal sealed class ConnectedServicesContextMiddleware : ILLMCallMiddleware
{
    private readonly ILogger<ConnectedServicesContextMiddleware> _logger;

    public ConnectedServicesContextMiddleware(ILogger<ConnectedServicesContextMiddleware> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
    {
        TryInjectConnectedServices(context);
        await next();
    }

    private void TryInjectConnectedServices(LLMCallContext context)
    {
        var metadata = context.Request.Metadata;
        if (metadata is null)
            return;

        if (!metadata.TryGetValue(LLMRequestMetadataKeys.ConnectedServicesContext, out var servicesContext) ||
            string.IsNullOrWhiteSpace(servicesContext))
            return;

        var messages = context.Request.Messages;
        if (messages is null || messages.Count == 0)
            return;

        var systemIndex = messages.FindIndex(m =>
            string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
        if (systemIndex < 0)
            return;

        // Guard against double-injection within the same context object.
        const string injectedMarker = "aevatar.connected_services_injected";
        if (context.Items.ContainsKey(injectedMarker))
            return;

        try
        {
            var existing = messages[systemIndex];
            var combined = string.IsNullOrWhiteSpace(existing.Content)
                ? servicesContext
                : existing.Content + "\n\n" + servicesContext;
            messages[systemIndex] = ChatMessage.System(combined);
            context.Items[injectedMarker] = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject connected services context into system message; continuing without it");
        }
    }
}
