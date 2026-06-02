using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

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
            if (!string.IsNullOrWhiteSpace(existing.Content) &&
                existing.Content.Contains("<channel-context>", StringComparison.Ordinal))
            {
                context.Items[injectedMarker] = true;
                return;
            }

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

    internal static string BuildChannelContextSection(IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.Count == 0 ||
            !metadata.TryGetValue(ChannelMetadataKeys.Platform, out var platform) ||
            string.IsNullOrWhiteSpace(platform))
        {
            return string.Empty;
        }

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
                $"platform_message_id: {Resolve(metadata, ChannelMetadataKeys.PlatformMessageId)}",
                $"lark_union_id: {Resolve(metadata, ChannelMetadataKeys.LarkUnionId)}",
                $"lark_chat_id: {Resolve(metadata, ChannelMetadataKeys.LarkChatId)}",
                $"operator_user_id: {Resolve(metadata, ChannelMetadataKeys.LarkOperatorUserId)}",
                $"operator_open_id: {Resolve(metadata, ChannelMetadataKeys.LarkOperatorOpenId)}",
                $"operator_union_id: {Resolve(metadata, ChannelMetadataKeys.LarkOperatorUnionId)}",
                "</channel-context>",
            ]);
    }
}
