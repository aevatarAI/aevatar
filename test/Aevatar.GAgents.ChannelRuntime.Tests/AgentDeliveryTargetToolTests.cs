using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
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
        var tool = CreateTool();
        tool.Name.Should().Be("agent_delivery_targets");
    }

    [Fact]
    public void ParametersSchema_Is_Valid_Json()
    {
        var tool = CreateTool();
        var act = () => JsonDocument.Parse(tool.ParametersSchema);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task ExecuteAsync_Returns_Error_When_No_Auth_Token()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("""{"action":"list"}""");

        result.Should().Contain("error");
        result.Should().Contain("access token");
    }

    [Fact]
    public void Constructor_Requires_Typed_Dependencies()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var resolver = Substitute.For<ICallerScopeResolver>();

        var missingQuery = () => new AgentDeliveryTargetTool(null!, commandPort, resolver);
        var missingCommand = () => new AgentDeliveryTargetTool(queryPort, null!, resolver);
        var missingResolver = () => new AgentDeliveryTargetTool(queryPort, commandPort, null!);

        missingQuery.Should().Throw<ArgumentNullException>().WithParameterName("queryPort");
        missingCommand.Should().Throw<ArgumentNullException>().WithParameterName("commandPort");
        missingResolver.Should().Throw<ArgumentNullException>().WithParameterName("callerScopeResolver");
    }

    [Fact]
    public async Task ExecuteAsync_List_DoesNotSurfaceCredentials()
    {
        // Issue #466 §D: the public DTO `UserAgentCatalogReadModelEntry` no longer carries the
        // NyxApiKey at all (not even masked). Credentials live behind the internal
        // `IUserAgentDeliveryTargetReader` and are not surfaced through any LLM tool.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(
                [
                new UserAgentCatalogReadModelEntry
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

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
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
            AgentToolRequestContext.Current = null;
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
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"upsert"}""");
            result.Should().Contain("agent_id");
            result.Should().Contain("required");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
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
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "agent-1",
                ConversationId = "oc_chat_existing",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Refactor (iter5/cluster-012):
        //   Old pattern: Stub manufactured an upsert result just to satisfy a dead return shape.
        //   New principle: Stub returns Task.CompletedTask; test asserts caller-scoped guard and command dispatch.
        commandPort.UpsertAsync(Arg.Any<UserAgentCatalogUpsertCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
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
                  "action": "upsert",
                  "agent_id": "agent-1",
                  "conversation_id": "oc_chat_1",
                  "nyx_provider_slug": "api-lark-bot"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("note").GetString()
                .Should().Contain("accepted")
                .And.Contain("propagating");

#pragma warning disable CS0612 // assert deprecated ownership fields are no longer emitted
            await commandPort.Received(1).UpsertAsync(
                Arg.Is<UserAgentCatalogUpsertCommand>(c =>
                    c.AgentId == "agent-1" &&
                    c.ConversationId == "oc_chat_1" &&
                    c.NyxProviderSlug == "api-lark-bot" &&
                    // Tool no longer accepts NyxApiKey as an argument; the credential
                    // is preserved through the actor's MergeNonEmpty upsert policy.
                    c.NyxApiKey == string.Empty &&
                    c.OwnerScope != null &&
                    c.OwnerScope.MatchesStrictly(caller) &&
                    c.Platform == string.Empty &&
                    c.OwnerNyxUserId == string.Empty),
                Arg.Any<CancellationToken>());
#pragma warning restore CS0612
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_Requires_Confirm()
    {
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-2", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "agent-2",
                ConversationId = "oc_chat_2",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete","agent_id":"agent-2"}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("confirm_required");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
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
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(null));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete","agent_id":"agent-2","confirm":true}""");

            result.Should().Contain("not found");
            await commandPort.DidNotReceive().TombstoneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_Forwards_Tombstone_To_Port()
    {
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-3", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "agent-3",
                ConversationId = "oc_chat_3",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Refactor (iter5/cluster-012):
        //   Old pattern: Stub manufactured a tombstone result just to satisfy a dead return shape.
        //   New principle: Stub returns Task.CompletedTask; test asserts caller-scoped guard and command dispatch.
        commandPort.TombstoneAsync("agent-3", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete","agent_id":"agent-3","confirm":true}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");

            await commandPort.Received(1).TombstoneAsync("agent-3", Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_ReturnsAccepted_WhenCommandPortAccepts()
    {
        // Refactor (iter4/cluster-009):
        //   Old pattern: Delete surfaced "deleted" when the command port reported Observed.
        //   New principle: Delete returns accepted; callers confirm removal through list/get query paths.
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-7", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "agent-7",
                ConversationId = "oc_chat_7",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Refactor (iter5/cluster-012):
        //   Old pattern: Stub manufactured a tombstone result just to satisfy a dead return shape.
        //   New principle: Stub returns Task.CompletedTask; accepted JSON remains a tool-boundary concern.
        commandPort.TombstoneAsync("agent-7", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete","agent_id":"agent-7","confirm":true}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("note").GetString()
                .Should().Contain("accepted")
                .And.Contain("propagating");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ToolSource_Always_Returns_Tool()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(Array.Empty<UserAgentCatalogReadModelEntry>()));

        var source = new AgentDeliveryTargetToolSource(queryPort, commandPort, callerScopeResolver);
        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be("agent_delivery_targets");

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tools[0].ExecuteAsync("""{"action":"list"}""");
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

    // ─── Patch coverage gap-fillers (issue #466 / codecov/patch) ───

    [Fact]
    public async Task ExecuteAsync_Returns_CallerScopeUnavailable_When_Resolver_Throws()
    {
        // Catches the ICallerScopeResolver.RequireAsync throw path: the tool surfaces
        // a structured `caller_scope_unavailable` error rather than falling through
        // (issue #466 fail-closed acceptance).
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns<Task<OwnerScope?>>(_ => throw new CallerScopeUnavailableException("test resolver failure"));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(resolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"list"}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("error").GetString().Should().Be("caller_scope_unavailable");
            doc.RootElement.GetProperty("detail").GetString().Should().Contain("test resolver failure");
            doc.RootElement.GetProperty("hint").GetString().Should().Contain("Re-authenticate");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public void ToolSource_Constructor_Requires_Typed_Dependencies()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var resolver = Substitute.For<ICallerScopeResolver>();

        var missingQuery = () => new AgentDeliveryTargetToolSource(null!, commandPort, resolver);
        var missingCommand = () => new AgentDeliveryTargetToolSource(queryPort, null!, resolver);
        var missingResolver = () => new AgentDeliveryTargetToolSource(queryPort, commandPort, null!);

        missingQuery.Should().Throw<ArgumentNullException>().WithParameterName("queryPort");
        missingCommand.Should().Throw<ArgumentNullException>().WithParameterName("commandPort");
        missingResolver.Should().Throw<ArgumentNullException>().WithParameterName("callerScopeResolver");
    }

    [Fact]
    public async Task ExecuteAsync_Upsert_Requires_ConversationId()
    {
        var (tool, _, _) = BuildBasicHarness();
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"upsert","agent_id":"agent-1"}""");
            result.Should().Contain("conversation_id");
            result.Should().Contain("required");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Upsert_Requires_NyxProviderSlug()
    {
        var (tool, _, _) = BuildBasicHarness();
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {"action":"upsert","agent_id":"agent-1","conversation_id":"oc_chat_1"}
                """);
            result.Should().Contain("nyx_provider_slug");
            result.Should().Contain("required");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Upsert_RejectsCreateWhenNoExistingEntry()
    {
        // Issue #466 review: upsert is rebind-only. When no existing entry exists for
        // the caller, fail closed with `delivery_target_not_found_for_caller` instead
        // of dispatching a credential-less upsert command.
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-new", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(null));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(resolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "upsert",
                  "agent_id": "agent-new",
                  "conversation_id": "oc_chat_new",
                  "nyx_provider_slug": "api-lark-bot"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("error").GetString().Should().Be("delivery_target_not_found_for_caller");
            doc.RootElement.GetProperty("hint").GetString().Should().Contain("rebind");
            await commandPort.DidNotReceive().UpsertAsync(Arg.Any<UserAgentCatalogUpsertCommand>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Upsert_ReturnsAccepted_WhenCommandPortReportsAccepted()
    {
        // Refactor (iter4/cluster-009):
        //   Old pattern: Test covered the !Observed branch after hidden projection polling timed out.
        //   New principle: Upsert always returns accepted plus a propagating note.
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-pending", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "agent-pending",
                ConversationId = "oc_chat_old",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Refactor (iter5/cluster-012):
        //   Old pattern: Stub manufactured an upsert result just to satisfy a dead return shape.
        //   New principle: Stub returns Task.CompletedTask; accepted JSON remains a tool-boundary concern.
        commandPort.UpsertAsync(Arg.Any<UserAgentCatalogUpsertCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(resolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "upsert",
                  "agent_id": "agent-pending",
                  "conversation_id": "oc_chat_new",
                  "nyx_provider_slug": "api-lark-bot"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("note").GetString()
                .Should().Contain("accepted")
                .And.Contain("propagating");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_RequiresAgentId()
    {
        var (tool, _, _) = BuildBasicHarness();
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete"}""");
            result.Should().Contain("agent_id");
            result.Should().Contain("required");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_ReturnsAccepted_WhenCommandPortReportsAccepted()
    {
        // Refactor (iter4/cluster-009):
        //   Old pattern: Test covered the !Observed branch after hidden projection polling timed out.
        //   New principle: Delete always returns accepted plus a propagating note after the caller-scoped pre-check.
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-slow", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "agent-slow",
                ConversationId = "oc_chat_slow",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Refactor (iter5/cluster-012):
        //   Old pattern: Stub manufactured a tombstone result just to satisfy a dead return shape.
        //   New principle: Stub returns Task.CompletedTask; accepted JSON remains a tool-boundary concern.
        commandPort.TombstoneAsync("agent-slow", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(resolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete","agent_id":"agent-slow","confirm":true}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("note").GetString().Should().Contain("propagating");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    /// <summary>
    /// Minimal harness for tests that only need the early required-field validation
    /// branches (no real query/command response wiring). Returns a tool with a stub
    /// query port, a stub command port, and a deterministic caller-scope resolver.
    /// </summary>
    private static AgentDeliveryTargetTool CreateTool(IServiceCollection? services = null)
    {
        var provider = (services ?? CreateDefaultServices()).BuildServiceProvider();
        return new AgentDeliveryTargetTool(
            provider.GetRequiredService<IUserAgentCatalogQueryPort>(),
            provider.GetService<IUserAgentCatalogCommandPort>() ?? Substitute.For<IUserAgentCatalogCommandPort>(),
            provider.GetRequiredService<ICallerScopeResolver>());
    }

    private static IServiceCollection CreateDefaultServices()
    {
        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));

        return new ServiceCollection()
            .AddSingleton(Substitute.For<IUserAgentCatalogQueryPort>())
            .AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>())
            .AddSingleton(resolver);
    }

    private static (AgentDeliveryTargetTool tool, IUserAgentCatalogQueryPort queryPort, IUserAgentCatalogCommandPort commandPort) BuildBasicHarness()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(resolver);
        return (CreateTool(services), queryPort, commandPort);
    }
}
