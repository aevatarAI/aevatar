using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

internal sealed class ChannelContextMiddleware : ILLMCallMiddleware
{
    private readonly ILogger<ChannelContextMiddleware> _logger;

    public ChannelContextMiddleware(ILogger<ChannelContextMiddleware> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
    {
        TryInjectChannelContext(context);
        await next();
    }

    private void TryInjectChannelContext(LLMCallContext context)
    {
        var metadata = context.Request.Metadata;
        if (metadata is null || metadata.Count == 0)
            return;

        if (!metadata.TryGetValue(ChannelMetadataKeys.Platform, out var platform) ||
            string.IsNullOrWhiteSpace(platform))
        {
            return;
        }

        var messages = context.Request.Messages;
        if (messages is null || messages.Count == 0)
            return;

        var systemIndex = messages.FindIndex(m =>
            string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
        if (systemIndex < 0)
            return;

        const string injectedMarker = "aevatar.channel_context_injected";
        if (context.Items.ContainsKey(injectedMarker))
            return;

        try
        {
            var channelContext = BuildChannelContextSection(metadata);
            if (string.IsNullOrWhiteSpace(channelContext))
                return;

            var existing = messages[systemIndex];
            var combined = string.IsNullOrWhiteSpace(existing.Content)
                ? channelContext
                : existing.Content + "\n\n" + channelContext;
            messages[systemIndex] = ChatMessage.System(combined);
            context.Items[injectedMarker] = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject channel context into system message; continuing without it");
        }
    }

    private static string BuildChannelContextSection(IReadOnlyDictionary<string, string> metadata)
    {
        static string Resolve(IReadOnlyDictionary<string, string> values, string key) =>
            values.TryGetValue(key, out var value) ? JsonSerializer.Serialize(value ?? string.Empty) : "\"\"";

        return string.Join(
            "\n",
            [
                "<channel-context>",
                $"platform: {Resolve(metadata, ChannelMetadataKeys.Platform)}",
                $"chat_type: {Resolve(metadata, ChannelMetadataKeys.ChatType)}",
                $"sender_id: {Resolve(metadata, ChannelMetadataKeys.SenderId)}",
                $"sender_name: {Resolve(metadata, ChannelMetadataKeys.SenderName)}",
                $"conversation_id: {Resolve(metadata, ChannelMetadataKeys.ConversationId)}",
                "</channel-context>",
            ]);
    }
}
