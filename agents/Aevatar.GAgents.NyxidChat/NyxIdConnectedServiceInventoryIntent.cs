namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdConnectedServiceInventoryIntent
{
    private static readonly string[] CatalogPhrases =
    [
        "支持哪些服务",
        "支持什么服务",
        "可以连接哪些服务",
        "能连接哪些服务",
        "服务目录",
        "servicecatalog",
        "whatservicesdoesnyxidsupport",
    ];

    private static readonly string[] MutationPhrases =
    [
        "帮我连接",
        "我要连接",
        "怎么连接",
        "如何连接",
        "添加服务",
        "新增服务",
        "删除服务",
        "移除服务",
        "更新服务",
        "修改服务",
        "授权服务",
        "connectaservice",
        "addaservice",
        "removeaservice",
        "deleteaservice",
    ];

    private static readonly string[] SelfScopePhrases =
    [
        "我在",
        "我的",
        "我有",
        "我已",
        "我能用",
        "我可用",
        "给我",
        "forme",
        "myconnected",
        "ihaveconnected",
        "didiconnect",
    ];

    private static readonly string[] ListPhrases =
    [
        "哪些",
        "有什么",
        "服务列表",
        "列出",
        "list",
        "show",
        "whatservices",
        "whichservices",
    ];

    public static bool Matches(string? text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0 ||
            !normalized.Contains("nyxid", StringComparison.Ordinal) ||
            !(normalized.Contains("服务", StringComparison.Ordinal) ||
              normalized.Contains("service", StringComparison.Ordinal)))
        {
            return false;
        }

        if (CatalogPhrases.Any(normalized.Contains) || MutationPhrases.Any(normalized.Contains))
            return false;

        return SelfScopePhrases.Any(normalized.Contains) &&
               ListPhrases.Any(normalized.Contains);
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return string.Concat(text
            .Trim()
            .ToLowerInvariant()
            .Where(static character =>
                !char.IsWhiteSpace(character) &&
                !char.IsPunctuation(character) &&
                !char.IsSymbol(character)));
    }
}
