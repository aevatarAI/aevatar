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
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRegistryEntry>>(
            [
                new AgentRegistryEntry
                {
                    AgentId = "agent-1",
                    Platform = "lark",
                    ConversationId = "oc_chat_1",
                    NyxProviderSlug = "api-lark-bot",
                    NyxApiKey = "secret-1234",
                    OwnerNyxUserId = "user-1",
                },
                new AgentRegistryEntry
                {
                    AgentId = "agent-2",
                    Platform = "lark",
                    ConversationId = "oc_chat_2",
                    NyxProviderSlug = "api-lark-bot",
                    NyxApiKey = "secret-9999",
                    OwnerNyxUserId = "user-2",
                },
            ]));

        var actorRuntime = Substitute.For<IActorRuntime>();
        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
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
        services.AddSingleton(Substitute.For<IUserAgentCatalogQueryPort>());
        services.AddSingleton(Substitute.For<IActorRuntime>());
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
    public async Task ExecuteAsync_Upsert_Dispatches_Command_And_Resolves_Current_User()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetStateVersionAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(3));
        queryPort.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = "agent-1",
                Platform = "lark",
                ConversationId = "oc_chat_1",
                NyxProviderSlug = "api-lark-bot",
                NyxApiKey = "api-key-1234",
                OwnerNyxUserId = "user-1",
            }));

        var actor = Substitute.For<IActor>();
        actor.Id.Returns(UserAgentCatalogGAgent.WellKnownId);
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(actor));

        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
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

            await actor.Received(1).HandleEventAsync(Arg.Is<EventEnvelope>(e =>
                e.Route != null &&
                e.Route.Direct != null &&
                e.Route.Direct.TargetActorId == UserAgentCatalogGAgent.WellKnownId &&
                e.Payload != null &&
                e.Payload.Is(AgentRegistryUpsertCommand.Descriptor) &&
                e.Payload.Unpack<AgentRegistryUpsertCommand>().AgentId == "agent-1" &&
                e.Payload.Unpack<AgentRegistryUpsertCommand>().ConversationId == "oc_chat_1" &&
                e.Payload.Unpack<AgentRegistryUpsertCommand>().NyxProviderSlug == "api-lark-bot" &&
                e.Payload.Unpack<AgentRegistryUpsertCommand>().NyxApiKey == "api-key-1234" &&
                e.Payload.Unpack<AgentRegistryUpsertCommand>().OwnerNyxUserId == "user-1"));
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_Requires_Confirm()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("agent-2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
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
        services.AddSingleton(Substitute.For<IActorRuntime>());
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
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("agent-2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = "agent-2",
                Platform = "lark",
                ConversationId = "oc_chat_2",
                NyxProviderSlug = "api-lark-bot",
                OwnerNyxUserId = "user-2",
            }));

        var actorRuntime = Substitute.For<IActorRuntime>();
        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
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
            await actorRuntime.DidNotReceive().GetAsync(UserAgentCatalogGAgent.WellKnownId);
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_Dispatches_Tombstone_Command()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("agent-3", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
                {
                    AgentId = "agent-3",
                    Platform = "lark",
                    ConversationId = "oc_chat_3",
                    NyxProviderSlug = "api-lark-bot",
                    OwnerNyxUserId = "user-1",
                }),
                Task.FromResult<AgentRegistryEntry?>(null));
        queryPort.GetStateVersionAsync("agent-3", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(6), Task.FromResult<long?>(7));

        var actor = Substitute.For<IActor>();
        actor.Id.Returns(UserAgentCatalogGAgent.WellKnownId);
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(actor));

        var httpClient = new HttpClient(new StaticJsonHandler("""{"user":{"id":"user-1"}}"""))
        {
            BaseAddress = new Uri("https://nyx.example.com"),
        };
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);
        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
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

            await actor.Received(1).HandleEventAsync(Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(AgentRegistryTombstoneCommand.Descriptor) &&
                e.Payload.Unpack<AgentRegistryTombstoneCommand>().AgentId == "agent-3"));
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
