namespace Aevatar.AI.Abstractions.Agents;

/// <summary>
/// Cross-entry role configuration input.
/// Used by both workflow roles and standalone role yaml.
/// </summary>
public sealed class RoleConfigurationInput
{
    // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
    //   Old pattern: ChatRuntime.ChatStreamAsync 用 Task.Run + Channel<LLMStreamChunk>/ChannelWriter 在 actor turn 外跑 LLM/tool/hook/history 业务循环,违反 actor execution integrity
    //   New principle: reflector force-pick: 删 Task.Run + Channel/ChannelWriter + _streamBufferCapacity 整个 owned stream 框架;ChatStreamAsync 自己 own stream flow;删 stream_buffer_capacity proto/YAML/config(reserve proto field number);middleware bridge 必须 private/internal adapter-only,不暴露公开 stream middleware 接口
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? SystemPrompt { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }

    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public int? MaxToolRounds { get; init; }
    public int? MaxHistoryMessages { get; init; }

    public string? EventModules { get; init; }
    public string? EventRoutes { get; init; }
    public IReadOnlyList<string>? Connectors { get; init; }
}

/// <summary>
/// Normalized role configuration.
/// </summary>
public sealed class RoleConfigurationNormalized
{
    // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
    //   Old pattern: ChatRuntime.ChatStreamAsync 用 Task.Run + Channel<LLMStreamChunk>/ChannelWriter 在 actor turn 外跑 LLM/tool/hook/history 业务循环,违反 actor execution integrity
    //   New principle: reflector force-pick: 删 Task.Run + Channel/ChannelWriter + _streamBufferCapacity 整个 owned stream 框架;ChatStreamAsync 自己 own stream flow;删 stream_buffer_capacity proto/YAML/config(reserve proto field number);middleware bridge 必须 private/internal adapter-only,不暴露公开 stream middleware 接口
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string SystemPrompt { get; init; } = "";
    public string? Provider { get; init; }
    public string? Model { get; init; }

    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public int? MaxToolRounds { get; init; }
    public int? MaxHistoryMessages { get; init; }

    public string? EventModules { get; init; }
    public string? EventRoutes { get; init; }
    public IReadOnlyList<string> Connectors { get; init; } = [];
}

/// <summary>
/// Shared role normalization rules.
/// </summary>
public static class RoleConfigurationNormalizer
{
    public static RoleConfigurationNormalized Normalize(RoleConfigurationInput input)
    {
        var effectiveId = input.Id ?? input.Name ?? string.Empty;
        var effectiveName = input.Name ?? input.Id ?? string.Empty;

        var eventModules = NormalizeText(input.EventModules);
        var eventRoutes = NormalizeText(input.EventRoutes);

        var connectors = input.Connectors?.ToList() ?? [];
        return new RoleConfigurationNormalized
        {
            Id = effectiveId,
            Name = effectiveName,
            SystemPrompt = input.SystemPrompt ?? string.Empty,
            Provider = NormalizeText(input.Provider),
            Model = NormalizeText(input.Model),
            Temperature = input.Temperature,
            MaxTokens = input.MaxTokens,
            MaxToolRounds = input.MaxToolRounds,
            MaxHistoryMessages = input.MaxHistoryMessages,
            EventModules = eventModules,
            EventRoutes = eventRoutes,
            Connectors = connectors,
        };
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim();
    }

}
