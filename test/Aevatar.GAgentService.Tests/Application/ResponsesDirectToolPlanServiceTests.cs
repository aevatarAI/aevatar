using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ResponsesDirectToolPlanServiceTests
{
    [Fact]
    public async Task ToolSetProvider_WhenSourceDiscoveryFails_ShouldContinueWithOtherSources()
    {
        var service = new ResponsesDirectToolPlanService(new StaticToolSetRegistry(
            [
                new FaultingToolSource(),
                new StaticToolSource([new StaticTool("use_skill")]),
            ]));

        var plan = service.Build(new ChatRouteAction
        {
            ForwardToModel = new ForwardToModel
            {
                ToolSetRef = new ChatRouteToolSetRef { Name = "workspace.default" },
            },
        });

        plan.Error.Should().BeNull();
        var provider = plan.AdditionalToolProviders.Should().ContainSingle().Subject;
        var tools = await provider.GetAdditiveToolsAsync(
            new ResponsesToolProviderContext(AgentToolExecutionContext.Empty));

        tools.Select(static tool => tool.Name).Should().ContainSingle("use_skill");
    }

    [Fact]
    public async Task ToolSetProvider_WhenCallerCancels_ShouldPropagateCancellation()
    {
        var service = new ResponsesDirectToolPlanService(new StaticToolSetRegistry(
            [new FaultingToolSource()]));
        var plan = service.Build(new ChatRouteAction
        {
            ForwardToModel = new ForwardToModel
            {
                ToolSetRef = new ChatRouteToolSetRef { Name = "workspace.default" },
            },
        });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => plan.AdditionalToolProviders.Single()
            .GetAdditiveToolsAsync(
                new ResponsesToolProviderContext(AgentToolExecutionContext.Empty),
                cts.Token)
            .AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Build_WhenToolSetResolves_ShouldExposeResolvedToolSetNameForRunReResolution()
    {
        var service = new ResponsesDirectToolPlanService(new StaticToolSetRegistry(
            [new StaticToolSource([new StaticTool("nyxid_services")])]));

        var plan = service.Build(new ChatRouteAction
        {
            ForwardToModel = new ForwardToModel
            {
                ToolSetRef = new ChatRouteToolSetRef { Name = "workspace.default" },
            },
        });

        plan.Error.Should().BeNull();
        plan.ResolvedToolSetName.Should().Be("workspace.default");
    }

    [Fact]
    public void Build_WhenNoToolSetRef_ShouldHaveEmptyResolvedToolSetName()
    {
        var service = new ResponsesDirectToolPlanService(new StaticToolSetRegistry([]));

        var plan = service.Build(new ChatRouteAction
        {
            ForwardToModel = new ForwardToModel(),
        });

        plan.AdditionalToolProviders.Should().BeEmpty();
        plan.ResolvedToolSetName.Should().BeEmpty();
    }

    private sealed class StaticToolSetRegistry(IReadOnlyList<IAgentToolSource> sources) : IToolSetRegistry
    {
        public IReadOnlyList<string> GetRegisteredNames() => ["workspace.default"];

        public ToolSetResolveResult Resolve(string? name) =>
            ToolSetResolveResult.Success("workspace.default", sources);
    }

    private sealed class FaultingToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromException<IReadOnlyList<IAgentTool>>(
                new InvalidOperationException("source discovery failed"));
        }
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class StaticTool(string name) : IAgentTool
    {
        public string Name { get; } = name;

        public string Description => "Static test tool";

        public string ParametersSchema => """{"type":"object"}""";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }
}
