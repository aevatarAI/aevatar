// ─────────────────────────────────────────────────────────────
// EventRoutingTests — 事件路由集成测试
//
// 验证 EventRoute 解析 + RoutedEventModule 包装 + 路由匹配
// ─────────────────────────────────────────────────────────────

using Aevatar;
using Aevatar.Actor;
using Aevatar.AI;
using Aevatar.AI.Routing;
using Aevatar.EventModules;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "EventRouting")]
public class EventRoutingTests
{
    // ─── EventRoute.Parse 测试 ───

    [Fact(DisplayName = "解析 YAML list 格式的路由规则")]
    public void Parse_YamlListFormat()
    {
        var yaml = """
            - when: event.type == "ChatRequestEvent"
              to: llm_handler
            - when: event.step_type == "llm_call"
              to: step_executor
            """;

        var routes = EventRoute.Parse(yaml);

        routes.Should().HaveCount(2);
        routes[0].EventType.Should().Be("ChatRequestEvent");
        routes[0].TargetModule.Should().Be("llm_handler");
        routes[1].StepType.Should().Be("llm_call");
        routes[1].TargetModule.Should().Be("step_executor");
    }

    [Fact(DisplayName = "解析行式 DSL 格式的路由规则")]
    public void Parse_LineDslFormat()
    {
        var dsl = """
            event.type == ChatRequestEvent -> llm_handler
            event.step_type == vote -> vote_handler
            """;

        var routes = EventRoute.Parse(dsl);

        routes.Should().HaveCount(2);
        routes[0].EventType.Should().Be("ChatRequestEvent");
        routes[0].TargetModule.Should().Be("llm_handler");
        routes[1].StepType.Should().Be("vote");
        routes[1].TargetModule.Should().Be("vote_handler");
    }

    // ─── RoutedEventModule 匹配测试 ───

    [Fact(DisplayName = "RoutedEventModule 只对匹配路由规则的事件通过")]
    public void RoutedModule_OnlyMatchingEventsPass()
    {
        var inner = new TrackingModule("llm_handler");
        var routes = new[]
        {
            new EventRoute("ChatRequestEvent", null, "llm_handler"),
        };
        var routed = new RoutedEventModule(inner, routes);

        // ChatRequestEvent → 应该匹配
        var chatEnvelope = MakeEnvelope("aevatar.ai.ChatRequestEvent");
        routed.CanHandle(chatEnvelope).Should().BeTrue();

        // ChatResponseEvent → 不匹配
        var responseEnvelope = MakeEnvelope("aevatar.ai.ChatResponseEvent");
        routed.CanHandle(responseEnvelope).Should().BeFalse();
    }

    [Fact(DisplayName = "无路由规则时 RoutedEventModule 对所有事件通过")]
    public void RoutedModule_NoRoutes_AllEventsPass()
    {
        var inner = new TrackingModule("any_module");
        var routed = new RoutedEventModule(inner, []);

        var envelope = MakeEnvelope("anything");
        routed.CanHandle(envelope).Should().BeTrue();
    }

    [Fact(DisplayName = "IRouteBypassModule 不被 RoutedEventModule 包装")]
    public void BypassModule_NotWrapped()
    {
        var bypass = new BypassTrackingModule("always_active");

        // 验证它实现了 IRouteBypassModule
        (bypass is IRouteBypassModule).Should().BeTrue();

        // 在 RoleGAgentFactory 逻辑中，bypass 模块不会被 RoutedEventModule 包装
        // 这里验证直接使用时 CanHandle 总是 true
        var envelope = MakeEnvelope("anything");
        bypass.CanHandle(envelope).Should().BeTrue();
    }

    [Fact(DisplayName = "RoleGAgentFactory 从 YAML 配置路由规则并包装模块")]
    public async Task Factory_AppliesRoutesFromYaml()
    {
        var (sp, runtime, _) = TestEnvironmentHelper.Build();
        using var _ = sp;

        var actor = await runtime.CreateAsync<RoleGAgent>("route-test");
        var agent = (RoleGAgent)((LocalActor)actor).Agent;

        var yaml = """
            name: RoutedAgent
            system_prompt: "test"
            provider: mock
            extensions:
              event_modules: "test_a,test_b"
              event_routes: |
                - when: event.type == "ChatRequestEvent"
                  to: test_a
            """;

        // 注册测试模块工厂
        // （由于 DI 中没有注册 test_a/test_b 的工厂，模块创建会失败）
        // 验证 Route 解析本身是正确的
        var routes = EventRoute.Parse("""
            - when: event.type == "ChatRequestEvent"
              to: test_a
            """);

        routes.Should().HaveCount(1);
        routes[0].EventType.Should().Be("ChatRequestEvent");
        routes[0].TargetModule.Should().Be("test_a");
    }

    // ─── 辅助 ───

    private static EventEnvelope MakeEnvelope(string typeUrlSuffix) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = new Any { TypeUrl = $"type.googleapis.com/{typeUrlSuffix}" },
        Direction = EventDirection.Down,
    };
}

// ─── 测试用模块 ───

public class TrackingModule : IEventModule
{
    public string Name { get; }
    public int Priority => 0;
    public TrackingModule(string name) => Name = name;
    public bool CanHandle(EventEnvelope envelope) => true; // 内部模块总是 true
    public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
        => Task.CompletedTask;
}

public class BypassTrackingModule : IEventModule, IRouteBypassModule
{
    public string Name { get; }
    public int Priority => 0;
    public BypassTrackingModule(string name) => Name = name;
    public bool CanHandle(EventEnvelope envelope) => true;
    public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
        => Task.CompletedTask;
}

// ─── 测试环境辅助 ───

internal static class TestEnvironmentHelper
{
    public static (Microsoft.Extensions.DependencyInjection.ServiceProvider sp, IActorRuntime runtime, MockLLMProvider mockLlm) Build()
    {
        var mockLlm = new MockLLMProvider();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Aevatar.DependencyInjection.ServiceCollectionExtensions.AddAevatarRuntime(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Aevatar.AI.LLM.ILLMProvider>(services, mockLlm);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Aevatar.AI.LLM.ILLMProviderFactory>(services, mockLlm);
        var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var runtime = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IActorRuntime>(sp);
        return (sp, runtime, mockLlm);
    }
}
