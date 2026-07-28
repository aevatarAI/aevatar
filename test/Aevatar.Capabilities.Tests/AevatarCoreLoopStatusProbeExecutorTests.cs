using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.AevatarInvocation;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgents.StatusDashboard;
using Aevatar.Mainnet.Host.Api.Status;
using Aevatar.AGUI.Contracts;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aevatar.Capabilities.Tests;

public sealed class AevatarCoreLoopStatusProbeExecutorTests
{
    [Fact]
    public async Task ProbeAsync_ShouldReportOk_WhenCoreLoopCapabilitiesAreComposed()
    {
        using var provider = BuildProvider(includeInvokeTeamSource: true);
        var executor = provider.GetRequiredService<AevatarCoreLoopStatusProbeExecutor>();

        var outcome = await executor.ProbeAsync(CoreLoopDescriptor(), CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        outcome.Detail.Should().Be("core_loop_tools_6");
    }

    [Fact]
    public async Task ProbeAsync_ShouldReportDown_WhenRequiredToolSourceIsMissing()
    {
        using var provider = BuildProvider(includeInvokeTeamSource: false);
        var executor = provider.GetRequiredService<AevatarCoreLoopStatusProbeExecutor>();

        var outcome = await executor.ProbeAsync(CoreLoopDescriptor(), CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("missing_invocation_tool_sources");
        outcome.ErrorMessage.Should().Contain(nameof(InvokeTeamToolSource));
    }

    [Fact]
    public async Task ProbeAsync_ShouldReportDown_WhenToolDiscoveryFails()
    {
        using var provider = BuildProvider(includeInvokeTeamSource: true, breakInvokeTeamDiscovery: true);
        var executor = provider.GetRequiredService<AevatarCoreLoopStatusProbeExecutor>();

        var outcome = await executor.ProbeAsync(
            CoreLoopDescriptor(requireWorkspaceSources: false),
            CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("tool_discovery_failed");
        outcome.ErrorMessage.Should().Contain(nameof(FailingToolSource));
    }

    [Fact]
    public async Task ProbeAsync_ShouldReportDown_WhenObserveRunToolIsNotReadOnly()
    {
        using var provider = BuildProvider(
            includeInvokeTeamSource: true,
            observeRunReadOnly: false);
        var executor = provider.GetRequiredService<AevatarCoreLoopStatusProbeExecutor>();

        var outcome = await executor.ProbeAsync(
            CoreLoopDescriptor(requireWorkspaceSources: false),
            CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("completion_observe_tool_not_read_only");
        outcome.ErrorMessage.Should().Contain("aevatar_observe_run");
    }

    private static ServiceProvider BuildProvider(
        bool includeInvokeTeamSource,
        bool breakInvokeTeamDiscovery = false,
        bool observeRunReadOnly = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<AevatarCoreLoopStatusProbeExecutor>();
        services.AddSingleton<FailingToolSource>();
        services.AddSingleton(Substitute.For<IActorDispatchPort>());
        services.AddSingleton(Substitute.For<IGAgentActorRegistryQueryPort>());
        services.AddSingleton(Substitute.For<ITeamEntryMemberResolver>());
        services.AddSingleton(Substitute.For<IMemberPublishedServiceResolver>());
        services.AddSingleton(Substitute.For<IStaticGAgentStreamInvocationPort<AGUIEvent>>());
        services.AddSingleton(Substitute.For<
            ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>());
        services.AddSingleton(Substitute.For<IServiceInvocationResolutionPort>());
        services.AddSingleton(Substitute.For<IServiceInvocationDispatcher>());
        services.AddSingleton(Substitute.For<IInvokeAdmissionAuthorizer>());
        services.AddSingleton(Substitute.For<IServiceRunQueryPort>());
        services.AddSingleton(Substitute.For<IGAgentRunTerminalQueryPort>());
        services.AddSingleton(Substitute.For<IWorkflowExecutionQueryApplicationService>());
        services.AddSingleton(Substitute.For<IWorkflowRunBindingReader>());
        services.AddSingleton<AevatarInvocationDispatcher>();
        services.AddSingleton<InvokeGAgentToolSource>();
        if (includeInvokeTeamSource)
        {
            services.AddSingleton<InvokeTeamToolSource>();
        }

        services.AddSingleton<InvokeMemberToolSource>();
        services.AddSingleton<StartWorkflowToolSource>();
        services.AddSingleton<ObserveRunToolSource>();
        services.AddSingleton<ReadWorkflowRunArtifactToolSource>();
        services.AddToolSetRegistry(options =>
        {
            var sources = new List<Func<IServiceProvider, IAgentToolSource>>
            {
                sp => sp.GetRequiredService<InvokeGAgentToolSource>(),
            };
            if (breakInvokeTeamDiscovery)
                sources.Add(sp => sp.GetRequiredService<FailingToolSource>());
            if (includeInvokeTeamSource)
                sources.Add(sp => sp.GetRequiredService<InvokeTeamToolSource>());
            sources.Add(sp => sp.GetRequiredService<InvokeMemberToolSource>());
            sources.Add(sp => sp.GetRequiredService<StartWorkflowToolSource>());
            if (!observeRunReadOnly)
                sources.Add(_ => new FixedToolSource(new FixedInvocationTool("aevatar_observe_run", false)));
            sources.Add(sp => sp.GetRequiredService<ObserveRunToolSource>());
            sources.Add(sp => sp.GetRequiredService<ReadWorkflowRunArtifactToolSource>());
            options.AddToolSet(ToolSetNames.WorkspaceDefault, sources);
        });

        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class FailingToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("discovery unavailable");
    }

    private sealed class FixedToolSource : IAgentToolSource
    {
        private readonly IAgentTool _tool;

        public FixedToolSource(IAgentTool tool)
        {
            _tool = tool;
        }

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAgentTool>>([_tool]);
    }

    private sealed class FixedInvocationTool : IAevatarInvocationTool
    {
        private readonly bool _isReadOnly;

        public FixedInvocationTool(string name, bool isReadOnly)
        {
            Name = name;
            _isReadOnly = isReadOnly;
        }

        public string Name { get; }

        public string Description => "Test invocation tool.";

        public string ParametersSchema => "{}";

        public bool IsReadOnly => _isReadOnly;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private static HealthProbeTargetDescriptor CoreLoopDescriptor(bool requireWorkspaceSources = true) =>
        new()
        {
            Slug = "aevatar-core-loop-tools",
            DisplayName = "Aevatar Core Loop Tools",
            Category = "feature",
            ProbeKind = "aevatar_core_loop",
            IntervalSeconds = 60,
            TimeoutMs = 1_000,
            Enabled = true,
            Parameters =
            {
                ["ToolSet"] = ToolSetNames.WorkspaceDefault,
                ["RequireWorkspaceSources"] = requireWorkspaceSources.ToString(),
            },
        };
}
