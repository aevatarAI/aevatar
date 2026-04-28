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
    public async Task ExecuteAsync_List_Masks_NyxApiKey()
    {
        var queryPort = Substitute.For<IUserAgentCatalogRuntimeQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogEntry>>(
                [
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-1",
                    Platform = "lark",
                    ConversationId = "oc_chat_1",
                    NyxProviderSlug = "api-lark-bot",
                    NyxApiKey = "secret-1234",
                    OwnerNyxUserId = "user-1",
                },
                new UserAgentCatalogEntry
                {
                    AgentId = "agent-2",
                    Platform = "lark",
                    ConversationId = "oc_chat_2",
                    NyxProviderSlug = "api-lark-bot",
                    NyxApiKey = "secret-9999",
                    OwnerNyxUserId = "user-2",
                },
            ]));

        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(nyxClient);
        var tool = new AgentDeliveryTargetTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"list"}""");
            result.Should().NotContain("secret-1234");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("total").GetInt32().Should().Be(1);
            var item = doc.RootElement.GetProperty("delivery_targets")[0];
            item.GetProperty("delivery_target_id").GetString().Should().Be("agent-1");
            item.GetProperty("nyx_api_key_hint").GetString().Should().Be("***1234");
            result.Should().NotContain("agent-2");
            result.Should().NotContain("secret-9999");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Upsert_Requires_AgentId()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IUserAgentCatalogRuntimeQueryPort>());
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
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
        var queryPort = Substitute.For<IUserAgentCatalogRuntimeQueryPort>();
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        commandPort.UpsertAsync(Arg.Any<UserAgentCatalogUpsertCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserAgentCatalogUpsertResult(CatalogCommandOutcome.Observed)));

        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
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
                  "nyx_provider_slug": "api-lark-bot",
                  "nyx_api_key": "api-key-1234"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("upserted");
            doc.RootElement.GetProperty("owner_nyx_user_id").GetString().Should().Be("user-1");

            await commandPort.Received(1).UpsertAsync(
                Arg.Is<UserAgentCatalogUpsertCommand>(c =>
                    c.AgentId == "agent-1" &&
                    c.ConversationId == "oc_chat_1" &&
                    c.NyxProviderSlug == "api-lark-bot" &&
                    c.NyxApiKey == "api-key-1234" &&
                    c.OwnerNyxUserId == "user-1"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_Requires_Confirm()
    {
        var queryPort = Substitute.For<IUserAgentCatalogRuntimeQueryPort>();
        queryPort.GetAsync("agent-2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "agent-2",
                Platform = "lark",
                ConversationId = "oc_chat_2",
                NyxProviderSlug = "api-lark-bot",
                OwnerNyxUserId = "user-1",
            }));

        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
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
        var queryPort = Substitute.For<IUserAgentCatalogRuntimeQueryPort>();
        queryPort.GetAsync("agent-2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "agent-2",
                Platform = "lark",
                ConversationId = "oc_chat_2",
                NyxProviderSlug = "api-lark-bot",
                OwnerNyxUserId = "user-2",
            }));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
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
        var queryPort = Substitute.For<IUserAgentCatalogRuntimeQueryPort>();
        queryPort.GetAsync("agent-3", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "agent-3",
                Platform = "lark",
                ConversationId = "oc_chat_3",
                NyxProviderSlug = "api-lark-bot",
                OwnerNyxUserId = "user-1",
            }));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        commandPort.TombstoneAsync("agent-3", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.Observed)));

        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
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
        var queryPort = Substitute.For<IUserAgentCatalogRuntimeQueryPort>();
        queryPort.GetAsync("agent-7", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "agent-7",
                Platform = "lark",
                ConversationId = "oc_chat_7",
                NyxProviderSlug = "api-lark-bot",
                OwnerNyxUserId = "user-1",
            }));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        commandPort.TombstoneAsync("agent-7", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.Observed)));

        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
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
