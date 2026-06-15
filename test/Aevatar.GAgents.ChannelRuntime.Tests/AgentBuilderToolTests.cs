using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentBuilderToolTests
{
    [Fact]
    public async Task ExecuteAsync_DeleteAgent_DisablesActor_RevokesApiKey_AndTombstonesRegistry()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "summary",
                    ApiKeyId = "key-1",
                    OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
                }),
                Task.FromResult<UserAgentCatalogReadModelEntry?>(null));
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(Array.Empty<UserAgentCatalogReadModelEntry>()));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Refactor (iter5/cluster-012):
        //   Old pattern: Stub manufactured a tombstone result just to satisfy a dead return shape.
        //   New principle: Stub returns Task.CompletedTask; test asserts dispatch happened at the tool boundary.
        catalogCommandPort.TombstoneAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-1", """{"ok":true}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory(nyxClient));
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "delete_agent",
                  "agent_id": "skill-runner-1",
                  "confirm": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("revoked_api_key_id").GetString().Should().Be("key-1");
            doc.RootElement.GetProperty("agents").GetArrayLength().Should().Be(0);
            doc.RootElement.GetProperty("delete_notice").GetString().Should().Contain("Delete submitted");
            doc.RootElement.GetProperty("note").GetString()
                .Should().Contain("propagating")
                .And.Contain("/agents");

            await skillRunnerPort.Received(1).DisableAsync(
                "skill-runner-1",
                "delete_agent",
                Arg.Any<CancellationToken>());

            await catalogCommandPort.Received(1).TombstoneAsync(
                "skill-runner-1",
                Arg.Any<CancellationToken>());

            handler.Requests.Should().ContainSingle(x =>
                x.Method == HttpMethod.Delete &&
                x.Path == "/api/v1/api-keys/key-1");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAgent_ReturnsAcceptedWithPropagatingHint()
    {
        // Refactor (iter4/cluster-009):
        //   Old pattern: Delete relied on command-port polling to decide whether to claim immediate deletion.
        //   New principle: Delete returns accepted and points confirmation to the explicit /agents query path.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-stuck", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-stuck",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                ApiKeyId = "key-stuck",
                OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
            }));
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(
                [new UserAgentCatalogReadModelEntry { AgentId = "skill-runner-stuck", OwnerScope = OwnerScope.ForNyxIdNative("user-1") }]));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Refactor (iter5/cluster-012):
        //   Old pattern: Stub manufactured a tombstone result just to satisfy a dead return shape.
        //   New principle: Stub returns Task.CompletedTask; test asserts accepted copy plus source/query guard behavior.
        catalogCommandPort.TombstoneAsync("skill-runner-stuck", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-stuck", """{"ok":true}""");
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory(nyxClient));
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "delete_agent",
                  "agent_id": "skill-runner-stuck",
                  "confirm": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("revoked_api_key_id").GetString().Should().Be("key-stuck");
            doc.RootElement.GetProperty("delete_notice").GetString()
                .Should().Contain("Delete submitted for");
            // The new copy must point users at /agents to verify rather than
            // implying the tombstone did not land.
            doc.RootElement.GetProperty("note").GetString()
                .Should().Contain("propagating")
                .And.Contain("/agents");

            await catalogCommandPort.Received(1).TombstoneAsync(
                "skill-runner-stuck",
                Arg.Any<CancellationToken>());

            await queryPort.DidNotReceive().GetStateVersionForCallerAsync(
                Arg.Any<string>(),
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunAgent_DispatchesManualTrigger()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
            }));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory());
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "run_agent",
                  "agent_id": "skill-runner-1"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("agent_id").GetString().Should().Be("skill-runner-1");
            doc.RootElement.GetProperty("note").GetString()
                .Should().Contain("accepted for dispatch")
                .And.Contain("/agent-status");

            await skillRunnerPort.Received(1).TriggerAsync(
                "skill-runner-1",
                "run_agent",
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_AgentStatus_JoinsPerIdCatalogAndExecutionAtToolBoundary()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-join", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-join",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                Status = string.Empty,
                ErrorCount = 0,
                CatalogAuthorityStateVersion = 7,
                CatalogLastEventId = "catalog-7",
            }));

        var executionQueryPort = Substitute.For<ISkillRunnerExecutionQueryPort>();
        executionQueryPort.GetAsync("skill-runner-join", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SkillRunnerExecutionDocument?>(new SkillRunnerExecutionDocument
            {
                Id = "skill-runner-join",
                StateVersion = 3,
                LastEventId = "runner-3",
                Status = SkillRunnerDefaults.StatusError,
                ErrorCount = 2,
                LastError = "tool failed",
            }));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(executionQueryPort);
        services.AddSingleton(Substitute.For<ISkillRunnerCommandPort>());
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory());
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "agent_status",
                  "agent_id": "skill-runner-join"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("agent_id").GetString().Should().Be("skill-runner-join");
            doc.RootElement.GetProperty("status").GetString().Should().Be(SkillRunnerDefaults.StatusError);
            doc.RootElement.GetProperty("error_count").GetInt32().Should().Be(2);
            doc.RootElement.GetProperty("last_error").GetString().Should().Be("tool failed");

            await queryPort.Received(1).GetForCallerAsync(
                "skill-runner-join",
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
            await executionQueryPort.Received(1).GetAsync(
                "skill-runner-join",
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ListAgents_JoinsCatalogAndExecutionAtToolBoundary()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(
            [
                new UserAgentCatalogReadModelEntry
                {
                    AgentId = "skill-runner-list",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "summary",
                },
            ]));

        var executionQueryPort = Substitute.For<ISkillRunnerExecutionQueryPort>();
        executionQueryPort.QueryByAgentIdsAsync(
                Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("skill-runner-list")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, SkillRunnerExecutionDocument>>(
                new Dictionary<string, SkillRunnerExecutionDocument>(StringComparer.Ordinal)
                {
                    ["skill-runner-list"] = new()
                    {
                        Id = "skill-runner-list",
                        StateVersion = 4,
                        LastEventId = "runner-4",
                        Status = SkillRunnerDefaults.StatusRunning,
                        ErrorCount = 1,
                    },
                }));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(executionQueryPort);
        services.AddSingleton(Substitute.For<ISkillRunnerCommandPort>());
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory());
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"list_agents"}""");

            using var doc = JsonDocument.Parse(result);
            var agent = doc.RootElement.GetProperty("agents").EnumerateArray().Should().ContainSingle().Subject;
            agent.GetProperty("agent_id").GetString().Should().Be("skill-runner-list");
            agent.GetProperty("status").GetString().Should().Be(SkillRunnerDefaults.StatusRunning);
            agent.GetProperty("error_count").GetInt32().Should().Be(1);

            await queryPort.Received(1).QueryByCallerAsync(
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
            await executionQueryPort.Received(1).QueryByAgentIdsAsync(
                Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("skill-runner-list")),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunAgent_DispatchesEvenWhenPresentationStatusIsDisabled()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                Status = SkillRunnerDefaults.StatusDisabled,
            }));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory());
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "run_agent",
                  "agent_id": "skill-runner-1"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("note").GetString()
                .Should().Contain("accepted for dispatch")
                .And.Contain("/agent-status");

            await skillRunnerPort.Received(1).TriggerAsync(
                "skill-runner-1",
                "run_agent",
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DisableAgent_DispatchesDisableAndReturnsAcceptedWithoutPolling()
    {
        // Refactor (iter1/cluster-002):
        //   Old pattern: Captured readmodel version, dispatched lifecycle, then delayed-looped for projected status.
        //   New principle: Lifecycle commands return accepted; freshness is observed by follow-up query or push event.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-fast", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-fast",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                Status = SkillRunnerDefaults.StatusRunning,
            }));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory());
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "disable_agent",
                  "agent_id": "skill-runner-fast"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be(SkillRunnerDefaults.StatusRunning);
            var note = doc.RootElement.GetProperty("note").GetString();
            note.Should().Contain("Disable accepted")
                .And.Contain("propagating")
                .And.Contain("/agent-status")
                .And.NotContain("Scheduling paused");

            await skillRunnerPort.Received(1).DisableAsync(
                "skill-runner-fast",
                "disable_agent",
                Arg.Any<CancellationToken>());
            await queryPort.DidNotReceive().GetStateVersionForCallerAsync(
                Arg.Any<string>(),
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DisableAgent_DispatchesEvenWhenPresentationStatusIsDisabled()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                Status = SkillRunnerDefaults.StatusDisabled,
                ScheduleCron = "0 9 * * *",
                ScheduleTimezone = "UTC",
            }));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory());
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "disable_agent",
                  "agent_id": "skill-runner-1"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be(SkillRunnerDefaults.StatusDisabled);
            doc.RootElement.GetProperty("note").GetString()
                .Should().Contain("Disable accepted")
                .And.Contain("/agent-status");

            await skillRunnerPort.Received(1).DisableAsync(
                "skill-runner-1",
                "disable_agent",
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_EnableAgent_DispatchesEnableAndReturnsAcceptedWithoutPolling()
    {
        // Refactor (iter1/cluster-002):
        //   Old pattern: Captured readmodel version, dispatched lifecycle, then delayed-looped for projected status.
        //   New principle: Lifecycle commands return accepted; freshness is observed by follow-up query or push event.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                Status = SkillRunnerDefaults.StatusDisabled,
                ScheduleCron = "0 9 * * *",
                ScheduleTimezone = "UTC",
            }));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory());
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "enable_agent",
                  "agent_id": "skill-runner-1"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be(SkillRunnerDefaults.StatusDisabled);
            var note = doc.RootElement.GetProperty("note").GetString();
            note.Should().Contain("Enable accepted")
                .And.Contain("propagating")
                .And.Contain("/agent-status")
                .And.NotContain("Scheduling resumed");

            await skillRunnerPort.Received(1).EnableAsync(
                "skill-runner-1",
                "enable_agent",
                Arg.Any<CancellationToken>());
            await queryPort.DidNotReceive().GetStateVersionForCallerAsync(
                Arg.Any<string>(),
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_LifecycleCommands_DoNotReadExecutionStatusForAdmission()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                Status = string.Empty,
            }));

        var executionQueryPort = Substitute.For<ISkillRunnerExecutionQueryPort>();
        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(executionQueryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory());
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            await tool.ExecuteAsync("""{"action":"run_agent","agent_id":"skill-runner-1"}""");
            await tool.ExecuteAsync("""{"action":"disable_agent","agent_id":"skill-runner-1"}""");
            await tool.ExecuteAsync("""{"action":"enable_agent","agent_id":"skill-runner-1"}""");

            await executionQueryPort.DidNotReceive().GetAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
            await executionQueryPort.DidNotReceive().QueryByAgentIdsAsync(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public void Constructor_Requires_Typed_Dependencies()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var executionQueryPort = Substitute.For<ISkillRunnerExecutionQueryPort>();
        var nyxClientFactory = Substitute.For<INyxIdApiClientFactory>();
        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();

        var missingQuery = () => new AgentBuilderTool(null!, executionQueryPort, nyxClientFactory, skillRunnerPort, catalogCommandPort, callerScopeResolver);
        var missingExecutionQuery = () => new AgentBuilderTool(queryPort, null!, nyxClientFactory, skillRunnerPort, catalogCommandPort, callerScopeResolver);
        var missingNyxFactory = () => new AgentBuilderTool(queryPort, executionQueryPort, null!, skillRunnerPort, catalogCommandPort, callerScopeResolver);
        var missingSkillRunner = () => new AgentBuilderTool(queryPort, executionQueryPort, nyxClientFactory, null!, catalogCommandPort, callerScopeResolver);
        var missingCatalogCommand = () => new AgentBuilderTool(queryPort, executionQueryPort, nyxClientFactory, skillRunnerPort, null!, callerScopeResolver);
        var missingCallerScope = () => new AgentBuilderTool(queryPort, executionQueryPort, nyxClientFactory, skillRunnerPort, catalogCommandPort, null!);
        var missingSourceQuery = () => new AgentBuilderToolSource(null!, executionQueryPort, nyxClientFactory, skillRunnerPort, catalogCommandPort, callerScopeResolver);
        var missingSourceExecutionQuery = () => new AgentBuilderToolSource(queryPort, null!, nyxClientFactory, skillRunnerPort, catalogCommandPort, callerScopeResolver);
        var missingSourceNyxFactory = () => new AgentBuilderToolSource(queryPort, executionQueryPort, null!, skillRunnerPort, catalogCommandPort, callerScopeResolver);
        var missingSourceSkillRunner = () => new AgentBuilderToolSource(queryPort, executionQueryPort, nyxClientFactory, null!, catalogCommandPort, callerScopeResolver);
        var missingSourceCatalogCommand = () => new AgentBuilderToolSource(queryPort, executionQueryPort, nyxClientFactory, skillRunnerPort, null!, callerScopeResolver);
        var missingSourceCallerScope = () => new AgentBuilderToolSource(queryPort, executionQueryPort, nyxClientFactory, skillRunnerPort, catalogCommandPort, null!);

        missingQuery.Should().Throw<ArgumentNullException>().WithParameterName("queryPort");
        missingExecutionQuery.Should().Throw<ArgumentNullException>().WithParameterName("executionQueryPort");
        missingNyxFactory.Should().Throw<ArgumentNullException>().WithParameterName("nyxClientFactory");
        missingSkillRunner.Should().Throw<ArgumentNullException>().WithParameterName("skillRunnerPort");
        missingCatalogCommand.Should().Throw<ArgumentNullException>().WithParameterName("catalogCommandPort");
        missingCallerScope.Should().Throw<ArgumentNullException>().WithParameterName("callerScopeResolver");
        missingSourceQuery.Should().Throw<ArgumentNullException>().WithParameterName("queryPort");
        missingSourceExecutionQuery.Should().Throw<ArgumentNullException>().WithParameterName("executionQueryPort");
        missingSourceNyxFactory.Should().Throw<ArgumentNullException>().WithParameterName("nyxClientFactory");
        missingSourceSkillRunner.Should().Throw<ArgumentNullException>().WithParameterName("skillRunnerPort");
        missingSourceCatalogCommand.Should().Throw<ArgumentNullException>().WithParameterName("catalogCommandPort");
        missingSourceCallerScope.Should().Throw<ArgumentNullException>().WithParameterName("callerScopeResolver");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsStructuredError_WhenCallerScopeUnavailable()
    {
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(null));

        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IUserAgentCatalogQueryPort>());
        services.AddSingleton(Substitute.For<ISkillRunnerExecutionQueryPort>());
        services.AddSingleton(Substitute.For<ISkillRunnerCommandPort>());
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory());
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"list_agents"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("error").GetString().Should().Be("caller_scope_unavailable");
            doc.RootElement.GetProperty("hint").GetString().Should().Contain("Re-authenticate");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ToolSource_Always_ReturnsTool()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler()) { BaseAddress = new Uri("https://nyx.example.com") });
        var nyxClientFactory = new TestNyxIdApiClientFactory(nyxClient);
        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(Array.Empty<UserAgentCatalogReadModelEntry>()));

        var source = new AgentBuilderToolSource(
            queryPort,
            Substitute.For<ISkillRunnerExecutionQueryPort>(),
            nyxClientFactory,
            skillRunnerPort,
            catalogCommandPort,
            callerScopeResolver);
        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be("agent_builder");

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tools[0].ExecuteAsync("""{"action":"list_agents"}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("total").GetInt32().Should().Be(0);

            await queryPort.Received(1).QueryByCallerAsync(
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    private static AgentBuilderTool CreateTool(IServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        return new AgentBuilderTool(
            provider.GetRequiredService<IUserAgentCatalogQueryPort>(),
            provider.GetService<ISkillRunnerExecutionQueryPort>() ?? Substitute.For<ISkillRunnerExecutionQueryPort>(),
            provider.GetRequiredService<INyxIdApiClientFactory>(),
            provider.GetRequiredService<ISkillRunnerCommandPort>(),
            provider.GetRequiredService<IUserAgentCatalogCommandPort>(),
            provider.GetRequiredService<ICallerScopeResolver>(),
            provider.GetService<ILogger<AgentBuilderTool>>());
    }

    private sealed class TestNyxIdApiClientFactory : INyxIdApiClientFactory
    {
        private readonly NyxIdApiClient _client;

        public TestNyxIdApiClientFactory(NyxIdApiClient? client = null)
        {
            _client = client ?? new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(new RoutingJsonHandler())
                {
                    BaseAddress = new Uri("https://nyx.example.com"),
                });
        }

        public NyxIdApiClient CreateClient() => _client;
    }

    private sealed class RoutingJsonHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.OrdinalIgnoreCase);

        public List<RecordedRequest> Requests { get; } = [];

        public void Add(HttpMethod method, string path, string json)
        {
            _responses[BuildKey(method, path)] = json;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, path, body));

            if (_responses.TryGetValue(BuildKey(request.Method, path), out var json))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":true,"message":"not found"}""", Encoding.UTF8, "application/json"),
            };
        }

        private static string BuildKey(HttpMethod method, string path) => $"{method.Method}:{path}";
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Body);

}
