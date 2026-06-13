using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

internal static class NyxIdRelayAttachmentNormalizer
{
    public static IReadOnlyList<AttachmentRef> Normalize(
        NyxIdRelayCallbackPayload payload,
        string platform,
        string platformMessageId)
    {
        var messageId = NormalizeOptional(platformMessageId) ?? NormalizeOptional(payload.MessageId) ?? "message";
        var attachments = new List<AttachmentRef>();
        AddNormalizedAttachments(payload, platform, messageId, attachments);
        AddLarkRawAttachments(payload, platform, messageId, attachments);
        return attachments;
    }

    private static void AddNormalizedAttachments(
        NyxIdRelayCallbackPayload payload,
        string platform,
        string platformMessageId,
        List<AttachmentRef> attachments)
    {
        var contentAttachments = payload.Content?.Attachments;
        if (contentAttachments is null || contentAttachments.Count == 0)
            return;

        for (var index = 0; index < contentAttachments.Count; index++)
        {
            var source = contentAttachments[index];
            var locator = NormalizeOptional(source.Url);
            if (locator is null)
                continue;

            var category = NormalizeOptional(source.ContentType)
                           ?? NormalizeOptional(source.Type)
                           ?? NormalizeOptional(payload.Content?.ContentType)
                           ?? NormalizeOptional(payload.Content?.Type)
                           ?? "file";
            var fileName = NormalizeOptional(source.Filename)
                           ?? NormalizeOptional(source.FileName)
                           ?? NormalizeOptional(source.Name)
                           ?? string.Empty;
            var kind = MapKind(category, source.MimeType, fileName);
            attachments.Add(new AttachmentRef
            {
                AttachmentId = BuildAttachmentId(platform, platformMessageId, ToAttachmentIdKind(kind), locator),
                Kind = kind,
                Name = fileName,
                ContentType = NormalizeOptional(source.MimeType) ?? category,
                SizeBytes = source.SizeBytes.GetValueOrDefault() > 0 ? source.SizeBytes.GetValueOrDefault() : 0,
                ExternalUrl = IsHttpUrl(locator) ? locator : string.Empty,
            });
        }
    }

    private static void AddLarkRawAttachments(
        NyxIdRelayCallbackPayload payload,
        string platform,
        string platformMessageId,
        List<AttachmentRef> attachments)
    {
        if (!IsLark(platform) || payload.RawPlatformData is not { } raw || raw.ValueKind != JsonValueKind.Object)
            return;

        if (!raw.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object ||
            !evt.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (attachments.Count > 0)
            return;

        var messageType = ReadStringProperty(message, "message_type");
        if (!TryReadLarkMessageContent(message, out var content))
            return;

        if (TryReadString(content, "image_key", out var imageKey))
        {
            attachments.Add(new AttachmentRef
            {
                AttachmentId = BuildAttachmentId(platform, platformMessageId, "image", imageKey),
                Kind = AttachmentKind.Image,
                Name = ReadFirstString(content, "file_name", "name"),
                ContentType = "image",
                BlobRef = $"lark:image_key:{imageKey}",
                SizeBytes = ReadFirstInt64(content, "size", "file_size"),
            });
            return;
        }

        if (TryReadString(content, "file_key", out var fileKey))
        {
            var fileName = ReadFirstString(content, "file_name", "name");
            attachments.Add(new AttachmentRef
            {
                AttachmentId = BuildAttachmentId(platform, platformMessageId, "file", fileKey),
                Kind = AttachmentKind.File,
                Name = fileName,
                ContentType = ReadFirstString(content, "mime_type", "file_type") is { Length: > 0 } contentType
                    ? contentType
                    : MapLarkFileContentType(messageType),
                BlobRef = $"lark:file_key:{fileKey}",
                SizeBytes = ReadFirstInt64(content, "size", "file_size"),
            });
        }
    }

    private static bool TryReadLarkMessageContent(JsonElement message, out JsonElement content)
    {
        content = default;
        if (!message.TryGetProperty("content", out var rawContent))
            return false;

        if (rawContent.ValueKind == JsonValueKind.Object)
        {
            content = rawContent;
            return true;
        }

        if (rawContent.ValueKind != JsonValueKind.String)
            return false;

        var rawText = rawContent.GetString();
        if (string.IsNullOrWhiteSpace(rawText))
            return false;

        try
        {
            using var document = JsonDocument.Parse(rawText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            content = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AttachmentKind MapKind(string? category, string? mimeType, string? fileName)
    {
        var normalizedCategory = NormalizeOptional(category)?.ToLowerInvariant();
        if (normalizedCategory is "image" or "photo" or "picture")
            return AttachmentKind.Image;
        if (normalizedCategory is "audio" or "voice")
            return AttachmentKind.Audio;
        if (normalizedCategory is "video")
            return AttachmentKind.Video;
        if (normalizedCategory is "link" or "url")
            return AttachmentKind.Link;
        if (normalizedCategory is "file" or "document")
            return AttachmentKind.File;

        var normalizedMime = NormalizeOptional(mimeType)?.ToLowerInvariant();
        if (normalizedMime?.StartsWith("image/", StringComparison.Ordinal) == true)
            return AttachmentKind.Image;
        if (normalizedMime?.StartsWith("audio/", StringComparison.Ordinal) == true)
            return AttachmentKind.Audio;
        if (normalizedMime?.StartsWith("video/", StringComparison.Ordinal) == true)
            return AttachmentKind.Video;

        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => AttachmentKind.Image,
            ".mp3" or ".wav" or ".ogg" or ".m4a" => AttachmentKind.Audio,
            ".mp4" or ".mov" or ".webm" => AttachmentKind.Video,
            _ => AttachmentKind.File,
        };
    }

    private static string MapLarkFileContentType(string messageType) =>
        messageType switch
        {
            "audio" => "audio",
            "media" or "video" => "video",
            _ => "application/octet-stream",
        };

    private static string ToAttachmentIdKind(AttachmentKind kind) =>
        kind switch
        {
            AttachmentKind.Image => "image",
            AttachmentKind.Audio => "audio",
            AttachmentKind.Video => "video",
            AttachmentKind.Link => "link",
            AttachmentKind.Card => "card",
            _ => "file",
        };

    private static string BuildAttachmentId(string platform, string platformMessageId, string kind, string locator)
    {
        var effectiveMessageId = NormalizeOptional(platformMessageId) ?? "message";
        var locatorHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(locator)))
            .ToLowerInvariant()[..16];
        return $"{platform}:{effectiveMessageId}:{kind}:{locatorHash}";
    }

    private static bool IsLark(string platform) =>
        string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string ReadFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryReadString(element, propertyName, out var value))
                return value;
        }

        return string.Empty;
    }

    private static long ReadFirstInt64(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ReadInt64Property(element, propertyName);
            if (value > 0)
                return value;
        }

        return 0;
    }

    private static string ReadStringProperty(JsonElement element, string propertyName) =>
        TryReadString(element, propertyName, out var value) ? value : string.Empty;

    private static bool TryReadString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var parsed = property.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(parsed))
            return false;

        value = parsed;
        return true;
    }

    private static long ReadInt64Property(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var numericValue))
            return Math.Max(0, numericValue);

        if (property.ValueKind == JsonValueKind.String &&
            long.TryParse(property.GetString(), out var stringValue))
        {
            return Math.Max(0, stringValue);
        }

        return 0;
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
