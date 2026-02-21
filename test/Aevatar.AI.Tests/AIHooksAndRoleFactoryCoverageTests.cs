using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Hooks.BuiltIn;
using Aevatar.AI.Core.Routing;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Hooks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public class AIHooksAndRoleFactoryCoverageTests
{
    [Fact]
    public async Task BuiltInHooks_ShouldCoverPositiveAndNegativeBranches()
    {
        var budget = new BudgetMonitorHook(NullLogger.Instance)
        {
            WarningThreshold = 2,
        };
        var toolTruncation = new ToolTruncationHook { MaxOutputLength = 5 };
        var trace = new ExecutionTraceHook(NullLogger.Instance);

        var ctx = new AIGAgentExecutionHookContext
        {
            AgentId = "agent-1",
            HandlerName = "handler-1",
            ToolName = "tool-a",
        };

        // Budget false branch: missing/invalid history_count.
        await budget.OnLLMRequestStartAsync(ctx, CancellationToken.None);
        ctx.Metadata["history_count"] = "invalid";
        await budget.OnLLMRequestStartAsync(ctx, CancellationToken.None);

        // Budget true branch: numeric history_count over threshold.
        ctx.Metadata["history_count"] = 3;
        await budget.OnLLMRequestStartAsync(ctx, CancellationToken.None);

        // Tool truncation: null/short branch.
        ctx.ToolResult = null;
        await toolTruncation.OnToolExecuteEndAsync(ctx, CancellationToken.None);
        ctx.ToolResult = "abcd";
        await toolTruncation.OnToolExecuteEndAsync(ctx, CancellationToken.None);
        ctx.ToolResult.Should().Be("abcd");

        // Tool truncation: long branch.
        ctx.ToolResult = "abcdefghijk";
        await toolTruncation.OnToolExecuteEndAsync(ctx, CancellationToken.None);
        ctx.ToolResult.Should().StartWith("abcde");
        ctx.ToolResult.Should().Contain("[truncated]");

        // Trace hook coverage.
        var hookCtx = new GAgentExecutionHookContext
        {
            AgentId = "agent-1",
            HandlerName = "handler-1",
            Duration = TimeSpan.FromMilliseconds(12),
        };
        await trace.OnEventHandlerStartAsync(hookCtx, CancellationToken.None);
        await trace.OnEventHandlerEndAsync(hookCtx, CancellationToken.None);
        await trace.OnErrorAsync(hookCtx, new InvalidOperationException("boom"), CancellationToken.None);
        await trace.OnLLMRequestStartAsync(ctx, CancellationToken.None);
        await trace.OnLLMRequestEndAsync(ctx, CancellationToken.None);
        await trace.OnToolExecuteStartAsync(ctx, CancellationToken.None);
        await trace.OnToolExecuteEndAsync(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task HookInterfaces_DefaultMethods_ShouldBeCallable()
    {
        IAIGAgentExecutionHook hook = new MinimalHook();
        var aiCtx = new AIGAgentExecutionHookContext { AgentId = "a-1" };
        var foundationCtx = new GAgentExecutionHookContext { AgentId = "a-1" };

        await hook.OnLLMRequestStartAsync(aiCtx, CancellationToken.None);
        await hook.OnLLMRequestEndAsync(aiCtx, CancellationToken.None);
        await hook.OnToolExecuteStartAsync(aiCtx, CancellationToken.None);
        await hook.OnToolExecuteEndAsync(aiCtx, CancellationToken.None);
        await hook.OnSessionStartAsync(aiCtx, CancellationToken.None);
        await hook.OnSessionEndAsync(aiCtx, CancellationToken.None);
        await hook.OnEventHandlerStartAsync(foundationCtx, CancellationToken.None);
        await hook.OnEventHandlerEndAsync(foundationCtx, CancellationToken.None);
        await hook.OnErrorAsync(foundationCtx, new Exception("ignored"), CancellationToken.None);
    }

    [Fact]
    public async Task RoleGAgentFactory_ShouldConfigureFromYamlAndWrapRoutableModules()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILLMProviderFactory, StubLLMProviderFactory>();
        services.AddSingleton<IEventModuleFactory, StubEventModuleFactory>();
        await using var provider = services.BuildServiceProvider();

        var agent = new RoleGAgent { Services = provider };
        var yaml = """
                   name: planner
                   system_prompt: "You are planner"
                   provider: stub
                   model: model-x
                   temperature: 0.2
                   extensions:
                     event_modules: "routable,bypass,missing"
                     event_routes: |
                       event.type == DemoEvent -> routable
                   """;

        await RoleGAgentFactory.ConfigureFromYaml(agent, yaml, provider);

        agent.RoleName.Should().Be("planner");
        var modules = agent.GetModules();
        modules.Should().HaveCount(2);
        modules.Should().ContainSingle(m => m.Name == "bypass" && m is StubBypassModule);
        modules.Should().ContainSingle(m => m.Name == "routable" && m is RoutedEventModule);
    }

    [Fact]
    public async Task RoleGAgentFactory_ShouldSupportDirectConfigWithoutExtensions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILLMProviderFactory, StubLLMProviderFactory>();
        await using var provider = services.BuildServiceProvider();

        var cfg = new RoleYamlConfig
        {
            Name = "worker",
            SystemPrompt = "prompt",
            Provider = "stub",
            Model = "m",
            Temperature = 0.4,
            Extensions = new RoleYamlExtensions
            {
                EventModules = null,
                EventRoutes = "event.type == X -> y",
            },
        };

        var agent = new RoleGAgent { Services = provider };
        await RoleGAgentFactory.ApplyConfig(agent, cfg, provider);

        agent.RoleName.Should().Be("worker");
        agent.GetModules().Should().BeEmpty();
    }

    private sealed class MinimalHook : IAIGAgentExecutionHook
    {
        public string Name => "minimal";
        public int Priority => 0;
    }

    private sealed class StubEventModuleFactory : IEventModuleFactory
    {
        public bool TryCreate(string name, out IEventModule? module)
        {
            module = name switch
            {
                "routable" => new StubRoutableModule(),
                "bypass" => new StubBypassModule(),
                _ => null,
            };
            return module != null;
        }
    }

    private sealed class StubRoutableModule : IEventModule
    {
        public string Name => "routable";
        public int Priority => 0;
        public bool CanHandle(EventEnvelope envelope) => envelope != null;
        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubBypassModule : IEventModule, IRouteBypassModule
    {
        public string Name => "bypass";
        public int Priority => 0;
        public bool CanHandle(EventEnvelope envelope) => envelope != null;
        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubLLMProviderFactory : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => new StubLLMProvider(name);
        public ILLMProvider GetDefault() => new StubLLMProvider("default");
        public IReadOnlyList<string> GetAvailableProviders() => ["stub"];
    }

    private sealed class StubLLMProvider(string name) : ILLMProvider
    {
        public string Name { get; } = name;

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new LLMResponse { Content = "ok" });
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }
}
