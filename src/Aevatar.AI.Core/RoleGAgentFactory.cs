// ─────────────────────────────────────────────────────────────
// RoleGAgentFactory — 角色 Agent 初始化工厂
//
// 从 YAML 初始化 RoleGAgent：
// 1. 初始化字段：名称、SystemPrompt、Provider、Model
// 2. 归一化 typed role config
// 3. 发送 InitializeRoleAgentEvent
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.Agents;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aevatar.AI.Core;

/// <summary>
/// RoleGAgent 初始化工厂。从 YAML 或初始化对象装配 Agent：
/// 初始化字段 → 归一化配置 → 发送 InitializeRoleAgentEvent。
/// </summary>
public static class RoleGAgentFactory
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>从 YAML 字符串初始化 RoleGAgent。</summary>
    public static Task InitializeFromYaml(RoleGAgent agent, string yaml, IServiceProvider services)
    {
        var config = Yaml.Deserialize<RoleYamlConfig>(yaml);
        return ApplyInitialization(agent, config, services);
    }

    /// <summary>应用 RoleYamlConfig 初始化 RoleGAgent。</summary>
    public static async Task ApplyInitialization(RoleGAgent agent, RoleYamlConfig config, IServiceProvider services)
    {
        var normalized = RoleConfigurationNormalizer.Normalize(new RoleConfigurationInput
        {
            Id = config.Name,
            Name = config.Name,
            SystemPrompt = config.SystemPrompt,
            Provider = config.Provider,
            Model = config.Model,
            Temperature = config.Temperature,
            MaxTokens = config.MaxTokens,
            MaxToolRounds = config.MaxToolRounds,
            MaxHistoryMessages = config.MaxHistoryMessages,
            StreamBufferCapacity = config.StreamBufferCapacity,
        });

        // ─── 基础配置（事件优先） ───
        var initializeEvent = new InitializeRoleAgentEvent
        {
            RoleName = normalized.Name,
            SystemPrompt = normalized.SystemPrompt,
            ProviderName = normalized.Provider ?? string.Empty,
            Model = normalized.Model ?? string.Empty,
            MaxTokens = normalized.MaxTokens ?? 0,
            MaxToolRounds = normalized.MaxToolRounds ?? 0,
            MaxHistoryMessages = normalized.MaxHistoryMessages ?? 0,
            StreamBufferCapacity = normalized.StreamBufferCapacity ?? 0,
        };
        if (normalized.Temperature.HasValue)
            initializeEvent.Temperature = normalized.Temperature.Value;

        await agent.HandleInitializeRoleAgent(initializeEvent);
    }
}

/// <summary>RoleGAgent 的 YAML 配置 DTO。</summary>
public sealed class RoleYamlConfig
{
    /// <summary>角色名称。</summary>
    public string? Name { get; set; }

    /// <summary>System Prompt。</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>LLM Provider 名称。</summary>
    public string? Provider { get; set; }

    /// <summary>模型名称。</summary>
    public string? Model { get; set; }

    /// <summary>温度参数。</summary>
    public double? Temperature { get; set; }

    /// <summary>最大输出 tokens。</summary>
    public int? MaxTokens { get; set; }

    /// <summary>最大工具调用轮数。</summary>
    public int? MaxToolRounds { get; set; }

    /// <summary>最大历史消息条数。</summary>
    public int? MaxHistoryMessages { get; set; }

    /// <summary>流式缓冲区容量。</summary>
    public int? StreamBufferCapacity { get; set; }

}
