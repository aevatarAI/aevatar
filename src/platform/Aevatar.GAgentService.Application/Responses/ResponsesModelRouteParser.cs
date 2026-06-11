namespace Aevatar.GAgentService.Application.Responses;

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Host endpoints parsed OpenRouter-style vendor/model strings while building provider requests.
//   New principle: Application owns route-slug parsing for command execution; Host only supplies external request fields.
public static class ResponsesModelRouteParser
{
    public static ResponsesModelRoute Parse(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var trimmed = model.Trim();
        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex <= 0 || slashIndex >= trimmed.Length - 1)
            return new ResponsesModelRoute(null, trimmed);

        var prefix = trimmed[..slashIndex];
        var rest = trimmed[(slashIndex + 1)..];
        return LooksLikeSlug(prefix)
            ? new ResponsesModelRoute(prefix, rest)
            : new ResponsesModelRoute(null, trimmed);
    }

    private static bool LooksLikeSlug(string value)
    {
        if (value.Length is < 2 or > 64) return false;
        if (!char.IsAsciiLetterLower(value[0])) return false;
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                return false;
        }
        return true;
    }
}

public readonly record struct ResponsesModelRoute(string? RouteSlug, string Model);
