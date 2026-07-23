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

        static List<string> BuildIdentityHintLines(IReadOnlyDictionary<string, string> values)
        {
            var hints = new Dictionary<int, Dictionary<string, string>>();
            foreach (var (key, value) in values)
            {
                if (!key.StartsWith(ChannelMetadataKeys.IdentityHintKeyPrefix, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var remainder = key[ChannelMetadataKeys.IdentityHintKeyPrefix.Length..];
                var separatorIndex = remainder.IndexOf('.', StringComparison.Ordinal);
                if (separatorIndex <= 0 || !int.TryParse(remainder[..separatorIndex], out var index))
                {
                    continue;
                }

                var field = remainder[(separatorIndex + 1)..];
                if (field is not ChannelMetadataKeys.IdentityHintSubjectField and
                    not ChannelMetadataKeys.IdentityHintKindField and
                    not ChannelMetadataKeys.IdentityHintValueField)
                {
                    continue;
                }

                if (!hints.TryGetValue(index, out var hint))
                {
                    hint = new Dictionary<string, string>();
                    hints[index] = hint;
                }

                hint[field] = value;
            }

            var lines = new List<string>();
            foreach (var hint in hints.OrderBy(pair => pair.Key).Select(pair => pair.Value))
            {
                if (!hint.TryGetValue(ChannelMetadataKeys.IdentityHintSubjectField, out var subject) ||
                    !hint.TryGetValue(ChannelMetadataKeys.IdentityHintKindField, out var kind) ||
                    !hint.TryGetValue(ChannelMetadataKeys.IdentityHintValueField, out var value))
                {
                    continue;
                }

                lines.Add(
                    $"- subject: {JsonSerializer.Serialize(subject)}, kind: {JsonSerializer.Serialize(kind)}, value: {JsonSerializer.Serialize(value)}");
            }

            return lines;
        }

        var lines = new List<string>
        {
            "<channel-context>",
            $"platform: {Resolve(metadata, ChannelMetadataKeys.Platform)}",
            $"chat_type: {Resolve(metadata, ChannelMetadataKeys.ChatType)}",
            $"sender_id: {Resolve(metadata, ChannelMetadataKeys.SenderId)}",
            $"sender_name: {Resolve(metadata, ChannelMetadataKeys.SenderName)}",
        };

        // Only emit the mentions line when the message actually mentioned someone, so turns with no
        // @-mentions don't carry a noisy empty field. The value is already a readable
        // `name <platform_id>; ...` list, so emit it raw rather than through Resolve; JSON-escaping would
        // mangle the `<>` delimiters and any non-ASCII (e.g. CJK) display names into `\uXXXX`.
        if (metadata.TryGetValue(ChannelMetadataKeys.Mentions, out var mentions) &&
            !string.IsNullOrWhiteSpace(mentions))
        {
            lines.Add($"mentions: {mentions}");
        }

        lines.Add($"conversation_id: {Resolve(metadata, ChannelMetadataKeys.ConversationId)}");
        lines.Add($"platform_message_id: {Resolve(metadata, ChannelMetadataKeys.PlatformMessageId)}");

        var identityHints = BuildIdentityHintLines(metadata);
        if (identityHints.Count > 0)
        {
            lines.Add("identity_hints:");
            lines.AddRange(identityHints);
        }

        lines.Add("</channel-context>");
        return string.Join("\n", lines);
    }
}
