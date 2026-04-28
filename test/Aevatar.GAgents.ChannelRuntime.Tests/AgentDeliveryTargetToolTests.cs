using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;
using Aevatar.AI.ToolProviders.AgentCatalog;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentDeliveryTargetToolTests
{
    [Fact]
    public void Name_Is_agent_delivery_targets()
    {
        var tool = new AgentDeliveryTargetTool(new ServiceCollection().BuildServiceProvider());
        tool.Name.Should().Be("agent_delivery_targets");
    }

    [Fact]
    public void ParametersSchema_Is_Valid_Json()
    {
        var tool = new AgentDeliveryTargetTool(new ServiceCollection().BuildServiceProvider());
        var act = () => JsonDocument.Parse(tool.ParametersSchema);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task ExecuteAsync_Returns_Error_When_No_Auth_Token()
    {
        var tool = new AgentDeliveryTargetTool(new ServiceCollection().BuildServiceProvider());
        var result = await tool.ExecuteAsync("""{"action":"list"}""");

        result.Should().Contain("error");
        result.Should().Contain("access token");
    }

    [Fact]
    public async Task ExecuteAsync_Returns_Error_When_Dependencies_Missing()
    {
        var tool = new AgentDeliveryTargetTool(new ServiceCollection().BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"list"}""");
            result.Should().Contain("error");
            result.Should().Contain("not registered");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_List_DoesNotSurfaceCredentials()
    {
        // Issue #466 §D: the public DTO `UserAgentCatalogEntry` no longer carries the
        // NyxApiKey at all (not even masked). Credentials live behind the internal
        // `IUserAgentDeliveryTargetReader` and are not surfaced through any LLM tool.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogEntry>>(
                [
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-1",
                    ConversationId = "oc_chat_1",
                    NyxProviderSlug = "api-lark-bot",
                    OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
                },
            ]));

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));

        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(callerScopeResolver);
        services.AddSingleton(nyxClient);
        var tool = new AgentDeliveryTargetTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"list"}""");

            // Public DTO must not surface any credential field at all (no masked hint either).
            result.Should().NotContain("nyx_api_key");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("total").GetInt32().Should().Be(1);
            var item = doc.RootElement.GetProperty("delivery_targets")[0];
            item.GetProperty("delivery_target_id").GetString().Should().Be("agent-1");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Upsert_Requires_AgentId()
    {
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));

        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IUserAgentCatalogQueryPort>());
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentDeliveryTargetTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"upsert"}""");
            result.Should().Contain("agent_id");
            result.Should().Contain("required");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Upsert_Forwards_Command_To_Port_And_Resolves_Current_User()
    {
        // Issue #466 §D: upsert is rebind-only — must reject when no existing entry exists.
        // Stub the queryPort to return a pre-existing entry so the rebind succeeds.
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "agent-1",
                ConversationId = "oc_chat_existing",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        commandPort.UpsertAsync(Arg.Any<UserAgentCatalogUpsertCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserAgentCatalogUpsertResult(CatalogCommandOutcome.Observed)));

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(callerScopeResolver);
        services.AddSingleton(nyxClient);
        var tool = new AgentDeliveryTargetTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "upsert",
                  "agent_id": "agent-1",
                  "conversation_id": "oc_chat_1",
                  "nyx_provider_slug": "api-lark-bot"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("upserted");

#pragma warning disable CS0612 // legacy fields kept on the command for rollback safety
            await commandPort.Received(1).UpsertAsync(
                Arg.Is<UserAgentCatalogUpsertCommand>(c =>
                    c.AgentId == "agent-1" &&
                    c.ConversationId == "oc_chat_1" &&
                    c.NyxProviderSlug == "api-lark-bot" &&
                    // Tool no longer accepts NyxApiKey as an argument; the credential
                    // is preserved through the actor's MergeNonEmpty upsert policy.
                    c.NyxApiKey == string.Empty &&
                    c.OwnerNyxUserId == "user-1"),
                Arg.Any<CancellationToken>());
#pragma warning restore CS0612
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_Requires_Confirm()
    {
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-2", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "agent-2",
                ConversationId = "oc_chat_2",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton(callerScopeResolver);
        services.AddSingleton(nyxClient);
        var tool = new AgentDeliveryTargetTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete","agent_id":"agent-2"}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("confirm_required");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_Rejects_NonOwner()
    {
        // Issue #466 acceptance: caller-scoped existence check collapses non-owned ids
        // to "not found" (no existence disclosure). The query port's GetForCallerAsync
        // returns null when the caller does not own the requested id.
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-2", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(null));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(callerScopeResolver);
        services.AddSingleton(nyxClient);
        var tool = new AgentDeliveryTargetTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete","agent_id":"agent-2","confirm":true}""");

            result.Should().Contain("not found");
            await commandPort.DidNotReceive().TombstoneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_Forwards_Tombstone_To_Port()
    {
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-3", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "agent-3",
                ConversationId = "oc_chat_3",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        commandPort.TombstoneAsync("agent-3", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.Observed)));

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(callerScopeResolver);
        services.AddSingleton(nyxClient);
        var tool = new AgentDeliveryTargetTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete","agent_id":"agent-3","confirm":true}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("deleted");

            await commandPort.Received(1).TombstoneAsync("agent-3", Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_ReturnsDeleted_WhenCommandPortReportsObserved()
    {
        // Regression guard for #278 review: under the tombstone-retention contract
        // DeleteAsync removes the document outright, so a successful tombstone must
        // surface as "deleted" once the command port reports `Observed`. The polling
        // loop now lives in UserAgentCatalogCommandPort; the tool just maps outcome
        // to status text.
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-7", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "agent-7",
                ConversationId = "oc_chat_7",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        commandPort.TombstoneAsync("agent-7", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.Observed)));

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(callerScopeResolver);
        services.AddSingleton(nyxClient);
        var tool = new AgentDeliveryTargetTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete","agent_id":"agent-7","confirm":true}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("deleted");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ToolSource_Always_Returns_Tool()
    {
        var source = new AgentDeliveryTargetToolSource(new ServiceCollection().BuildServiceProvider());
        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be("agent_delivery_targets");
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
