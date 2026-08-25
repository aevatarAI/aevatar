using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentBuilderToolTests
{
    [Fact]
    public void ParametersSchema_Remains_ManagementOnly()
    {
        var tool = CreateTool();

        using var document = JsonDocument.Parse(tool.ParametersSchema);
        var actions = document.RootElement
            .GetProperty("properties")
            .GetProperty("action")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();

        actions.Should().BeEquivalentTo(
            "list_agents",
            "agent_status",
            "run_agent",
            "share_agent",
            "unshare_agent",
            "disable_agent",
            "enable_agent",
            "delete_agent");
        actions.Should().NotContain("create_agent");
        tool.Description.Should().Contain("scheduled_agent_creator");
    }

    [Fact]
    public async Task ExecuteAsync_RunAgent_ShouldDispatchScheduledWorkflowRunNow()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var scheduledDispatch = Substitute.For<IScheduledDispatchApplicationService>();
        var tool = CreateTool(queryPort: queryPort, scheduledDispatch: scheduledDispatch);
        queryPort
            .GetTriggerableForCallerAsync("scheduled-workflow-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(ScheduledWorkflowEntry("scheduled-workflow-1")));

        var result = await WithToolContext(() => tool.ExecuteAsync("""
            {
              "action": "run_agent",
              "agent_id": "scheduled-workflow-1"
            }
            """));

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        await scheduledDispatch.Received(1).RunNowAsync("scheduled-workflow-1", ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DisableAndEnableAgent_ShouldDispatchScheduledWorkflowLifecycle()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var scheduledDispatch = Substitute.For<IScheduledDispatchApplicationService>();
        var tool = CreateTool(queryPort: queryPort, scheduledDispatch: scheduledDispatch);
        queryPort
            .GetForCallerAsync("scheduled-workflow-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(ScheduledWorkflowEntry("scheduled-workflow-1")));

        await WithToolContext(() => tool.ExecuteAsync("""
            {
              "action": "disable_agent",
              "agent_id": "scheduled-workflow-1"
            }
            """));
        await WithToolContext(() => tool.ExecuteAsync("""
            {
              "action": "enable_agent",
              "agent_id": "scheduled-workflow-1"
            }
            """));

        await scheduledDispatch.Received(1).DisableAsync("scheduled-workflow-1", "disable_agent", ct: Arg.Any<CancellationToken>());
        await scheduledDispatch.Received(1).EnableAsync("scheduled-workflow-1", "enable_agent", ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAgent_ShouldDeleteSchedule_AndTombstoneCatalog()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var scheduledDispatch = Substitute.For<IScheduledDispatchApplicationService>();
        var tool = CreateTool(queryPort: queryPort, catalogCommandPort: catalogCommandPort, scheduledDispatch: scheduledDispatch);
        queryPort
            .GetForCallerAsync("scheduled-workflow-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(ScheduledWorkflowEntry("scheduled-workflow-1", "key-1")));
        queryPort
            .QueryVisibleByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>([]));

        var result = await WithToolContext(() => tool.ExecuteAsync("""
            {
              "action": "delete_agent",
              "agent_id": "scheduled-workflow-1",
              "confirm": true
            }
            """));

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        await scheduledDispatch.Received(1).DeleteAsync("scheduled-workflow-1", "delete_agent", ct: Arg.Any<CancellationToken>());
        await catalogCommandPort.Received(1).RetryCredentialRevocationsAsync(Arg.Any<OwnerScope>(), "session-token", Arg.Any<CancellationToken>());
        await catalogCommandPort.Received(1).TombstoneAsync("scheduled-workflow-1", Arg.Any<CancellationToken>(), "session-token");
    }

    [Fact]
    public async Task ExecuteAsync_RunAgent_ForRetiredRunnerCatalogRow_ShouldReject()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var scheduledDispatch = Substitute.For<IScheduledDispatchApplicationService>();
        var tool = CreateTool(queryPort: queryPort, scheduledDispatch: scheduledDispatch);
        queryPort
            .GetTriggerableForCallerAsync("legacy-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "legacy-runner-1",
                AgentType = "skill_runner",
                OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
            }));

        var result = await WithToolContext(() => tool.ExecuteAsync("""
            {
              "action": "run_agent",
              "agent_id": "legacy-runner-1"
            }
            """));

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString().Should().Contain("does not support run_agent");
        await scheduledDispatch.DidNotReceive().RunNowAsync(Arg.Any<string>(), ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentBuilderToolSource_ShouldDiscoverManagementAndCreatorTools()
    {
        var source = new AgentBuilderToolSource(
            Substitute.For<IUserAgentCatalogQueryPort>(),
            Substitute.For<IScheduledDispatchApplicationService>(),
            Substitute.For<IScheduledWorkflowAgentCreationPort>(),
            Substitute.For<IUserAgentCatalogCommandPort>(),
            Substitute.For<ICallerScopeResolver>(),
            new ScheduledAgentCreateRequestMapper(),
            Substitute.For<IScheduledAgentCredentialLifecycle>(),
            Substitute.For<IScheduledInvocationAuthorizationPlanner>(),
            Substitute.For<IScheduledInvocationAuthorizationRevalidator>());

        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo("agent_builder", "scheduled_agent_creator");
    }

    private static AgentBuilderTool CreateTool(
        IUserAgentCatalogQueryPort? queryPort = null,
        IScheduledDispatchApplicationService? scheduledDispatch = null,
        IUserAgentCatalogCommandPort? catalogCommandPort = null,
        ICallerScopeResolver? callerScopeResolver = null) =>
        new(
            queryPort ?? Substitute.For<IUserAgentCatalogQueryPort>(),
            scheduledDispatch ?? Substitute.For<IScheduledDispatchApplicationService>(),
            catalogCommandPort ?? Substitute.For<IUserAgentCatalogCommandPort>(),
            callerScopeResolver ?? ResolvedCallerScope());

    private static ICallerScopeResolver ResolvedCallerScope()
    {
        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.RequireAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OwnerScope.ForNyxIdNative("user-1")));
        return resolver;
    }

    private static UserAgentCatalogReadModelEntry ScheduledWorkflowEntry(string agentId, string apiKeyId = "") =>
        new()
        {
            AgentId = agentId,
            AgentType = ScheduledWorkflowAgentDefaults.AgentType,
            TemplateName = "workflow",
            ScopeId = "scope-1",
            ApiKeyId = apiKeyId,
            OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
        };

    private static async Task<string> WithToolContext(Func<Task<string>> action)
    {
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            return await action();
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }
}
