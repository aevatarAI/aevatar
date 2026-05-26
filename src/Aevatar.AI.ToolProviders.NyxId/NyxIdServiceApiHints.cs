using System.Text;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Provides concise API cheat-sheets for well-known services,
/// keyed by slug pattern matching. Used to dynamically inject
/// relevant API hints into the system prompt based on the user's
/// actually connected services.
/// </summary>
// Refactor (iter113/cluster-113-telegram-connector-inmemory-updates):
//   Old pattern: Telegram connector keeps inbound updates as in-memory state (process-local queue/dictionary).
//   New principle: Delete telegram_user /getUpdates in-memory queue and route inbound Telegram through existing NyxID relay/proxy; no new actor type; no in-memory state on connector side.
public static class NyxIdServiceApiHints
{
    private static readonly (string Pattern, string Hint)[] HintEntries =
    [
        ("telegram", """
### Telegram Bot
Base URL includes bot token — paths are relative.
POST /getMe {} — Bot info
POST /sendMessage {"chat_id":"...","text":"...","parse_mode":"Markdown"} — Send message
POST /sendPhoto {"chat_id":"...","photo":"https://...","caption":"..."} — Send photo
POST /editMessageText {"chat_id":"...","message_id":N,"text":"..."} — Edit message
POST /deleteMessage {"chat_id":"...","message_id":N} — Delete message
POST /setMyCommands {"commands":[{"command":"start","description":"..."}]} — Set commands
Inbound messages are delivered through NyxID Channel Bot Relay to /api/webhooks/nyxid-relay; do not poll getUpdates from Aevatar connectors.
"""),

        ("github", """
### GitHub API
Base URL: https://api.github.com
GET /user — Current authenticated user
GET /user/repos — List repos
GET /repos/{owner}/{repo} — Repo info
GET /repos/{owner}/{repo}/issues — List issues
POST /repos/{owner}/{repo}/issues {"title":"...","body":"..."} — Create issue
GET /repos/{owner}/{repo}/pulls — List PRs
POST /repos/{owner}/{repo}/pulls {"title":"...","head":"...","base":"..."} — Create PR
GET /repos/{owner}/{repo}/contents/{path} — Get file contents
GET /search/repositories?q=... — Search repos
"""),

        ("openai", """
### OpenAI API
Base URL: https://api.openai.com/v1
POST /chat/completions {"model":"gpt-4o","messages":[{"role":"user","content":"..."}]} — Chat
GET /models — List models
POST /embeddings {"model":"text-embedding-3-small","input":"..."} — Embeddings
POST /images/generations {"model":"dall-e-3","prompt":"...","size":"1024x1024"} — Image gen
"""),

        ("anthropic", """
### Anthropic API
Base URL: https://api.anthropic.com/v1 — requires header: anthropic-version: 2023-06-01
POST /messages {"model":"claude-sonnet-4-20250514","max_tokens":1024,"messages":[{"role":"user","content":"..."}]} — Chat
"""),

        ("twitter", """
### Twitter / X API
Base URL: https://api.x.com/2 — version in base URL, do NOT add /2/ to paths
POST /tweets {"text":"..."} — Post tweet
DELETE /tweets/{id} — Delete tweet
GET /users/me — Current user
GET /tweets/search/recent?query=... — Search tweets
"""),

        ("slack", """
### Slack API
Base URL: https://slack.com/api
POST /chat.postMessage {"channel":"...","text":"..."} — Send message
GET /conversations.list — List channels
GET /conversations.history?channel=... — Channel history
GET /users.list — List users
"""),

        ("discord", """
### Discord API
Base URL: https://discord.com/api/v10
GET /users/@me — Current user
GET /users/@me/guilds — List servers
POST /channels/{id}/messages {"content":"..."} — Send message
GET /channels/{id}/messages — Get messages
"""),

        ("gmail", """
### Gmail API
Base URL: https://gmail.googleapis.com
GET /gmail/v1/users/me/messages — List messages
GET /gmail/v1/users/me/messages/{id} — Get message
"""),

        ("google-calendar", """
### Google Calendar API
Base URL: https://www.googleapis.com/calendar/v3
GET /calendars/primary/events — List events
POST /calendars/primary/events {...} — Create event
"""),

        ("sandbox", """
### Chrono Sandbox (Code Execution)
POST /run {"language":"python","code":"print('hello')"} — Execute code
Supported languages: python, javascript, typescript, bash
Response: {"stdout":"...","stderr":"...","exit_code":0}
Use the code_execute tool for a simpler interface.
"""),
    ];

