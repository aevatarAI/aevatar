using System.Runtime.CompilerServices;
using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class WorkflowRoleGAgentMappingTests
{
    [Fact]
    public void WorkflowRunScopeMapper_ShouldFillMissingOwnerScopeWhenCallerScopeAlreadyExists()
    {
        var context = AgentToolExecutionContext.Empty with
        {
            Caller = new AgentToolCallerContext("scope-caller-alpha", null, null),
        };

        var mapped = WorkflowRunScopeToolContextMapper.Apply(" scope-owner-alpha ", context);

        mapped.Caller.ScopeId.Should().Be("scope-caller-alpha");
        mapped.Caller.OwnerScopeId.Should().Be("scope-owner-alpha");
        mapped.Caller.OwnerSubject.Should().BeNull();
    }

    [Fact]
    public void WorkflowRunScopeMapper_ShouldPreserveExistingIndependentOwnerScope()
    {
        var context = AgentToolExecutionContext.Empty with
        {
            Caller = new AgentToolCallerContext(
                "scope-caller-alpha",
                "owner-subject-alpha",
                null,
                "scope-owner-beta"),
        };

        var mapped = WorkflowRunScopeToolContextMapper.Apply("scope-owner-alpha", context);

        mapped.Caller.ScopeId.Should().Be("scope-caller-alpha");
        mapped.Caller.OwnerScopeId.Should().Be("scope-owner-beta");
        mapped.Caller.OwnerSubject.Should().Be("owner-subject-alpha");
    }

    [Fact]
    public async Task WorkflowRoleGAgent_ShouldMapWorkflowCredentialAndRouteAtAiBoundary()
    {
        var provider = new RecordingLlmProvider();
        var publisher = new RecordingEventPublisher();
        var (agent, eventStore) = CreateAgent(provider, publisher);

        await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
        {
            RunId = "run-1",
            StepId = "reply",
            SessionId = "session-1",
            Prompt = "hello",
            ScopeId = " scope-owner-alpha ",
            ScheduleId = " schedule-1 ",
            Model = "model-a",
            UserMemoryPrompt = "memory",
            RoutePreference = " route-a ",
            CallerCredential = new WorkflowCallerCredential
            {
                BearerToken = " raw-token ",
                Kind = NyxIdCallerCredentialKind.SourceReadableUserBearer,
                NyxIdAuthority = new WorkflowCallerNyxIdAuthority
                {
                    ExternalUserId = " user-audit-alpha ",
                },
            },
            WorkflowRuntimeContext = new WorkflowToolRuntimeContextPayload
            {
                ParentActorId = "parent-actor",
                ParentRunId = "parent-run",
                ParentStepId = "reply",
                RootRunId = "root-run",
                Depth = 2,
            },
        });

        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.LlmControl.Should().NotBeNull();
        provider.LastRequest.LlmControl!.NyxIdAccessToken.Should().Be("raw-token");
        provider.LastRequest.LlmControl.NyxIdRoutePreference.Should().Be("route-a");
        provider.LastRequest.ToolContext.Should().NotBeNull();
        provider.LastRequest.ToolContext!.Credentials.NyxIdAccessToken.Should().Be("raw-token");
        provider.LastRequest.ToolContext.Credentials.NyxIdOrgToken.Should().Be("raw-token");
        provider.LastRequest.ToolContext.Caller.ScopeId.Should().Be("scope-owner-alpha");
        provider.LastRequest.ToolContext.Caller.OwnerScopeId.Should().Be("scope-owner-alpha");
        provider.LastRequest.ToolContext.Caller.OwnerSubject.Should().Be("user-audit-alpha");
        provider.LastRequest.ToolContext.Chat.Should().Be(new AgentChatInvocationContext(
            AgentChatInvocationSurface.WorkflowChat,
            "run-1",
            "session-1",
            null,
            "reply",
            null));
        provider.LastRequest.ToolContext.Routing.NyxIdRoutePreference.Should().Be("route-a");
        provider.LastRequest.ToolContext.WorkflowRuntime.ParentActorId.Should().Be("parent-actor");
        provider.LastRequest.ToolContext.WorkflowRuntime.ParentRunId.Should().Be("parent-run");
        provider.LastRequest.ToolContext.WorkflowRuntime.ParentStepId.Should().Be("reply");
        provider.LastRequest.ToolContext.WorkflowRuntime.RootRunId.Should().Be("root-run");
        provider.LastRequest.ToolContext.WorkflowRuntime.Depth.Should().Be(2);
        provider.LastRequest.ToolContext.WorkflowRuntime.HasManagedParent.Should().BeTrue();
        provider.LastRequest.ToolContext.Schedule.ScheduleId.Should().Be("schedule-1");
        (provider.LastRequest.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal))
            .Should()
            .BeEmpty();
        publisher.Published.OfType<WorkflowLlmInvocationCompletedEvent>()
            .Should()
            .ContainSingle(x => x.Success);
        (await eventStore.GetEventsAsync(agent.Id))
            .Select(stateEvent => stateEvent.EventData)
            .Should().ContainSingle(eventData =>
                eventData.Is(RoleChatSessionCompletedEvent.Descriptor) &&
                eventData.Unpack<RoleChatSessionCompletedEvent>().Outcome ==
                RoleChatSessionOutcome.Completed);
    }

    [Fact]
    public async Task WorkflowRoleGAgent_WhenToolReceiptCarriesManagedHandoff_ShouldPublishHandoffCompletion()
    {
        var provider = new RecordingLlmProvider
        {
            ToolReceipt = new AgentToolReceipt
            {
                CallId = "tool-call-1",
                ToolName = "aevatar_start_workflow",
                Status = AgentToolReceiptStatus.Success,
                ManagedWorkflowHandoff = new ManagedWorkflowHandoffReceipt
                {
                    ParentActorId = "parent-actor",
                    ParentRunId = "parent-run",
                    ParentStepId = "reply",
                    InvocationId = "parent-run:workflow_tool:reply:tool-call-1",
                    ChildRunId = "parent-run:workflow_tool:reply:tool-call-1",
                },
            },
        };
        var publisher = new RecordingEventPublisher();
        var (agent, eventStore) = CreateAgent(provider, publisher);

        await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
        {
            RunId = "parent-run",
            StepId = "reply",
            SessionId = "session-1",
            Prompt = "start child",
        });

        var completed = publisher.Published
            .OfType<WorkflowLlmInvocationCompletedEvent>()
            .Single(x => x.ManagedHandoff != null && !string.IsNullOrWhiteSpace(x.ManagedHandoff.InvocationId));
        completed.Success.Should().BeTrue();
        completed.ManagedHandoff.Should().NotBeNull();
        completed.ManagedHandoff.InvocationId.Should().Be("parent-run:workflow_tool:reply:tool-call-1");
        completed.ManagedHandoff.ParentStepId.Should().Be("reply");
        var committed = (await eventStore.GetEventsAsync(agent.Id))
            .Select(stateEvent => stateEvent.EventData)
            .Where(eventData => eventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(eventData => eventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should().ContainSingle().Which;
        committed.ToolReceipts.Should().ContainSingle(receipt =>
            receipt.ManagedWorkflowHandoff != null &&
            receipt.ManagedWorkflowHandoff.InvocationId ==
            "parent-run:workflow_tool:reply:tool-call-1");
    }

    [Fact]
    public async Task WorkflowRoleGAgent_ShouldMapWorkflowFileRefsToChatUriPartsWithoutBase64()
    {
        var provider = new RecordingLlmProvider();
        var publisher = new RecordingEventPublisher();
        var (agent, _) = CreateAgent(provider, publisher);
        var intent = new WorkflowLlmExecutionIntent
        {
            RunId = "run-files",
            StepId = "reply",
            SessionId = "session-files",
            Prompt = "describe this",
        };
        intent.InputFileRefs.Add(BuildWorkflowFileRef("file-role"));

        await agent.HandleWorkflowLlmExecutionIntent(intent);

        provider.LastRequest.Should().NotBeNull();
        var user = provider.LastRequest!.Messages.Should().ContainSingle().Subject;
        user.ContentParts.Should().NotBeNull();
        user.ContentParts!.Should().HaveCount(2);
        user.ContentParts[0].Kind.Should().Be(ContentPartKind.Text);
        user.ContentParts[0].Text.Should().Be("describe this");
        user.ContentParts[1].Kind.Should().Be(ContentPartKind.Image);
        user.ContentParts[1].Uri.Should().Be("workflow-file://file-role");
        user.ContentParts[1].MediaType.Should().Be("image/png");
        user.ContentParts[1].Name.Should().Be("file-role.png");
        user.ContentParts[1].DataBase64.Should().BeNull();
        var fileRef = user.ContentParts[1].FileRef;
        fileRef.Should().NotBeNull();
        fileRef!.FileId.Should().Be("file-role");
        fileRef.ArtifactId.Should().Be("workflow-file://file-role");
        fileRef.SourceKind.Should().Be(Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind.ConnectedServiceResource);
        fileRef.SourceMessageId.Should().Be("om_1");
        fileRef.SourceResourceKey.Should().Be("image_key_1");
        fileRef.FileName.Should().Be("file-role.png");
        fileRef.MediaType.Should().Be("image/png");
        fileRef.SizeBytes.Should().Be(3);
        fileRef.Sha256.Should().Be("sha-file-role");
        fileRef.CreatedAtUnixMs.Should().Be(1710000000000);
        fileRef.ExpiresAtUnixMs.Should().Be(1710003600000);
    }

    [Fact]
    public async Task WorkflowRoleGAgent_ShouldMapWorkflowToolScopeToAiVisibility()
    {
        var provider = new RecordingLlmProvider();
        var publisher = new RecordingEventPublisher();
        var (agent, _) = CreateAgent(provider, publisher);

        await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
        {
            RunId = "run-1",
            StepId = "reply",
            SessionId = "session-1",
            Prompt = "hello",
            AgentToolScope = new WorkflowAgentToolScope
            {
                AllowedToolNames = { "search" },
            },
        });

        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.ToolContext.Should().NotBeNull();
        provider.LastRequest.ToolContext!.ToolVisibility.IsRestricted.Should().BeTrue();
        provider.LastRequest.ToolContext.ToolVisibility.Allows("search").Should().BeTrue();
        provider.LastRequest.ToolContext.ToolVisibility.Allows("calendar").Should().BeFalse();
    }

    [Fact]
    public async Task WorkflowRoleGAgent_ShouldDiscoverConnectedServiceToolsPerCallerRequest()
    {
        var provider = new RecordingLlmProvider();
        var publisher = new RecordingEventPublisher();
        var source = new RecordingToolSource(new StaticAgentTool("nyxid_calendar_create_event"));
        var registry = new RecordingToolSetRegistry(source);
        var (agent, _) = CreateAgent(provider, publisher, registry);
        agent.AddTool(new StaticAgentTool("nyxid_proxy"));

        await agent.HandleWorkflowLlmExecutionIntent(BuildConnectedServiceIntent("token-a", "session-a"));

        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Tools.Should().ContainSingle(tool => tool.Name == "nyxid_calendar_create_event");
        provider.LastRequest.Tools.Should().NotContain(tool => tool.Name == "nyxid_proxy");
        provider.LastRequest.ToolContext!.InvocationSurface.Should().Be(AgentToolInvocationSurface.WorkflowLlmToolLoop);

        await agent.HandleWorkflowLlmExecutionIntent(BuildConnectedServiceIntent("token-b", "session-b"));

        source.AccessTokens.Should().Equal("token-a", "token-b");
        source.InvocationSurfaces.Should().OnlyContain(surface => surface == AgentToolInvocationSurface.WorkflowLlmToolLoop);
        registry.ResolvedNames.Should().Equal("nyxid.connected_services", "nyxid.connected_services");
    }

    [Fact]
    public async Task WorkflowRoleGAgent_WithOnlyToolSetRestriction_ShouldKeepRegisteredStaticTools()
    {
        var provider = new RecordingLlmProvider();
        var source = new RecordingToolSource(new StaticAgentTool("nyxid_calendar_create_event"));
        var (agent, _) = CreateAgent(
            provider,
            new RecordingEventPublisher(),
            new RecordingToolSetRegistry(source));
        agent.AddTool(new StaticAgentTool("search"));

        await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
        {
            RunId = "run-tool-set-only",
            StepId = "reply",
            SessionId = "session-tool-set-only",
            Prompt = "find and schedule",
            AgentToolScope = new WorkflowAgentToolScope
            {
                ToolSetRefs = { "nyxid.connected_services" },
            },
        });

        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Tools.Should().Contain(tool => tool.Name == "search");
        provider.LastRequest.Tools.Should().Contain(tool => tool.Name == "nyxid_calendar_create_event");
    }

    [Fact]
    public async Task WorkflowRoleGAgent_WithExplicitEmptyStaticRestriction_ShouldExposeOnlyRequestTools()
    {
        var provider = new RecordingLlmProvider();
        var source = new RecordingToolSource(new StaticAgentTool("nyxid_calendar_create_event"));
        var (agent, _) = CreateAgent(
            provider,
            new RecordingEventPublisher(),
            new RecordingToolSetRegistry(source));
        agent.AddTool(new StaticAgentTool("search"));

        await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
        {
            RunId = "run-empty-static",
            StepId = "reply",
            SessionId = "session-empty-static",
            Prompt = "schedule only",
            AgentToolScope = new WorkflowAgentToolScope
            {
                RestrictAllowedToolNames = true,
                RestrictToolSets = true,
                ToolSetRefs = { "nyxid.connected_services" },
            },
        });

        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Tools.Should().NotContain(tool => tool.Name == "search");
        provider.LastRequest.Tools.Should().ContainSingle(tool => tool.Name == "nyxid_calendar_create_event");
    }

    private static WorkflowLlmExecutionIntent BuildConnectedServiceIntent(string token, string sessionId) =>
        new()
        {
            RunId = $"run-{sessionId}",
            StepId = "reply",
            SessionId = sessionId,
            Prompt = "create an event",
            CallerCredential = new WorkflowCallerCredential { BearerToken = token },
            WorkflowRuntimeContext = new WorkflowToolRuntimeContextPayload
            {
                ParentActorId = "parent-actor",
                ParentRunId = $"parent-{sessionId}",
                ParentStepId = "reply",
            },
            AgentToolScope = new WorkflowAgentToolScope
            {
                AllowedToolNames = { "search" },
                ToolSetRefs = { "nyxid.connected_services" },
            },
        };

    private sealed class RecordingLlmProvider : ILLMProviderFactory, ILLMProvider
    {
        public LLMRequest? LastRequest { get; private set; }

        public AgentToolReceipt? ToolReceipt { get; init; }

        public string Name => "recording";

        public ILLMProvider GetProvider(string name)
        {
            _ = name;
            return this;
        }

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = request;
            await Task.Yield();
            yield return new LLMStreamChunk
            {
                DeltaContent = "ok",
            };
            if (ToolReceipt != null)
            {
                yield return new LLMStreamChunk
                {
                    ToolReceipt = ToolReceipt,
                };
            }

            yield return new LLMStreamChunk
            {
                IsLast = true,
                FinishReason = "stop",
            };
        }
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<IMessage> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = audience;
            _ = ct;
            _ = sourceEnvelope;
            _ = options;
            Published.Add(evt);
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            return PublishAsync(evt, TopologyAudience.Children, ct, sourceEnvelope, options);
        }
    }

    private sealed class RecordingToolSetRegistry(IAgentToolSource source) : IToolSetRegistry
    {
        public List<string> ResolvedNames { get; } = [];

        public IReadOnlyList<string> GetRegisteredNames() => ["nyxid.connected_services"];

        public ToolSetResolveResult Resolve(string? name)
        {
            name ??= string.Empty;
            ResolvedNames.Add(name);
            return ToolSetResolveResult.Success(name, [source]);
        }
    }

    private static (TestWorkflowRoleGAgent Agent, InMemoryEventStore EventStore) CreateAgent(
        ILLMProviderFactory provider,
        RecordingEventPublisher publisher,
        IToolSetRegistry? registry = null)
    {
        var eventStore = new InMemoryEventStore();
        var vault = new InMemorySecretVault();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<ISecretVault>(vault)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var agent = new TestWorkflowRoleGAgent(provider, registry, vault)
        {
            Services = services,
            EventPublisher = publisher,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        SetAgentId(agent, $"workflow-role-mapping-{Guid.NewGuid():N}");
        return (agent, eventStore);
    }

    private static void SetAgentId(Aevatar.Foundation.Core.GAgentBase agent, string agentId) =>
        typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [agentId]);

    private sealed class TestWorkflowRoleGAgent(
        ILLMProviderFactory provider,
        IToolSetRegistry? registry,
        ISecretVault chatToolRecoverySecretVault)
        : WorkflowRoleGAgent(
            UnexpectedAgentToolExecutionPort.Instance,
            provider,
            toolSetRegistry: registry,
            chatToolRecoverySecretVault: chatToolRecoverySecretVault)
    {
        public void AddTool(IAgentTool tool) => RegisterTool(tool);
    }

    private sealed class RecordingToolSource(IAgentTool tool) : IAgentToolSource
    {
        public List<string?> AccessTokens { get; } = [];
        public List<AgentToolInvocationSurface> InvocationSurfaces { get; } = [];

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AccessTokens.Add(AgentToolRequestContext.NyxIdAccessToken);
            InvocationSurfaces.Add(AgentToolRequestContext.Current?.InvocationSurface ?? AgentToolInvocationSurface.Unspecified);
            return Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
        }
    }

    private sealed class StaticAgentTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => "Connected service operation";
        public string ParametersSchema => "{\"type\":\"object\"}";
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private static WorkflowFileRef BuildWorkflowFileRef(string fileId) =>
        new()
        {
            FileId = fileId,
            ArtifactId = $"workflow-file://{fileId}",
            SourceKind = WorkflowFileSourceKind.ConnectedServiceResource,
            SourceMessageId = "om_1",
            SourceResourceKey = "image_key_1",
            FileName = $"{fileId}.png",
            MediaType = "image/png",
            SizeBytes = 3,
            Sha256 = $"sha-{fileId}",
            CreatedAtUnixMs = 1710000000000,
            ExpiresAtUnixMs = 1710003600000,
        };

    private sealed class UnexpectedAgentToolExecutionPort : IAgentToolExecutionPort
    {
        public static UnexpectedAgentToolExecutionPort Instance { get; } = new();

        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException(
                $"Tool '{request.Tool.Name}' must not execute in workflow request-mapping tests.");
    }
}
