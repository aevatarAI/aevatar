// ─────────────────────────────────────────────────────────────
// RoleGAgentFactory — 角色 Agent 工厂
//
// 从 YAML 配置 RoleGAgent：
// 1. 基础配置：名称、SystemPrompt、Provider、Model
// 2. EventModules：按名字从 IEventModuleFactory 创建
// 3. EventRoutes：解析路由规则，用 RoutedEventModule 包装非 bypass 模块
//
// YAML 示例:
//   extensions:
//     event_modules: "llm_handler,web_search"
//     event_routes: |
//       - when: event.type == "ChatRequestEvent"
//         to: llm_handler
//       - when: event.step_type == "llm_call"
//         to: llm_handler
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Core.Routing;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aevatar.AI.Core;

/// <summary>
/// RoleGAgent 配置工厂。从 YAML 或配置对象装配 Agent：
/// 基础配置 → 创建 EventModules → 解析 EventRoutes → 路由包装 → 注册到 Agent。
/// </summary>
public static class RoleGAgentFactory
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>从 YAML 字符串配置 RoleGAgent。</summary>
    public static void ConfigureFromYaml(RoleGAgent agent, string yaml, IServiceProvider services)
    {
        var config = Yaml.Deserialize<RoleYamlConfig>(yaml);
        ApplyConfig(agent, config, services);
    }

    /// <summary>应用 RoleYamlConfig 到 RoleGAgent。</summary>
    public static void ApplyConfig(RoleGAgent agent, RoleYamlConfig config, IServiceProvider services)
    {
        // ─── 基础配置 ───
        if (!string.IsNullOrEmpty(config.Name))
            agent.SetRoleName(config.Name);

        agent.ConfigureAsync(new AIAgentConfig
        {
            SystemPrompt = config.SystemPrompt ?? "",
            ProviderName = config.Provider ?? "deepseek",
            Model = config.Model,
            Temperature = config.Temperature,
        }).GetAwaiter().GetResult();

        // ─── EventModules 创建 ───
        if (string.IsNullOrEmpty(config.Extensions?.EventModules)) return;

        var factories = services.GetServices<IEventModuleFactory>().ToList();
        var moduleNames = config.Extensions.EventModules
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("RoleGAgentFactory");
        var rawModules = new List<IEventModule>();
        foreach (var name in moduleNames)
        {
            var created = false;
            foreach (var f in factories)
            {
                if (f.TryCreate(name, out var m) && m != null)
                {
                    rawModules.Add(m);
                    created = true;
                    break;
                }
            }
            if (!created)
                logger?.LogWarning("EventModule '{Name}' 未找到对应的 factory", name);
        }

        // ─── EventRoutes 解析 + 路由包装 ───
        var routes = EventRoute.Parse(config.Extensions.EventRoutes, logger);
        var evaluator = services.GetService<IEventRouteEvaluator>()
                        ?? DefaultEventRouteEvaluator.Instance;

        var finalModules = new List<IEventModule>();
        foreach (var module in rawModules)
        {
            if (routes.Length > 0 && module is not IRouteBypassModule)
            {
                // 非 bypass 模块用 RoutedEventModule 包装
                finalModules.Add(new RoutedEventModule(module, routes, evaluator));
            }
            else
            {
                // bypass 模块或无路由规则 → 直接注册
                finalModules.Add(module);
            }
        }

        if (finalModules.Count > 0)
            agent.SetModules(finalModules);
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

    /// <summary>扩展配置。</summary>
    public RoleYamlExtensions? Extensions { get; set; }
}

/// <summary>RoleYamlConfig 扩展配置。</summary>
public sealed class RoleYamlExtensions
{
    /// <summary>逗号分隔的 EventModule 名称列表。</summary>
    public string? EventModules { get; set; }

    /// <summary>
    /// 事件路由规则（YAML list 或行式 DSL）。
    /// 匹配条件：event.type / event.step_type。
    /// 目标：to 指定的模块名。
    /// </summary>
    public string? EventRoutes { get; set; }
}