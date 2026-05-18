using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Scheduled;
using StudioUserConfig = Aevatar.Studio.Application.Studio.Abstractions.UserConfig;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentBuilderToolTests
{
    [Fact]
    public async Task ExecuteAsync_DeleteAgent_DisablesActor_RevokesApiKey_AndTombstonesRegistry()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily",
                    ApiKeyId = "key-1",
                    OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
                }),
                Task.FromResult<UserAgentCatalogEntry?>(null));
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogEntry>>(Array.Empty<UserAgentCatalogEntry>()));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        catalogCommandPort.TombstoneAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.Observed)));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-1", """{"ok":true}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(nyxClient);
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
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
            doc.RootElement.GetProperty("status").GetString().Should().Be("deleted");
            doc.RootElement.GetProperty("revoked_api_key_id").GetString().Should().Be("key-1");
            doc.RootElement.GetProperty("agents").GetArrayLength().Should().Be(0);
            doc.RootElement.GetProperty("delete_notice").GetString().Should().Contain("Deleted agent");

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
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAgent_ReturnsAcceptedWithPropagatingHint_WhenTombstoneDoesNotReflectWithinBudget()
    {
        // Production bug class: with the old 5 s polling budget, /delete-agent
        // routinely returned "accepted" + "tombstone is not yet reflected" while
        // the document was still visible to /agents minutes later. This guard
        // proves that when the read model legitimately stays behind, the user-
        // facing payload now nudges the user to retry rather than implying the
        // delete might not have landed at all.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-stuck", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-stuck",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily",
                ApiKeyId = "key-stuck",
                OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
            }));
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogEntry>>(
                [new UserAgentCatalogEntry { AgentId = "skill-runner-stuck", OwnerScope = OwnerScope.ForNyxIdNative("user-1") }]));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Tombstone is dispatched but the projection has not yet caught up; the
        // port surfaces an Accepted outcome and the tool reports the propagating
        // notice so the user knows to re-check /agents.
        catalogCommandPort.TombstoneAsync("skill-runner-stuck", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.Accepted)));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-stuck", """{"ok":true}""");
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(nyxClient);
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
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
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunAgent_DispatchesManualTrigger()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily",
            }));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
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

            await skillRunnerPort.Received(1).TriggerAsync(
                "skill-runner-1",
                "run_agent",
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunAgent_RejectsDisabledAgent()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily",
                Status = SkillRunnerDefaults.StatusDisabled,
            }));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "run_agent",
                  "agent_id": "skill-runner-1"
                }
                """);

            result.Should().Contain("is disabled");
            await skillRunnerPort.DidNotReceive().TriggerAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
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
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-fast",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily",
                Status = SkillRunnerDefaults.StatusRunning,
            }));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
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
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DisableAgent_ReturnsAlreadyDisabledWithoutDispatch()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily",
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
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
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
            doc.RootElement.GetProperty("note").GetString().Should().Contain("already disabled");

            await skillRunnerPort.DidNotReceive().DisableAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
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
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily",
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
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
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
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ToolSource_Always_ReturnsTool()
    {
        var source = new AgentBuilderToolSource(new ServiceCollection().BuildServiceProvider());
        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be("agent_builder");
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

    private sealed class StubUserConfigQueryPort : IUserConfigQueryPort
    {
        private readonly StudioUserConfig _config;

        public StubUserConfigQueryPort(StudioUserConfig config)
        {
            _config = config;
        }

        public Task<StudioUserConfig> GetAsync(CancellationToken ct = default) => Task.FromResult(_config);

        public Task<StudioUserConfig> GetAsync(string scopeId, CancellationToken ct = default) => Task.FromResult(_config);
    }

    private sealed class RecordingUserConfigCommandService : IUserConfigCommandService
    {
        public string? SavedScopeId { get; private set; }
        public StudioUserConfig? SavedConfig { get; private set; }
        public string? SavedGithubUsername { get; private set; }

        public Task SaveAsync(StudioUserConfig config, CancellationToken ct = default)
        {
            SavedConfig = config;
            return Task.CompletedTask;
        }

        public Task SaveAsync(string scopeId, StudioUserConfig config, CancellationToken ct = default)
        {
            SavedScopeId = scopeId;
            return SaveAsync(config, ct);
        }

        public Task SaveGithubUsernameAsync(string scopeId, string githubUsername, CancellationToken ct = default)
        {
            SavedScopeId = scopeId;
            SavedGithubUsername = githubUsername;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal in-memory <see cref="ILogger{T}"/> that records each log call so tests can assert
    /// on level + formatted message. Avoids a full Microsoft.Extensions.Logging.Testing dependency
    /// for a single observability assertion.
    /// </summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }

        public sealed record LogEntry(LogLevel Level, string Message);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