    /// <summary>
    /// Returns the API hint for a service slug, or null if no hint matches.
    /// Matching is case-insensitive substring on the slug.
    /// </summary>
    public static string? GetHint(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        foreach (var (pattern, hint) in HintEntries)
        {
            if (slug.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return hint;
        }

        return null;
    }

    /// <summary>
    /// Builds a combined API hints section for all matched slugs.
    /// Returns empty string if no hints match.
    /// </summary>
    public static string BuildHintsSection(IEnumerable<string> slugs)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var slug in slugs)
        {
            var hint = GetHint(slug);
            if (hint is null) continue;

            // Deduplicate hints (e.g., multiple telegram bots)
            var key = hint.Split('\n', 2)[0].Trim();
            if (!seen.Add(key)) continue;

            sb.Append(hint.TrimEnd());
            sb.AppendLine();
        }

        if (sb.Length == 0)
            return string.Empty;

        return "<api-hints>\n" + sb.ToString().TrimEnd() + "\n</api-hints>";
    }

    /// <summary>
    /// Builds a combined API hints section using spec-derived hints where available,
    /// falling back to hardcoded hints for services without specs.
    /// </summary>
    public static async Task<string> BuildHintsSectionAsync(
        IEnumerable<ServiceHintRequest> services,
        IConnectedServiceSpecSource specSource,
        string accessToken,
        CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-025-nyxid-tool-discovery-actor-cache):
        //   Old pattern: NyxIdSpecCatalog + SpecFetchToken + IServiceDiscoveryCache 在仓库内建第二 catalog(NyxID 真实源的影子)
        //   New principle: NyxID 是唯一真实源;删除 in-process catalog 假权威面; routing 和 spec hints 请求时读取 live NyxID surface;保留 typed tools + live nyxid_proxy
        var sb = new StringBuilder();
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var svc in services)
        {
            if (!seenSlugs.Add(svc.Slug)) continue;

            string? hint = null;

            if (specSource is not null && !string.IsNullOrWhiteSpace(accessToken))
            {
                var operations = await specSource.GetOrFetchAsync(
                    svc.Slug,
                    svc.ServiceId,
                    svc.OpenApiUrl,
                    accessToken,
                    ct);
                if (operations is { Length: > 0 })
                    hint = BuildHintFromOperations(svc.DisplayName ?? svc.Slug, operations);
            }

            hint ??= GetHint(svc.Slug);

            if (hint is null) continue;

            // Deduplicate by title (e.g., multiple telegram bots share the same hardcoded hint)
            var title = hint.Split('\n', 2)[0].Trim();
            if (!seenTitles.Add(title)) continue;

            sb.Append(hint.TrimEnd());
            sb.AppendLine();
        }

        if (sb.Length == 0)
            return string.Empty;

        return "<api-hints>\n" + sb.ToString().TrimEnd() + "\n</api-hints>";
    }

    internal static string BuildHintFromOperations(string serviceName, OperationCard[] operations, int maxEndpoints = 15)
    {
        if (operations.Length == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"### {serviceName} API (from spec, {operations.Length} endpoints)");

        var shown = operations.Take(maxEndpoints);
        foreach (var op in shown)
        {
            sb.Append($"{op.Method} {op.Path}");
            if (!string.IsNullOrWhiteSpace(op.Summary))
                sb.Append($" — {op.Summary}");
            sb.AppendLine();
        }

        if (operations.Length > maxEndpoints)
            sb.AppendLine($"... and {operations.Length - maxEndpoints} more. Use nyxid_proxy with the service slug for uncovered endpoints.");

        return sb.ToString();
    }
}

public sealed record ServiceHintRequest(
    string Slug,
    string? ServiceId = null,
    string? DisplayName = null,
    string? OpenApiUrl = null);
