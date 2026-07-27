namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

internal static class NyxIdServiceRequestHeaderPolicy
{
    private const int MaximumConditionalHeaderLength = 1024;

    public static bool TryBuild(
        IEnumerable<NyxIdServiceHeader> requestedHeaders,
        bool hasJsonBody,
        out Dictionary<string, string> headers,
        out string? error)
    {
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/json",
        };
        error = null;
        foreach (var requestedHeader in requestedHeaders)
        {
            var name = requestedHeader.Name switch
            {
                NyxIdServiceHeaderName.Accept => "Accept",
                NyxIdServiceHeaderName.ContentType => "Content-Type",
                NyxIdServiceHeaderName.IfMatch => "If-Match",
                NyxIdServiceHeaderName.IfNoneMatch => "If-None-Match",
                _ => null,
            };
            if (name is null)
            {
                error = "header_not_allowed";
                return false;
            }
            var value = requestedHeader.Value;
            if (requestedHeader.Name is NyxIdServiceHeaderName.Accept or NyxIdServiceHeaderName.ContentType)
            {
                if (!string.Equals(value, "application/json", StringComparison.OrdinalIgnoreCase))
                {
                    error = "unsupported_media_type";
                    return false;
                }
                if (requestedHeader.Name == NyxIdServiceHeaderName.ContentType && !hasJsonBody)
                {
                    error = "content_type_without_body";
                    return false;
                }
                continue;
            }

            if (string.IsNullOrEmpty(value) || value.Length > MaximumConditionalHeaderLength ||
                value.Contains('\r') || value.Contains('\n'))
            {
                error = "invalid_conditional_header";
                return false;
            }

            headers[name] = value;
        }

        return true;
    }
}
