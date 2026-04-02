namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// 工具执行期间的请求上下文。通过 AsyncLocal 将请求元数据（如 bearer token）传递给工具。
/// 在 ToolCallLoop 执行期间设置，工具执行完毕后清除。
/// </summary>
public static class AgentToolRequestContext
{
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> s_metadata = new();

    /// <summary>当前请求的元数据。包含 NyxID access token 等信息。</summary>
    public static IReadOnlyDictionary<string, string>? CurrentMetadata
    {
        get => s_metadata.Value;
        set => s_metadata.Value = value;
    }

    /// <summary>尝试从当前上下文获取指定键的值。</summary>
    public static string? TryGet(string key)
    {
        var metadata = s_metadata.Value;
        if (metadata != null && metadata.TryGetValue(key, out var value))
            return value;
        return null;
    }
}
