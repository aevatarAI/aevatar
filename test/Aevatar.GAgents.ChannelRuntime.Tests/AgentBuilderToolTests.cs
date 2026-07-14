using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.Studio.Application.Authorization;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentBuilderToolTests
{
    [Fact]
    public void ParametersSchema_Remains_ManagementOnly()
    {
        var tool = new AgentBuilderTool(
            Substitute.For<IUserAgentCatalogQueryPort>(),
            Substitute.For<ISkillRunnerExecutionQueryPort>(),
            Substitute.For<ISkillRunnerCommandPort>(),
            Substitute.For<IScheduledDispatchApplicationService>(),
            Substitute.For<IUserAgentCatalogCommandPort>(),
            Substitute.For<ICallerScopeResolver>(),
            Substitute.For<IScheduledAgentApiKeyIssuer>());

        using var document = JsonDocument.Parse(tool.ParametersSchema);
        var actions = document.RootElement
            .GetProperty("properties")
            .GetProperty("action")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();

        actions.Should().NotContain("create_agent");
        tool.Description.Should().Contain("scheduled_agent_creator");
        tool.Description.Should().NotContain("Agent creation is not handled here");
    }

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
        queryPort.QueryVisibleByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(Array.Empty<UserAgentCatalogReadModelEntry>()));
        queryPort.QueryPendingApiKeyRevocationsByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentApiKeyRevocationReadModelEntry>>(Array.Empty<UserAgentApiKeyRevocationReadModelEntry>()));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Refactor (iter5/cluster-012):
        //   Old pattern: Stub manufactured a tombstone result just to satisfy a dead return shape.
        //   New principle: Stub returns Task.CompletedTask; test asserts dispatch happened at the tool boundary.
        catalogCommandPort.TombstoneAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        catalogCommandPort.RecordApiKeyRevocationAttemptAsync(
                Arg.Any<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(),
                Arg.Any<CancellationToken>())
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
            doc.RootElement.GetProperty("api_key_revocation_status").GetString().Should().Be("completed");
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
            await catalogCommandPort.Received(1).RecordApiKeyRevocationAttemptAsync(
                Arg.Is<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(command =>
                    command.AgentId == "skill-runner-1" &&
                    command.ApiKeyId == "key-1" &&
                    command.Completed),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAgent_WithNyxIdFailure_RecordsPendingRevocationDetails()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-fail", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-fail",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                ApiKeyId = "key-fail",
                OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
            }));
        queryPort.QueryVisibleByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(Array.Empty<UserAgentCatalogReadModelEntry>()));
        queryPort.QueryPendingApiKeyRevocationsByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentApiKeyRevocationReadModelEntry>>(Array.Empty<UserAgentApiKeyRevocationReadModelEntry>()));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        catalogCommandPort.TombstoneAsync("skill-runner-fail", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        catalogCommandPort.RecordApiKeyRevocationAttemptAsync(
                Arg.Any<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-fail", """{"error":true,"status":503,"body":"upstream unavailable"}""");
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
                  "agent_id": "skill-runner-fail",
                  "confirm": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("api_key_revocation_status").GetString().Should().Be("pending");

            await catalogCommandPort.Received(1).RecordApiKeyRevocationAttemptAsync(
                Arg.Is<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(command =>
                    command.AgentId == "skill-runner-fail" &&
                    command.ApiKeyId == "key-fail" &&
                    !command.Completed &&
                    command.HttpStatus == 503 &&
                    command.Error == "upstream unavailable" &&
                    command.FailureKind == UserAgentApiKeyRevocationFailureKind.Transient),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAgent_RetriesPendingRevocationsForCaller()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-current", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-current",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                ApiKeyId = "key-current",
                OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
            }));
        queryPort.QueryVisibleByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(Array.Empty<UserAgentCatalogReadModelEntry>()));
        queryPort.QueryPendingApiKeyRevocationsByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentApiKeyRevocationReadModelEntry>>(
            [
                new UserAgentApiKeyRevocationReadModelEntry
                {
                    AgentId = "skill-runner-old",
                    ApiKeyId = "key-old",
                    OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
                    AttemptCount = 1,
                    LastHttpStatus = 503,
                    FailureKind = UserAgentApiKeyRevocationFailureKind.Transient,
                },
            ]));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        catalogCommandPort.TombstoneAsync("skill-runner-current", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        catalogCommandPort.RecordApiKeyRevocationAttemptAsync(
                Arg.Any<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-current", """{"ok":true}""");
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-old", """{"error":true,"status":404,"body":"already deleted"}""");
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
                  "agent_id": "skill-runner-current",
                  "confirm": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("api_key_revocation_retry_count").GetInt32().Should().Be(1);

            handler.Requests.Select(static request => request.Path)
                .Should().BeEquivalentTo("/api/v1/api-keys/key-current", "/api/v1/api-keys/key-old");
            await queryPort.Received(1).QueryPendingApiKeyRevocationsByCallerAsync(
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
            await catalogCommandPort.Received(1).RecordApiKeyRevocationAttemptAsync(
                Arg.Is<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(command =>
                    command.AgentId == "skill-runner-old" &&
                    command.ApiKeyId == "key-old" &&
                    command.Completed &&
                    command.HttpStatus == 404 &&
                    command.FailureKind == UserAgentApiKeyRevocationFailureKind.None),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAgent_StillRecordsCurrentRevocation_WhenPendingRevocationProjectionHasSchemaDrift()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-drift-revoke", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-drift-revoke",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                ApiKeyId = "key-current",
                OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
            }));
        queryPort.QueryVisibleByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(Array.Empty<UserAgentCatalogReadModelEntry>()));
        queryPort.QueryPendingApiKeyRevocationsByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<UserAgentApiKeyRevocationReadModelEntry>>(
                CreateRevocationProjectionDrift()));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        catalogCommandPort.TombstoneAsync("skill-runner-drift-revoke", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        catalogCommandPort.RecordApiKeyRevocationAttemptAsync(
                Arg.Any<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-current", """{"ok":true}""");
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
                  "agent_id": "skill-runner-drift-revoke",
                  "confirm": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("api_key_revocation_status").GetString().Should().Be("completed");
            doc.RootElement.GetProperty("api_key_revocation_retry_count").GetInt32().Should().Be(0);
            await catalogCommandPort.Received(1).RecordApiKeyRevocationAttemptAsync(
                Arg.Is<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(command =>
                    command.AgentId == "skill-runner-drift-revoke" &&
                    command.ApiKeyId == "key-current" &&
                    command.Completed),
                Arg.Any<CancellationToken>());
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
        queryPort.QueryVisibleByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(
                [new UserAgentCatalogReadModelEntry { AgentId = "skill-runner-stuck", OwnerScope = OwnerScope.ForNyxIdNative("user-1") }]));
        queryPort.QueryPendingApiKeyRevocationsByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentApiKeyRevocationReadModelEntry>>(Array.Empty<UserAgentApiKeyRevocationReadModelEntry>()));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Refactor (iter5/cluster-012):
        //   Old pattern: Stub manufactured a tombstone result just to satisfy a dead return shape.
        //   New principle: Stub returns Task.CompletedTask; test asserts accepted copy plus source/query guard behavior.
        catalogCommandPort.TombstoneAsync("skill-runner-stuck", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        catalogCommandPort.RecordApiKeyRevocationAttemptAsync(
                Arg.Any<UserAgentCatalogRecordApiKeyRevocationAttemptCommand>(),
                Arg.Any<CancellationToken>())
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
            doc.RootElement.GetProperty("api_key_revocation_status").GetString().Should().Be("completed");
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
    public async Task ExecuteAsync_DeleteAgent_StillReturnsAccepted_WhenExecutionProjectionHasSchemaDrift()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-drift", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-drift",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                ApiKeyId = string.Empty,
                OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
            }));
        queryPort.QueryVisibleByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(
            [
                new UserAgentCatalogReadModelEntry
                {
                    AgentId = "skill-runner-drift",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "summary",
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "Asia/Shanghai",
                    OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
                },
            ]));
        queryPort.QueryPendingApiKeyRevocationsByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentApiKeyRevocationReadModelEntry>>(Array.Empty<UserAgentApiKeyRevocationReadModelEntry>()));

        var executionQueryPort = Substitute.For<ISkillRunnerExecutionQueryPort>();
        executionQueryPort.QueryByAgentIdsAsync(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyDictionary<string, SkillRunnerExecutionDocument>>(
                CreateExecutionProjectionDrift()));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        catalogCommandPort.TombstoneAsync("skill-runner-drift", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(executionQueryPort);
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
                  "action": "delete_agent",
                  "agent_id": "skill-runner-drift",
                  "confirm": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("agents").EnumerateArray()
                .Should().ContainSingle()
                .Subject.GetProperty("agent_id").GetString().Should().Be("skill-runner-drift");

            await skillRunnerPort.Received(1).DisableAsync(
                "skill-runner-drift",
                "delete_agent",
                Arg.Any<CancellationToken>());
            await catalogCommandPort.Received(1).TombstoneAsync(
                "skill-runner-drift",
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
        queryPort.GetTriggerableForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
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
    public async Task ExecuteAsync_RunAgent_FromChannelInbound_DispatchesManualTriggerNotAdmission()
    {
        // Regression (prod 2026-06-11): run_agent used to route channel-context calls through
        // the external-trigger admission protocol. Admission requires a pre-registered
        // ExternalTriggerSource on the runner, and scheduled_agent_creator registers none,
        // so every owner-issued /run-agent ended as a committed-but-silent
        // SkillRunnerExternalTriggerRejectedEvent(unknown_source) while the tool replied
        // "accepted". The owner's management-plane trigger is authorized by the caller-scope
        // check and must dispatch TriggerAsync directly even when channel metadata is present.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetTriggerableForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                ScopeId = "scope-1",
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
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForChannel("nyx-user-1", "lark", "scope-1", "ou-user")));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            ["channel.platform"] = "lark",
            ["registration_scope_id"] = "scope-1",
            ["channel.message_id"] = "activity-1",
            ["channel.platform_message_id"] = "om_1",
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

            await skillRunnerPort.Received(1).TriggerAsync(
                "skill-runner-1",
                "run_agent",
                Arg.Any<CancellationToken>());
            await skillRunnerPort.DidNotReceive().AdmitExternalTriggerAsync(
                Arg.Any<string>(),
                Arg.Any<AdmitSkillRunnerExternalTriggerCommand>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunAgent_UsesTriggerableAccessForSharedAgent()
    {
        var caller = OwnerScope.ForChannel("user-B", "lark", "scope-1", "bob");
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetTriggerableForCallerAsync("skill-runner-shared", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-shared",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                SharingGrant = new ScheduledAgentSharingGrant
                {
                    SharedWithRegistrationScope = "scope-1",
                    AllowTrigger = true,
                },
            }));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(Substitute.For<ISkillRunnerExecutionQueryPort>());
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory());
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"run_agent","agent_id":"skill-runner-shared"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            await queryPort.Received(1).GetTriggerableForCallerAsync(
                "skill-runner-shared",
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
            await queryPort.DidNotReceive().GetForCallerAsync(
                "skill-runner-shared",
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
            await skillRunnerPort.Received(1).TriggerAsync(
                "skill-runner-shared",
                "run_agent",
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShareAgent_IsOwnerOnly_AndDispatchesCatalogShare()
    {
        var owner = OwnerScope.ForChannel("user-A", "lark", "scope-1", "alice");
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                OwnerScope = owner,
            }));

        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(Substitute.For<ISkillRunnerExecutionQueryPort>());
        services.AddSingleton(Substitute.For<ISkillRunnerCommandPort>());
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory());
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(owner));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"share_agent","agent_id":"skill-runner-1","allow_trigger":true}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("shared_with_registration_scope").GetString().Should().Be("scope-1");
            doc.RootElement.GetProperty("allow_trigger").GetBoolean().Should().BeTrue();
            await queryPort.Received(1).GetForCallerAsync(
                "skill-runner-1",
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
            await catalogCommandPort.Received(1).ShareAsync(
                "skill-runner-1",
                Arg.Is<OwnerScope>(scope => scope.MatchesStrictly(owner)),
                true,
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShareAgent_NyxIdNativeCaller_ReturnsChannelScopeErrorWithoutDispatch()
    {
        var caller = OwnerScope.ForNyxIdNative("user-A");
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(Substitute.For<ISkillRunnerExecutionQueryPort>());
        services.AddSingleton(Substitute.For<ISkillRunnerCommandPort>());
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory());
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"share_agent","agent_id":"skill-runner-1","allow_trigger":true}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("error").GetString()
                .Should().Be("share_agent requires a channel registration scope");
            await queryPort.DidNotReceive().GetForCallerAsync(
                Arg.Any<string>(),
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
            await catalogCommandPort.DidNotReceive().ShareAsync(
                Arg.Any<string>(),
                Arg.Any<OwnerScope>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShareAgent_ChannelCallerWithBlankRegistrationScope_ReturnsChannelScopeErrorWithoutDispatch()
    {
        var caller = OwnerScope.ForChannel("user-A", "lark", " ", "alice");
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(Substitute.For<ISkillRunnerExecutionQueryPort>());
        services.AddSingleton(Substitute.For<ISkillRunnerCommandPort>());
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory());
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"share_agent","agent_id":"skill-runner-1","allow_trigger":true}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("error").GetString()
                .Should().Be("share_agent requires a channel registration scope");
            await queryPort.DidNotReceive().GetForCallerAsync(
                Arg.Any<string>(),
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
            await catalogCommandPort.DidNotReceive().ShareAsync(
                Arg.Any<string>(),
                Arg.Any<OwnerScope>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_UnshareAgent_IsOwnerOnly_AndDispatchesCatalogUnshare()
    {
        var owner = OwnerScope.ForChannel("user-A", "lark", "scope-1", "alice");
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                OwnerScope = owner,
            }));

        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(Substitute.For<ISkillRunnerExecutionQueryPort>());
        services.AddSingleton(Substitute.For<ISkillRunnerCommandPort>());
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory());
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(owner));
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"unshare_agent","agent_id":"skill-runner-1"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            await queryPort.Received(1).GetForCallerAsync(
                "skill-runner-1",
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
            await catalogCommandPort.Received(1).UnshareAsync(
                "skill-runner-1",
                Arg.Is<OwnerScope>(scope => scope.MatchesStrictly(owner)),
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
        queryPort.GetVisibleForCallerAsync("skill-runner-join", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "skill-runner-join",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "summary",
                OutputFormat = SkillRunnerOutputFormat.FeishuDoc,
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
                ScheduleMode = SkillRunnerScheduleMode.OneShot,
                RunAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 6, 11, 10, 30, 0, TimeSpan.Zero)),
                RetiredAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 6, 11, 10, 31, 0, TimeSpan.Zero)),
                RetirementReason = SkillRunnerDefaults.OneShotRetirementReasonFailed,
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
            doc.RootElement.GetProperty("output_format").GetString().Should().Be("feishu_doc");
            doc.RootElement.GetProperty("schedule_mode").GetString().Should().Be("one_shot");
            doc.RootElement.GetProperty("run_at_utc").ValueKind.Should().Be(JsonValueKind.Object);
            doc.RootElement.GetProperty("retired_at_utc").ValueKind.Should().Be(JsonValueKind.Object);
            doc.RootElement.GetProperty("retirement_reason").GetString()
                .Should().Be(SkillRunnerDefaults.OneShotRetirementReasonFailed);
            doc.RootElement.GetProperty("error_count").GetInt32().Should().Be(2);
            doc.RootElement.GetProperty("last_error").GetString().Should().Be("tool failed");

            await queryPort.Received(1).GetVisibleForCallerAsync(
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
        queryPort.QueryVisibleByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(
            [
                new UserAgentCatalogReadModelEntry
                {
                    AgentId = "skill-runner-list",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "summary",
                    OutputFormat = SkillRunnerOutputFormat.Text,
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
                        ScheduleMode = SkillRunnerScheduleMode.OneShot,
                        RunAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                            new DateTimeOffset(2026, 6, 11, 10, 30, 0, TimeSpan.Zero)),
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
            agent.GetProperty("output_format").GetString().Should().Be("text");
            agent.GetProperty("schedule_mode").GetString().Should().Be("one_shot");
            agent.GetProperty("run_at_utc").ValueKind.Should().Be(JsonValueKind.Object);
            agent.GetProperty("error_count").GetInt32().Should().Be(1);

            await queryPort.Received(1).QueryVisibleByCallerAsync(
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
    public async Task ExecuteAsync_ListAgents_ReturnsCatalogOnlyRows_WhenExecutionProjectionHasSchemaDrift()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.QueryVisibleByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(
            [
                new UserAgentCatalogReadModelEntry
                {
                    AgentId = "skill-runner-list-drift",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "summary",
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "Asia/Shanghai",
                    OutputFormat = SkillRunnerOutputFormat.FeishuDoc,
                    OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
                },
            ]));

        var executionQueryPort = Substitute.For<ISkillRunnerExecutionQueryPort>();
        executionQueryPort.QueryByAgentIdsAsync(
                Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("skill-runner-list-drift")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyDictionary<string, SkillRunnerExecutionDocument>>(
                CreateExecutionProjectionDrift()));

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
            doc.RootElement.GetProperty("total").GetInt32().Should().Be(1);
            var agent = doc.RootElement.GetProperty("agents").EnumerateArray().Should().ContainSingle().Subject;
            agent.GetProperty("agent_id").GetString().Should().Be("skill-runner-list-drift");
            agent.GetProperty("template").GetString().Should().Be("summary");
            agent.GetProperty("schedule_cron").GetString().Should().Be("0 9 * * *");
            agent.GetProperty("schedule_timezone").GetString().Should().Be("Asia/Shanghai");
            agent.GetProperty("status").GetString().Should().BeEmpty();
            agent.GetProperty("output_format").GetString().Should().Be("feishu_doc");
            agent.GetProperty("next_scheduled_run").ValueKind.Should().Be(JsonValueKind.Null);
            doc.RootElement.TryGetProperty("error", out _).Should().BeFalse();

            await queryPort.Received(1).QueryVisibleByCallerAsync(
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
            await executionQueryPort.Received(1).QueryByAgentIdsAsync(
                Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("skill-runner-list-drift")),
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
        queryPort.GetTriggerableForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
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
    public async Task ExecuteAsync_ScheduledWorkflowLifecycle_RoutesToScheduleService()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var scheduledWorkflowEntry = new UserAgentCatalogReadModelEntry
        {
            AgentId = "scheduled-workflow-1",
            AgentType = ScheduledWorkflowAgentDefaults.AgentType,
            TemplateName = "summary",
            Status = SkillRunnerDefaults.StatusRunning,
            ScheduleCron = "0 9 * * *",
            ScheduleTimezone = "UTC",
        };
        queryPort.GetForCallerAsync("scheduled-workflow-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(scheduledWorkflowEntry));
        queryPort.GetTriggerableForCallerAsync("scheduled-workflow-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(scheduledWorkflowEntry));
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(Array.Empty<UserAgentCatalogReadModelEntry>()));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var scheduledDispatchService = Substitute.For<IScheduledDispatchApplicationService>();
        scheduledDispatchService.RunNowAsync("scheduled-workflow-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ScheduledDispatchRunNowReceipt(
                "scheduled-workflow-1",
                "actor:scheduled-workflow-1",
                DateTimeOffset.UtcNow,
                "idem-1",
                true,
                "command-1",
                "correlation-1",
                DateTimeOffset.UtcNow,
                "accepted")));
        scheduledDispatchService.DisableAsync("scheduled-workflow-1", "disable_agent", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BuildScheduleMutationReceipt("scheduled-workflow-1")));
        scheduledDispatchService.EnableAsync("scheduled-workflow-1", "enable_agent", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BuildScheduleMutationReceipt("scheduled-workflow-1")));
        scheduledDispatchService.DeleteAsync("scheduled-workflow-1", "delete_agent", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BuildScheduleMutationReceipt("scheduled-workflow-1")));
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(scheduledDispatchService);
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
            await tool.ExecuteAsync("""{"action":"run_agent","agent_id":"scheduled-workflow-1"}""");
            await tool.ExecuteAsync("""{"action":"disable_agent","agent_id":"scheduled-workflow-1"}""");
            await tool.ExecuteAsync("""{"action":"enable_agent","agent_id":"scheduled-workflow-1"}""");
            var deleteResult = await tool.ExecuteAsync("""{"action":"delete_agent","agent_id":"scheduled-workflow-1","confirm":true}""");

            using var document = JsonDocument.Parse(deleteResult);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            await scheduledDispatchService.Received(1).RunNowAsync("scheduled-workflow-1", Arg.Any<CancellationToken>());
            await scheduledDispatchService.Received(1).DisableAsync("scheduled-workflow-1", "disable_agent", Arg.Any<CancellationToken>());
            await scheduledDispatchService.Received(1).EnableAsync("scheduled-workflow-1", "enable_agent", Arg.Any<CancellationToken>());
            await scheduledDispatchService.Received(1).DeleteAsync("scheduled-workflow-1", "delete_agent", Arg.Any<CancellationToken>());
            await catalogCommandPort.Received(1).TombstoneAsync("scheduled-workflow-1", Arg.Any<CancellationToken>());
            await skillRunnerPort.DidNotReceive().TriggerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await skillRunnerPort.DidNotReceive().DisableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await skillRunnerPort.DidNotReceive().EnableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        queryPort.GetTriggerableForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
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
        var scheduledDispatchService = Substitute.For<IScheduledDispatchApplicationService>();
        var scheduledWorkflowAgentCreationPort = Substitute.For<IScheduledWorkflowAgentCreationPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        var scheduledAgentMapper = new ScheduledAgentCreateRequestMapper(new InMemorySecretVault());
        var scheduledAgentApiKeyIssuer = new ScheduledAgentApiKeyIssuer(
            nyxClientFactory,
            new ScheduledAgentCreatorOptions());

        var missingQuery = () => new AgentBuilderTool(null!, executionQueryPort, skillRunnerPort, scheduledDispatchService, catalogCommandPort, callerScopeResolver, scheduledAgentApiKeyIssuer);
        var missingExecutionQuery = () => new AgentBuilderTool(queryPort, null!, skillRunnerPort, scheduledDispatchService, catalogCommandPort, callerScopeResolver, scheduledAgentApiKeyIssuer);
        var missingSkillRunner = () => new AgentBuilderTool(queryPort, executionQueryPort, null!, scheduledDispatchService, catalogCommandPort, callerScopeResolver, scheduledAgentApiKeyIssuer);
        var missingScheduledDispatch = () => new AgentBuilderTool(queryPort, executionQueryPort, skillRunnerPort, null!, catalogCommandPort, callerScopeResolver, scheduledAgentApiKeyIssuer);
        var missingCatalogCommand = () => new AgentBuilderTool(queryPort, executionQueryPort, skillRunnerPort, scheduledDispatchService, null!, callerScopeResolver, scheduledAgentApiKeyIssuer);
        var missingCallerScope = () => new AgentBuilderTool(queryPort, executionQueryPort, skillRunnerPort, scheduledDispatchService, catalogCommandPort, null!, scheduledAgentApiKeyIssuer);
        var missingIssuer = () => new AgentBuilderTool(queryPort, executionQueryPort, skillRunnerPort, scheduledDispatchService, catalogCommandPort, callerScopeResolver, null!);
        var missingSourceQuery = () => new AgentBuilderToolSource(null!, executionQueryPort, skillRunnerPort, scheduledDispatchService, scheduledWorkflowAgentCreationPort, catalogCommandPort, callerScopeResolver, scheduledAgentMapper, scheduledAgentApiKeyIssuer);
        var missingSourceExecutionQuery = () => new AgentBuilderToolSource(queryPort, null!, skillRunnerPort, scheduledDispatchService, scheduledWorkflowAgentCreationPort, catalogCommandPort, callerScopeResolver, scheduledAgentMapper, scheduledAgentApiKeyIssuer);
        var missingSourceSkillRunner = () => new AgentBuilderToolSource(queryPort, executionQueryPort, null!, scheduledDispatchService, scheduledWorkflowAgentCreationPort, catalogCommandPort, callerScopeResolver, scheduledAgentMapper, scheduledAgentApiKeyIssuer);
        var missingSourceScheduledDispatch = () => new AgentBuilderToolSource(queryPort, executionQueryPort, skillRunnerPort, null!, scheduledWorkflowAgentCreationPort, catalogCommandPort, callerScopeResolver, scheduledAgentMapper, scheduledAgentApiKeyIssuer);
        var missingSourceCreationPort = () => new AgentBuilderToolSource(queryPort, executionQueryPort, skillRunnerPort, scheduledDispatchService, null!, catalogCommandPort, callerScopeResolver, scheduledAgentMapper, scheduledAgentApiKeyIssuer);
        var missingSourceCatalogCommand = () => new AgentBuilderToolSource(queryPort, executionQueryPort, skillRunnerPort, scheduledDispatchService, scheduledWorkflowAgentCreationPort, null!, callerScopeResolver, scheduledAgentMapper, scheduledAgentApiKeyIssuer);
        var missingSourceCallerScope = () => new AgentBuilderToolSource(queryPort, executionQueryPort, skillRunnerPort, scheduledDispatchService, scheduledWorkflowAgentCreationPort, catalogCommandPort, null!, scheduledAgentMapper, scheduledAgentApiKeyIssuer);
        var missingSourceMapper = () => new AgentBuilderToolSource(queryPort, executionQueryPort, skillRunnerPort, scheduledDispatchService, scheduledWorkflowAgentCreationPort, catalogCommandPort, callerScopeResolver, null!, scheduledAgentApiKeyIssuer);
        var missingSourceIssuer = () => new AgentBuilderToolSource(queryPort, executionQueryPort, skillRunnerPort, scheduledDispatchService, scheduledWorkflowAgentCreationPort, catalogCommandPort, callerScopeResolver, scheduledAgentMapper, null!);

        missingQuery.Should().Throw<ArgumentNullException>().WithParameterName("queryPort");
        missingExecutionQuery.Should().Throw<ArgumentNullException>().WithParameterName("executionQueryPort");
        missingSkillRunner.Should().Throw<ArgumentNullException>().WithParameterName("skillRunnerPort");
        missingScheduledDispatch.Should().Throw<ArgumentNullException>().WithParameterName("scheduledDispatchService");
        missingCatalogCommand.Should().Throw<ArgumentNullException>().WithParameterName("catalogCommandPort");
        missingCallerScope.Should().Throw<ArgumentNullException>().WithParameterName("callerScopeResolver");
        missingIssuer.Should().Throw<ArgumentNullException>().WithParameterName("scheduledAgentApiKeyIssuer");
        missingSourceQuery.Should().Throw<ArgumentNullException>().WithParameterName("queryPort");
        missingSourceExecutionQuery.Should().Throw<ArgumentNullException>().WithParameterName("executionQueryPort");
        missingSourceSkillRunner.Should().Throw<ArgumentNullException>().WithParameterName("skillRunnerPort");
        missingSourceScheduledDispatch.Should().Throw<ArgumentNullException>().WithParameterName("scheduledDispatchService");
        missingSourceCreationPort.Should().Throw<ArgumentNullException>().WithParameterName("scheduledWorkflowAgentCreationPort");
        missingSourceCatalogCommand.Should().Throw<ArgumentNullException>().WithParameterName("catalogCommandPort");
        missingSourceCallerScope.Should().Throw<ArgumentNullException>().WithParameterName("callerScopeResolver");
        missingSourceMapper.Should().Throw<ArgumentNullException>().WithParameterName("scheduledAgentMapper");
        missingSourceIssuer.Should().Throw<ArgumentNullException>().WithParameterName("scheduledAgentApiKeyIssuer");
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
        var scheduledDispatchService = Substitute.For<IScheduledDispatchApplicationService>();
        var scheduledWorkflowAgentCreationPort = Substitute.For<IScheduledWorkflowAgentCreationPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        queryPort.QueryVisibleByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(Array.Empty<UserAgentCatalogReadModelEntry>()));

        var source = new AgentBuilderToolSource(
            queryPort,
            Substitute.For<ISkillRunnerExecutionQueryPort>(),
            skillRunnerPort,
            scheduledDispatchService,
            scheduledWorkflowAgentCreationPort,
            catalogCommandPort,
            callerScopeResolver,
            new ScheduledAgentCreateRequestMapper(new InMemorySecretVault()),
            new ScheduledAgentApiKeyIssuer(nyxClientFactory, new ScheduledAgentCreatorOptions()));
        var tools = await source.DiscoverToolsAsync();

        tools.Select(tool => tool.Name).Should().BeEquivalentTo("agent_builder", "scheduled_agent_creator");
        var managementTool = tools.Single(tool => tool.Name == "agent_builder");
        var creatorTool = tools.Single(tool => tool.Name == "scheduled_agent_creator");
        creatorTool.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
        creatorTool.IsReadOnly.Should().BeFalse();
        creatorTool.IsDestructive.Should().BeFalse();

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await managementTool.ExecuteAsync("""{"action":"list_agents"}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("total").GetInt32().Should().Be(0);

            await queryPort.Received(1).QueryVisibleByCallerAsync(
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task AddLarkAgentAuthoring_WhenCalledTwice_ShouldResolveSingleToolSource_AndDiscoverRegisteredTools()
    {
        var handler = new RoutingJsonHandler();
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var executionQueryPort = Substitute.For<ISkillRunnerExecutionQueryPort>();
        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var scheduledDispatchService = Substitute.For<IScheduledDispatchApplicationService>();
        var scheduledWorkflowAgentCreationPort = Substitute.For<IScheduledWorkflowAgentCreationPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(executionQueryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(scheduledDispatchService);
        services.AddSingleton(scheduledWorkflowAgentCreationPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory(nyxClient));
        services.AddSingleton(nyxClient);
        services.AddSingleton<ISecretVault>(new InMemorySecretVault());
        services.AddSingleton(Substitute.For<IUserAgentDeliveryTargetReader>());
        var existingNotificationPort = Substitute.For<IChannelInteractionNotificationPort>();
        services.AddSingleton(existingNotificationPort);
        services.AddSingleton<LarkMessageComposer>();
        services.AddSingleton(callerScopeResolver);
        services.AddSingleton(Substitute.For<IScheduledInvocationAuthorizationPlanner>());

        services.AddLarkAgentAuthoring();
        services.AddLarkAgentAuthoring();

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IAgentToolSource>().Should().ContainSingle().Subject;
        source.Should().BeOfType<AgentBuilderToolSource>();
        provider.GetService<IHumanInteractionPort>().Should().BeNull();
        provider.GetRequiredService<IChannelInteractionNotificationPort>().Should().BeSameAs(existingNotificationPort);

        var tools = await source.DiscoverToolsAsync();

        tools.Select(tool => tool.Name).Should().BeEquivalentTo("agent_builder", "scheduled_agent_creator");
        tools.Single(tool => tool.Name == "scheduled_agent_creator").ApprovalMode
            .Should().Be(ToolApprovalMode.NeverRequire);
    }

    private static ScheduledDispatchMutationReceipt BuildScheduleMutationReceipt(string scheduleId) =>
        new(
            scheduleId,
            $"actor:{scheduleId}",
            true,
            "command-1",
            "correlation-1",
            DateTimeOffset.UtcNow,
            "accepted");

    private static AgentBuilderTool CreateTool(IServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        return new AgentBuilderTool(
            provider.GetRequiredService<IUserAgentCatalogQueryPort>(),
            provider.GetService<ISkillRunnerExecutionQueryPort>() ?? Substitute.For<ISkillRunnerExecutionQueryPort>(),
            provider.GetRequiredService<ISkillRunnerCommandPort>(),
            provider.GetService<IScheduledDispatchApplicationService>() ?? Substitute.For<IScheduledDispatchApplicationService>(),
            provider.GetRequiredService<IUserAgentCatalogCommandPort>(),
            provider.GetRequiredService<ICallerScopeResolver>(),
            provider.GetService<IScheduledAgentApiKeyIssuer>() ??
            new ScheduledAgentApiKeyIssuer(
                provider.GetRequiredService<INyxIdApiClientFactory>(),
                new ScheduledAgentCreatorOptions()),
            provider.GetService<ILogger<AgentBuilderTool>>());
    }

    private static ProjectionIndexSchemaDriftException CreateExecutionProjectionDrift() =>
        new(
            "Elasticsearch",
            "aevatar-mainnet-skill-runner-execution",
            "aevatar-mainnet-skill-runner-execution-vold",
            "aevatar-mainnet-skill-runner-execution-vnew");

    private static ProjectionIndexSchemaDriftException CreateRevocationProjectionDrift() =>
        new(
            "Elasticsearch",
            "aevatar-mainnet-user-agent-api-key-revocation",
            "aevatar-mainnet-user-agent-api-key-revocation-vold",
            "aevatar-mainnet-user-agent-api-key-revocation-vnew");

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
