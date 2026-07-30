namespace Aevatar.GAgents.Channel.NyxIdRelay;

public static class LarkAttachmentResourceKeys
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var resourceKey = value.Trim();
        if (!Uri.TryCreate(resourceKey, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return resourceKey;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "resources", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(segments[i + 1]);
        }

        return null;
    }
}
