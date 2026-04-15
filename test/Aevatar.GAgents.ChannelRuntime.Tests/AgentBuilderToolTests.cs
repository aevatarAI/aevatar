using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentBuilderToolTests
{
    [Fact]
    public async Task ExecuteAsync_ListTemplates_ReturnsDailyReportTemplate()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IAgentRegistryQueryPort>());
        services.AddSingleton(Substitute.For<IActorRuntime>());
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));

        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"list_templates"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("templates").EnumerateArray()
                .Any(static x => x.GetProperty("name").GetString() == "daily_report")
                .Should().BeTrue();
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_RejectsGroupChats()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IAgentRegistryQueryPort>());
        services.AddSingleton(Substitute.For<IActorRuntime>());
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));

        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "group",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "github_username": "alice",
                  "schedule_cron": "0 9 * * *"
                }
                """);

            result.Should().Contain("private chat");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DispatchesInitializeAndImmediateTrigger()
    {
        var queryPort = Substitute.For<IAgentRegistryQueryPort>();
        queryPort.GetStateVersionAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = SkillRunnerDefaults.StatusRunning,
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<SkillRunnerGAgent>("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(skillRunnerActor));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/proxy/services", """
            [
              {"id":"svc-github","slug":"api-github"},
              {"id":"svc-lark","slug":"api-lark-bot"}
            ]
            """);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-1","full_key":"full-key-1"}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "agent_id": "skill-runner-1",
                  "github_username": "alice",
                  "repositories": "aevatarAI/aevatar",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC",
                  "run_immediately": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("created");
            doc.RootElement.GetProperty("agent_id").GetString().Should().Be("skill-runner-1");
            doc.RootElement.GetProperty("api_key_id").GetString().Should().Be("key-1");

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(InitializeSkillRunnerCommand.Descriptor) &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().TemplateName == "daily_report" &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().ScopeId == "scope-1" &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.ConversationId == "oc_chat_1" &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.NyxProviderSlug == "api-lark-bot" &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.NyxApiKey == "full-key-1" &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.ApiKeyId == "key-1" &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.OwnerNyxUserId == "user-1"),
                Arg.Any<CancellationToken>());

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(TriggerSkillRunnerExecutionCommand.Descriptor) &&
                    e.Payload.Unpack<TriggerSkillRunnerExecutionCommand>().Reason == "create_agent"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_SocialMedia_UpsertsWorkflowAndInitializesWorkflowAgent()
    {
        var queryPort = Substitute.For<IAgentRegistryQueryPort>();
        queryPort.GetStateVersionAsync("workflow-agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        queryPort.GetAsync("workflow-agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = "workflow-agent-1",
                AgentType = WorkflowAgentDefaults.AgentType,
                TemplateName = WorkflowAgentDefaults.TemplateName,
                Status = WorkflowAgentDefaults.StatusRunning,
            }));

        var workflowAgentActor = Substitute.For<IActor>();
        workflowAgentActor.Id.Returns("workflow-agent-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("workflow-agent-1").Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<WorkflowAgentGAgent>("workflow-agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(workflowAgentActor));

        var workflowCommandPort = Substitute.For<IScopeWorkflowCommandPort>();
        workflowCommandPort.UpsertAsync(Arg.Any<ScopeWorkflowUpsertRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ScopeWorkflowUpsertResult(
                new ScopeWorkflowSummary(
                    "scope-1",
                    "social-media-workflow-agent-1",
                    "Social Media Approval workflow-agent-1",
                    "service-key",
                    "social_media_workflow_agent_1",
                    "workflow-actor-1",
                    "rev-1",
                    "deploy-1",
                    "active",
                    DateTimeOffset.UtcNow),
                "rev-1",
                "workflow-actor-prefix",
                "workflow-actor-1")));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-2","full_key":"full-key-2"}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(workflowCommandPort);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "social_media",
                  "agent_id": "workflow-agent-1",
                  "topic": "Launch update for the new workflow feature",
                  "audience": "Developers",
                  "style": "Confident and concise",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC",
                  "run_immediately": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("created");
            doc.RootElement.GetProperty("agent_id").GetString().Should().Be("workflow-agent-1");
            doc.RootElement.GetProperty("agent_type").GetString().Should().Be(WorkflowAgentDefaults.AgentType);
            doc.RootElement.GetProperty("workflow_id").GetString().Should().Be("social-media-workflow-agent-1");
            doc.RootElement.GetProperty("api_key_id").GetString().Should().Be("key-2");

            await workflowCommandPort.Received(1).UpsertAsync(
                Arg.Is<ScopeWorkflowUpsertRequest>(request =>
                    request.ScopeId == "scope-1" &&
                    request.WorkflowId == "social-media-workflow-agent-1" &&
                    request.WorkflowYaml.Contains("provider: nyxid", StringComparison.Ordinal) &&
                    request.WorkflowYaml.Contains("type: human_approval", StringComparison.Ordinal) &&
                    request.WorkflowYaml.Contains("delivery_target_id: \"workflow-agent-1\"", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());

            await workflowAgentActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(InitializeWorkflowAgentCommand.Descriptor) &&
                    e.Payload.Unpack<InitializeWorkflowAgentCommand>().WorkflowActorId == "workflow-actor-1" &&
                    e.Payload.Unpack<InitializeWorkflowAgentCommand>().ConversationId == "oc_chat_1" &&
                    e.Payload.Unpack<InitializeWorkflowAgentCommand>().NyxApiKey == "full-key-2" &&
                    e.Payload.Unpack<InitializeWorkflowAgentCommand>().ApiKeyId == "key-2"),
                Arg.Any<CancellationToken>());

            await workflowAgentActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(TriggerWorkflowAgentExecutionCommand.Descriptor) &&
                    e.Payload.Unpack<TriggerWorkflowAgentExecutionCommand>().Reason == "create_agent"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAgent_DisablesActor_RevokesApiKey_AndTombstonesRegistry()
    {
        var queryPort = Substitute.For<IAgentRegistryQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    ApiKeyId = "key-1",
                    OwnerNyxUserId = "user-1",
                }),
                Task.FromResult<AgentRegistryEntry?>(null));
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRegistryEntry>>(Array.Empty<AgentRegistryEntry>()));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");
        var registryActor = Substitute.For<IActor>();
        registryActor.Id.Returns(AgentRegistryGAgent.WellKnownId);

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));
        actorRuntime.GetAsync(AgentRegistryGAgent.WellKnownId).Returns(Task.FromResult<IActor?>(registryActor));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-1", """{"ok":true}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);
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

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(DisableSkillRunnerCommand.Descriptor) &&
                    e.Payload.Unpack<DisableSkillRunnerCommand>().Reason == "delete_agent"),
                Arg.Any<CancellationToken>());

            await registryActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(AgentRegistryTombstoneCommand.Descriptor) &&
                    e.Payload.Unpack<AgentRegistryTombstoneCommand>().AgentId == "skill-runner-1"),
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
    public async Task ExecuteAsync_RunAgent_DispatchesManualTrigger()
    {
        var queryPort = Substitute.For<IAgentRegistryQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
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

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(TriggerSkillRunnerExecutionCommand.Descriptor) &&
                    e.Payload.Unpack<TriggerSkillRunnerExecutionCommand>().Reason == "run_agent"),
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
        var queryPort = Substitute.For<IAgentRegistryQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = SkillRunnerDefaults.StatusDisabled,
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
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
            await skillRunnerActor.DidNotReceive().HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunAgent_DispatchesWorkflowTrigger()
    {
        var queryPort = Substitute.For<IAgentRegistryQueryPort>();
        queryPort.GetAsync("workflow-agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = "workflow-agent-1",
                AgentType = WorkflowAgentDefaults.AgentType,
                TemplateName = WorkflowAgentDefaults.TemplateName,
                Status = WorkflowAgentDefaults.StatusRunning,
            }));

        var workflowAgentActor = Substitute.For<IActor>();
        workflowAgentActor.Id.Returns("workflow-agent-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("workflow-agent-1").Returns(Task.FromResult<IActor?>(workflowAgentActor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
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
                  "agent_id": "workflow-agent-1"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("agent_id").GetString().Should().Be("workflow-agent-1");

            await workflowAgentActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(TriggerWorkflowAgentExecutionCommand.Descriptor) &&
                    e.Payload.Unpack<TriggerWorkflowAgentExecutionCommand>().Reason == "run_agent"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DisableAgent_DispatchesDisableAndReturnsStatus()
    {
        var queryPort = Substitute.For<IAgentRegistryQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusRunning,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }),
                Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusDisabled,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
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
            doc.RootElement.GetProperty("note").GetString().Should().Contain("Scheduling paused");

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(DisableSkillRunnerCommand.Descriptor) &&
                    e.Payload.Unpack<DisableSkillRunnerCommand>().Reason == "disable_agent"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_EnableAgent_DispatchesEnableAndReturnsStatus()
    {
        var queryPort = Substitute.For<IAgentRegistryQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusDisabled,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }),
                Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusRunning,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
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
            doc.RootElement.GetProperty("status").GetString().Should().Be(SkillRunnerDefaults.StatusRunning);
            doc.RootElement.GetProperty("note").GetString().Should().Contain("Scheduling resumed");

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(EnableSkillRunnerCommand.Descriptor) &&
                    e.Payload.Unpack<EnableSkillRunnerCommand>().Reason == "enable_agent"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DisableAgent_DispatchesWorkflowDisableAndReturnsStatus()
    {
        var queryPort = Substitute.For<IAgentRegistryQueryPort>();
        queryPort.GetAsync("workflow-agent-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
                {
                    AgentId = "workflow-agent-1",
                    AgentType = WorkflowAgentDefaults.AgentType,
                    TemplateName = WorkflowAgentDefaults.TemplateName,
                    Status = WorkflowAgentDefaults.StatusRunning,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }),
                Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
                {
                    AgentId = "workflow-agent-1",
                    AgentType = WorkflowAgentDefaults.AgentType,
                    TemplateName = WorkflowAgentDefaults.TemplateName,
                    Status = WorkflowAgentDefaults.StatusDisabled,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }));

        var workflowAgentActor = Substitute.For<IActor>();
        workflowAgentActor.Id.Returns("workflow-agent-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("workflow-agent-1").Returns(Task.FromResult<IActor?>(workflowAgentActor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
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
                  "agent_id": "workflow-agent-1"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be(WorkflowAgentDefaults.StatusDisabled);
            doc.RootElement.GetProperty("note").GetString().Should().Contain("Scheduling paused");

            await workflowAgentActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(DisableWorkflowAgentCommand.Descriptor) &&
                    e.Payload.Unpack<DisableWorkflowAgentCommand>().Reason == "disable_agent"),
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
}
