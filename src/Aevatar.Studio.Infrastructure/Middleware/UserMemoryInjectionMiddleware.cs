using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Microsoft.Extensions.Logging;


namespace Aevatar.Studio.Infrastructure.Middleware;

/// <summary>
/// LLM 调用中间件：读取请求元数据中预加载的用户记忆文本块，追加到系统消息末尾。
/// 记忆文本由 NyxIdChatEndpoints 在请求入口处加载，存储于
/// <see cref="LLMRequestMetadataKeys.UserMemoryPrompt"/> 键下。
/// </summary>
internal sealed class UserMemoryInjectionMiddleware : ILLMCallMiddleware
{
    private readonly ILogger<UserMemoryInjectionMiddleware> _logger;

    public UserMemoryInjectionMiddleware(ILogger<UserMemoryInjectionMiddleware> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
    {
        TryInjectMemory(context);
        await next();
    }

    private void TryInjectMemory(LLMCallContext context)
    {
        var metadata = context.Request.Metadata;
        if (metadata is null)
            return;

        if (!metadata.TryGetValue(LLMRequestMetadataKeys.UserMemoryPrompt, out var memorySection) ||
            string.IsNullOrWhiteSpace(memorySection))
            return;

        var messages = context.Request.Messages;
        if (messages is null || messages.Count == 0)
            return;

        var systemIndex = messages.FindIndex(m =>
            string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
        if (systemIndex < 0)
            return;

        // Guard against double-injection within the same context object.
        const string injectedMarker = "aevatar.user_memory_injected";
        if (context.Items.ContainsKey(injectedMarker))
            return;

        try
        {
            var existing = messages[systemIndex];
            var combined = string.IsNullOrWhiteSpace(existing.Content)
                ? memorySection
                : existing.Content + "\n\n" + memorySection;
            messages[systemIndex] = ChatMessage.System(combined);
            context.Items[injectedMarker] = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject user memory into system message; continuing without it");
        }
    }
}
