using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using System.Reflection;
using Any = Google.Protobuf.WellKnownTypes.Any;
using StringValue = Google.Protobuf.WellKnownTypes.StringValue;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Aevatar.Integration.Tests;

public abstract class WorkflowGAgentTestBase
{
        internal static WorkflowGAgent CreateDefinitionAgent(IEventStore? eventStore = null)
        {
            eventStore ??= new InMemoryEventStore();
            var services = BuildServices(eventStore, workflowResolver: null);
            var agent = new WorkflowGAgent
            {
                Services = services,
            };
            agent.EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowState>>();
            return agent;
        }

        internal static Task BindInteractiveWorkflowDefinitionAsync(
            WorkflowGAgent agent,
            string workflowYaml,
            string? workflowName = null,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
            string? scopeId = null,
            string? sourceKind = null,
            WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan = null,
            string? workflowId = null,
            string? revisionId = null,
            CancellationToken ct = default) =>
            agent.BindWorkflowDefinitionAsync(
                workflowYaml,
                workflowName,
                inlineWorkflowYamls,
                scopeId,
                sourceKind,
                capabilityAdmissionPlan,
                workflowId,
                revisionId,
                ExternalCapabilityExecutionMode.Interactive,
                ct);

        internal static Task BindInteractiveWorkflowRunDefinitionAsync(
            WorkflowRunGAgent agent,
            string definitionActorId,
            string workflowYaml,
            string? workflowName = null,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
            string? runId = null,
            string? scopeId = null,
            string? runOrigin = null,
            string? scheduleId = null,
            WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan = null,
            CancellationToken ct = default) =>
            agent.BindWorkflowRunDefinitionAsync(
                definitionActorId,
                workflowYaml,
                workflowName,
                inlineWorkflowYamls,
                runId,
                scopeId,
                runOrigin,
                scheduleId,
                workflowId: null,
                revisionId: null,
                definitionVersion: 0,
                capabilityAdmissionPlan,
                ExternalCapabilityExecutionMode.Interactive,
                ct: ct);

        internal static async Task<WorkflowGAgent> CreateRegisteredDefinitionAgentAsync(
            RecordingActorRuntime runtime,
            RecordingEventPublisher publisher,
            string actorId,
            string workflowName,
            string workflowYaml)
        {
            var agent = CreateDefinitionAgent();
            SetAgentId(agent, actorId);
            agent.EventPublisher = publisher;
            await BindInteractiveWorkflowDefinitionAsync(agent, workflowYaml, workflowName);
            runtime.RegisterAgent(actorId, agent);
            return agent;
        }

        internal static async Task<(TestWorkflowRoleGAgent Agent, RecordingEventPublisher Publisher)> CreateActivatedWorkflowRoleAgentAsync(
            IEventStore eventStore,
            ILLMProviderFactory llmProviderFactory,
            string agentId,
            IEnumerable<IAgentTool>? tools = null,
            IToolSetRegistry? toolSetRegistry = null,
            IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider = null,
            TimeProvider? timeProvider = null,
            RoleChatExecutionOptions? chatExecutionOptions = null,
            IActorRuntimeCallbackScheduler? callbackScheduler = null,
            ISecretVault? chatToolRecoverySecretVault = null)
        {
            if (timeProvider is FakeTimeProvider fakeTimeProvider)
                fakeTimeProvider.SetUtcNow(DateTimeOffset.UtcNow);
            chatToolRecoverySecretVault ??= new InMemorySecretVault();
            var serviceCollection = new ServiceCollection()
                .AddSingleton<IEventStore>(eventStore)
                .AddSingleton(eventStore)
                .AddSingleton<ISecretVault>(chatToolRecoverySecretVault)
                .AddSingleton<EventSourcingRuntimeOptions>()
                .AddSingleton<IAuditTrailAppender, AppendedAuditTrail>()
                .AddSingleton<IAuditActorIdentityHasher, StableIdentityHasher>()
                .AddSingleton<IAgentToolAdmissionLedger>(AlwaysStartingAgentToolAdmissionLedger.Instance)
                .AddSingleton<IAgentToolExecutionPort, AdmittedAgentToolExecutor>()
                .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
            if (callbackScheduler is not null)
                serviceCollection.AddSingleton<IActorRuntimeCallbackScheduler>(callbackScheduler);
            var services = serviceCollection.BuildServiceProvider();
            var publisher = new RecordingEventPublisher();
            var agent = new TestWorkflowRoleGAgent(
                services.GetRequiredService<IAgentToolExecutionPort>(),
                llmProviderFactory,
                toolSetRegistry,
                callerAccessTokenProvider,
                timeProvider,
                chatExecutionOptions,
                chatToolRecoverySecretVault)
            {
                Services = services,
                EventPublisher = publisher,
                EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
            };
            foreach (var tool in tools ?? [])
                agent.RegisterToolForTest(tool);
            SetAgentId(agent, agentId);
            await agent.ActivateAsync();
            await agent.HandleWorkflowRoleInitialize(new WorkflowRoleInitializeEvent
            {
                RoleId = "assistant",
                RoleName = "Assistant",
                ProviderName = "mock",
                SystemPrompt = "workflow role",
            });
            return (agent, publisher);
        }

        private sealed class AppendedAuditTrail : IAuditTrailAppender
        {
            public Task<AuditTrailAppendResult> AppendAsync(
                AuditRecord record,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
        }

        private sealed class StableIdentityHasher : IAuditActorIdentityHasher
        {
            public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

            public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
        }

        internal sealed class SuccessfulWorkflowTool(string name) : IAgentTool
        {
            private int _executeCount;

            public string Name => name;
            public string Description => "Workflow integration test tool";
            public string ParametersSchema => "{}";
            public int ExecuteCount => Volatile.Read(ref _executeCount);

            public AgentToolReceipt CreateSuccessReceipt(
                string callId,
                string toolName,
                string resultJson) => new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = resultJson,
            };

            public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            {
                Interlocked.Increment(ref _executeCount);
                return Task.FromResult("{}");
            }
        }

        internal sealed class TestWorkflowRoleGAgent(
            IAgentToolExecutionPort toolExecutionPort,
            ILLMProviderFactory llmProviderFactory,
            IToolSetRegistry? toolSetRegistry,
            IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider,
            TimeProvider? timeProvider,
            RoleChatExecutionOptions? chatExecutionOptions,
            ISecretVault chatToolRecoverySecretVault)
            : WorkflowRoleGAgent(
                toolExecutionPort,
                llmProviderFactory,
                toolSetRegistry: toolSetRegistry,
                callerAccessTokenProvider: callerAccessTokenProvider,
                chatExecutionOptions: chatExecutionOptions,
                timeProvider: timeProvider,
                chatToolRecoverySecretVault: chatToolRecoverySecretVault)
        {
            public void RegisterToolForTest(IAgentTool tool) => RegisterTool(tool);

            public async Task StartRecoverySessionForTestAsync(
                ChatRequestEvent request,
                AgentToolExecutionContext toolContext,
                CancellationToken ct = default)
            {
                await EstablishTurnAuthorityAsync(request, trackedSession: null, toolContext, ct);
            }
        }

        internal sealed class AlwaysStartingAgentToolAdmissionLedger : IAgentToolAdmissionLedger
        {
            public static AlwaysStartingAgentToolAdmissionLedger Instance { get; } = new();

            public Task<AgentToolAdmissionResult> TryStartAsync(
                AgentToolAdmissionFact fact,
                CancellationToken ct = default)
            {
                ArgumentNullException.ThrowIfNull(fact);
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new AgentToolAdmissionResult(AgentToolAdmissionStatus.Started));
            }
        }

        internal sealed class UnexpectedAgentToolExecutionPort : IAgentToolExecutionPort
        {
            public static UnexpectedAgentToolExecutionPort Instance { get; } = new();

            public Task<AgentToolExecutionOutcome> ExecuteAsync(
                AgentToolExecutionRequest request,
                CancellationToken ct = default) =>
                throw new InvalidOperationException(
                    $"Tool '{request.Tool.Name}' must not execute in workflow mapping tests.");
        }

        internal static WorkflowRunGAgent CreateRunAgent(
            RecordingActorRuntime? runtime = null,
            IEventModuleFactory<IWorkflowExecutionContext>? eventModuleFactory = null,
            IEnumerable<IWorkflowModulePack>? packs = null,
            IEventStore? eventStore = null,
            IWorkflowDefinitionResolver? workflowResolver = null,
            IRuntimeSecretStore? runtimeSecretStore = null)
        {
            runtime ??= new RecordingActorRuntime();
            eventModuleFactory ??= new RecordingEventModuleFactory();
            packs ??= [];
            eventStore ??= new InMemoryEventStore();

            var services = BuildServices(eventStore, workflowResolver, runtimeSecretStore);
            var agent = new WorkflowRunGAgent(runtime, runtime, eventModuleFactory, packs, workflowResolver)
            {
                Services = services,
            };
            agent.EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowRunState>>();
            return agent;
        }

        internal static ServiceProvider BuildServices(
            IEventStore eventStore,
            IWorkflowDefinitionResolver? workflowResolver,
            IRuntimeSecretStore? runtimeSecretStore = null)
        {
            runtimeSecretStore ??= new InMemoryRuntimeSecretStore();
            var services = new ServiceCollection()
                .AddSingleton(eventStore)
                .AddSingleton<IEventStore>(eventStore)
                .AddSingleton<IStreamProvider, InMemoryStreamProvider>()
                .AddSingleton(runtimeSecretStore)
                .AddSingleton<InMemoryActorRuntimeCallbackScheduler>()
                .AddSingleton<IActorRuntimeCallbackScheduler>(sp =>
                    sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>())
                .AddSingleton<EventSourcingRuntimeOptions>()
                .AddSingleton<IAgentToolExecutionPort>(UnexpectedAgentToolExecutionPort.Instance)
                .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
                .AddAevatarWorkflow();

            if (workflowResolver != null)
                services.AddSingleton(workflowResolver);

            return services.BuildServiceProvider();
        }

        internal static EventEnvelope Envelope(
            IMessage message,
            string publisherId,
            TopologyAudience direction,
            string? id = null)
        {
            return new EventEnvelope
            {
                Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(message),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisherId, direction),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = Guid.NewGuid().ToString("N"),
                },
            };
        }

        internal static async Task ResolveLatestDefinitionRequestAsync(
            WorkflowRunGAgent runAgent,
            RecordingEventPublisher runPublisher,
            WorkflowGAgent definitionAgent,
            RecordingEventPublisher definitionPublisher)
        {
            var request = runPublisher.Sent.Select(x => x.evt).OfType<SubWorkflowDefinitionResolveRequestedEvent>().Last();
            await definitionAgent.HandleSubWorkflowDefinitionResolveRequested(request);

            var reply = definitionPublisher.Sent.Last();
            await runAgent.HandleEventAsync(Envelope(
                reply.evt,
                definitionAgent.Id,
                TopologyAudience.Children));

            switch (reply.evt)
            {
                case SubWorkflowDefinitionResolvedEvent:
                case SubWorkflowDefinitionResolveFailedEvent:
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected workflow definition reply '{reply.evt.Descriptor.FullName}'.");
            }
        }

        internal static void SetAgentId(GAgentBase agent, string agentId)
        {
            var setIdMethod = typeof(GAgentBase).GetMethod(
                "SetId",
                BindingFlags.Instance | BindingFlags.NonPublic);
            setIdMethod.Should().NotBeNull();
            setIdMethod!.Invoke(agent, [agentId]);
        }

        internal static async Task SeedRuntimeContextAsync(WorkflowRunGAgent agent)
        {
            var host = (IWorkflowExecutionStateHost)agent;
            await host.UpdateExecutionContextAsync(
                new WorkflowRunExecutionContextDelta
                {
                    ClearLlm = true,
                    ClearCallerCredential = true,
                    Llm = new WorkflowRunLlmExecutionContextDelta
                    {
                        ModelOverride = "model",
                        MaxToolRoundsOverride = 2,
                        UserMemoryPrompt = "memory",
                    },
                    CallerCredential = new WorkflowCallerCredential
                    {
                        BearerToken = "secret",
                    },
                });
            host.RuntimeContext.RequestPassthroughMetadata.Set("trace-id", "abc");
        }

        internal static void AssertRuntimeContextCleared(WorkflowRunGAgent agent)
        {
            var host = (IWorkflowExecutionStateHost)agent;
            host.ExecutionContextSnapshot.Llm.Should().BeNull();
            host.ExecutionContextSnapshot.CallerCredential.Should().BeNull();
            host.RuntimeContext.RequestPassthroughMetadata.Values.Should().BeEmpty();
        }

        internal static (object Accumulator, MethodInfo Track, MethodInfo Build) CreateWorkflowToolCallAccumulator()
        {
            var type = typeof(WorkflowRoleGAgent).GetNestedType(
                "WorkflowToolCallAccumulator",
                BindingFlags.NonPublic);
            type.Should().NotBeNull();
            var accumulator = Activator.CreateInstance(type!);
            accumulator.Should().NotBeNull();
            var track = type!.GetMethod("TrackDelta", BindingFlags.Public | BindingFlags.Instance);
            var build = type.GetMethod("BuildToolCalls", BindingFlags.Public | BindingFlags.Instance);
            track.Should().NotBeNull();
            build.Should().NotBeNull();
            return (accumulator!, track!, build!);
        }

        internal static string BuildValidWorkflowYaml(
            string roleId,
            string roleName,
            string? provider = null,
            string? model = null,
            string? workflowName = null,
            bool includeAgentKind = true)
        {
            var name = workflowName ?? "wf_valid";
            var agentKindLine = includeAgentKind ? "\n    agent_kind: workflow.role-agent" : string.Empty;
            var providerLine = string.IsNullOrWhiteSpace(provider) ? string.Empty : $"\n    provider: \"{provider}\"";
            var modelLine = string.IsNullOrWhiteSpace(model) ? string.Empty : $"\n    model: \"{model}\"";
            return $$"""
                     name: {{name}}
                     roles:
                       - id: "{{roleId}}"
                         name: "{{roleName}}"
                         system_prompt: "helpful role"{{agentKindLine}}{{providerLine}}{{modelLine}}
                     steps:
                       - id: step_1
                         type: transform
                     """;
        }

        internal static string BuildWorkflowYamlWithFullRoleConfig()
        {
            return """
                   name: wf_valid
                   roles:
                     - id: role_a
                       name: RoleA
                       agent_kind: workflow.role-agent
                       system_prompt: "helpful role"
                       provider: openai
                       model: gpt-5.4
                       temperature: 0.2
                       max_tokens: 256
                       max_tool_rounds: 4
                       max_history_messages: 30
                       event_modules: "llm_handler,tool_handler"
                       event_routes: |
                         event.type == ChatRequestEvent -> llm_handler
                   steps:
                     - id: step_1
                       type: transform
                   """;
        }

        internal sealed class RecordingEventPublisher : IEventPublisher, ICommittedStateEventPublisher
        {
            public List<(IMessage evt, TopologyAudience direction)> Published { get; } = [];
            public List<(IMessage Event, TopologyAudience Direction, EventEnvelopePublishOptions? Options)>
                PublicationsWithOptions { get; } = [];
            public List<(string targetActorId, IMessage evt)> Sent { get; } = [];
            public Func<IMessage, CancellationToken, Task>? BeforePublishAsync { get; set; }
            public Func<IMessage, CancellationToken, Task>? BeforeSendAsync { get; set; }

            public async Task PublishAsync<TEvent>(
                TEvent evt,
                TopologyAudience direction = TopologyAudience.Children,
                CancellationToken ct = default,
                EventEnvelope? sourceEnvelope = null,
                EventEnvelopePublishOptions? options = null)
                where TEvent : IMessage
            {
                _ = sourceEnvelope;
                _ = options;
                if (BeforePublishAsync is not null)
                    await BeforePublishAsync(evt, ct);
                ct.ThrowIfCancellationRequested();
                Published.Add((evt, direction));
                PublicationsWithOptions.Add((evt, direction, options));
            }

            public async Task SendToAsync<TEvent>(
                string targetActorId,
                TEvent evt,
                CancellationToken ct = default,
                EventEnvelope? sourceEnvelope = null,
                EventEnvelopePublishOptions? options = null)
                where TEvent : IMessage
            {
                Sent.Add((targetActorId, evt));
                _ = sourceEnvelope;
                _ = options;
                if (BeforeSendAsync is not null)
                    await BeforeSendAsync(evt, ct);
                ct.ThrowIfCancellationRequested();
                Published.Add((evt, TopologyAudience.Self));
            }

            public Task PublishCommittedStateEventAsync(
                CommittedStateEventPublished evt,
                ObserverAudience audience = ObserverAudience.CommittedFacts,
                CancellationToken ct = default,
                EventEnvelope? sourceEnvelope = null,
                EventEnvelopePublishOptions? options = null)
            {
                _ = audience;
                _ = sourceEnvelope;
                _ = options;
                Published.Add((evt, TopologyAudience.Self));
                return Task.CompletedTask;
            }

            Task ICommittedStateEventPublisher.PublishAsync(
                CommittedStateEventPublished evt,
                ObserverAudience audience,
                CancellationToken ct,
                EventEnvelope? sourceEnvelope,
                EventEnvelopePublishOptions? options)
            {
                _ = audience;
                _ = sourceEnvelope;
                _ = options;
                Published.Add((evt, TopologyAudience.Self));
                return Task.CompletedTask;
            }
        }

        internal sealed class RecordingWorkflowIntentLlmProvider : ILLMProviderFactory, ILLMProvider
        {
            public List<LLMRequest> Requests { get; } = [];
            public string Name => "mock";

            public ILLMProvider GetProvider(string name) => this;
            public ILLMProvider GetDefault() => this;
            public IReadOnlyList<string> GetAvailableProviders() => [Name];

            public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                Requests.Add(request);
                yield return new LLMStreamChunk { DeltaContent = "workflow " };
                yield return new LLMStreamChunk { DeltaReasoningContent = "reasoning" };
                yield return new LLMStreamChunk { DeltaContent = "answer" };
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
                await Task.CompletedTask;
            }
        }

        internal abstract class WorkflowIntentLlmProviderBase : ILLMProviderFactory, ILLMProvider
        {
            public string Name => "mock";

            public ILLMProvider GetProvider(string name) => this;
            public ILLMProvider GetDefault() => this;
            public IReadOnlyList<string> GetAvailableProviders() => [Name];

            public abstract IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                CancellationToken ct = default);
        }

        internal sealed class ThrowingWorkflowIntentLlmProvider(Exception exception) : WorkflowIntentLlmProviderBase
        {
            private int _callCount;

            public int CallCount => Volatile.Read(ref _callCount);

            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                _ = request;
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _callCount);
                await Task.CompletedTask;
                if (exception is not null)
                    throw exception;
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
            }
        }

        internal sealed class EmptyMessageThrowingWorkflowIntentLlmProvider : WorkflowIntentLlmProviderBase
        {
            public List<LLMRequest> Requests { get; } = [];

            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                Requests.Add(request);
                await Task.CompletedTask;
                var emit = false;
                if (emit)
                    yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
                throw new InvalidOperationException(" ");
            }
        }

        internal sealed class CancellationWorkflowIntentLlmProvider : WorkflowIntentLlmProviderBase
        {
            private readonly TaskCompletionSource _streamStarted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _cancellationObserved =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _neverCompletes =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task StreamStarted => _streamStarted.Task;
            public Task CancellationObserved => _cancellationObserved.Task;

            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                _ = request;
                _streamStarted.TrySetResult();
                try
                {
                    await _neverCompletes.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _cancellationObserved.TrySetResult();
                    throw;
                }

                var emit = false;
                if (emit)
                    yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
            }
        }

        internal sealed class ToolCallWorkflowIntentLlmProvider : WorkflowIntentLlmProviderBase
        {
            private int _calls;

            public List<LLMRequest> Requests { get; } = [];
            public int CallCount => Volatile.Read(ref _calls);

            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                Requests.Add(request);
                if (Interlocked.Increment(ref _calls) > 1)
                {
                    yield return new LLMStreamChunk { DeltaContent = "done" };
                    yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
                    await Task.CompletedTask;
                    yield break;
                }

                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call-1",
                        Name = "lookup",
                        ArgumentsJson = """{"query":""",
                    },
                };
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "",
                        Name = "",
                        ArgumentsJson = "\"aevatar\"}",
                    },
                };
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" };
                await Task.CompletedTask;
            }
        }

        internal sealed class ContentPartAndAnonymousToolWorkflowIntentLlmProvider : WorkflowIntentLlmProviderBase
        {
            private int _calls;

            public override async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                _ = request;
                ct.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref _calls) > 1)
                {
                    yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
                    await Task.CompletedTask;
                    yield break;
                }

                yield return new LLMStreamChunk
                {
                    DeltaContentPart = ContentPart.TextPart("part-only"),
                };
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "",
                        Name = "",
                        ArgumentsJson = "{}",
                    },
                };
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "known-1",
                        Name = "search",
                        ArgumentsJson = "",
                    },
                };
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "",
                        Name = "",
                        ArgumentsJson = "[]",
                    },
                };
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
                await Task.CompletedTask;
            }
        }

        internal sealed class RecordingActorRuntime : IActorRuntime, IActorDispatchPort
        {
            public int CreateCalls { get; private set; }
            public List<(string agentKind, string actorId)> CreateByKindCalls { get; } = [];
            public List<FakeActor> CreatedActors { get; } = [];
            public List<FakeWorkflowRunChildAgent> CreatedChildWorkflowAgents { get; } = [];
            public List<(string parent, string child)> Linked { get; } = [];
            public List<string> Destroyed { get; } = [];
            public List<string> Unlinked { get; } = [];
            public string? ThrowOnGetAsyncActorId { get; set; }
            public Exception? CreateByKindException { get; set; }

            public void RegisterAgent(string actorId, IAgent agent)
            {
                CreatedActors.RemoveAll(x => string.Equals(x.Id, actorId, StringComparison.Ordinal));
                CreatedActors.Add(new FakeActor(actorId, agent));
            }

            public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
            {
                return CreateAsync(typeof(TAgent), id, ct);
            }

            public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
            {
                var actorId = id ?? $"actor-{CreateCalls + 1}";
                var existing = CreatedActors.FirstOrDefault(x => x.Id == actorId);
                if (existing != null)
                    return Task.FromResult<IActor>(existing);

                CreateCalls++;
                IAgent agent = agentType == typeof(FakeRoleAgent)
                    ? new FakeRoleAgent(actorId)
                    : agentType == typeof(FakeNonRoleAgent)
                        ? new FakeNonRoleAgent(actorId)
                        : agentType == typeof(WorkflowRunGAgent)
                            ? CreateChildWorkflowRunAgent(actorId)
                            : throw new InvalidOperationException($"Unsupported agent type '{agentType.FullName}'.");

                var actor = new FakeActor(actorId, agent);
                CreatedActors.Add(actor);
                return Task.FromResult<IActor>(actor);
            }

            public Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
            {
                var actorId = id ?? $"{agentKind}:actor-{CreateByKindCalls.Count + 1}";
                CreateByKindCalls.Add((agentKind.Trim(), actorId));
                if (CreateByKindException != null)
                    throw CreateByKindException;

                var existing = CreatedActors.FirstOrDefault(x => x.Id == actorId);
                if (existing != null)
                    return Task.FromResult<IActor>(existing);

                var actor = new FakeActor(actorId, new FakeRoleAgent(actorId));
                CreatedActors.Add(actor);
                return Task.FromResult<IActor>(actor);
            }

            public Task DestroyAsync(string id, CancellationToken ct = default)
            {
                Destroyed.Add(id);
                CreatedActors.RemoveAll(x => string.Equals(x.Id, id, StringComparison.Ordinal));
                return Task.CompletedTask;
            }

            public Task<IActor?> GetAsync(string id) =>
                string.Equals(id, ThrowOnGetAsyncActorId, StringComparison.Ordinal)
                    ? throw new InvalidOperationException($"Unexpected self GetAsync for actor '{id}'.")
                    : Task.FromResult<IActor?>(CreatedActors.FirstOrDefault(x => x.Id == id));

            public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                var actor = CreatedActors.FirstOrDefault(x => x.Id == actorId)
                            ?? throw new InvalidOperationException($"Actor {actorId} not found.");
                await actor.HandleEventAsync(envelope, ct);
                return DispatchAdmissionFactory.Create(actorId, envelope);
            }

            public Task<bool> ExistsAsync(string id) =>
                Task.FromResult(CreatedActors.Any(x => x.Id == id));

            public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
            {
                Linked.Add((parentId, childId));
                return Task.CompletedTask;
            }

            public Task UnlinkAsync(string childId, CancellationToken ct = default)
            {
                Unlinked.Add(childId);
                return Task.CompletedTask;
            }

            private FakeWorkflowRunChildAgent CreateChildWorkflowRunAgent(string actorId)
            {
                var child = new FakeWorkflowRunChildAgent(actorId);
                CreatedChildWorkflowAgents.Add(child);
                return child;
            }
        }

        internal sealed class FakeActor(string id, IAgent agent) : IActor
        {
            public string Id { get; } = id;
            public IAgent Agent { get; } = agent;

            public Task ActivateAsync(CancellationToken ct = default) => Agent.ActivateAsync(ct);
            public Task DeactivateAsync(CancellationToken ct = default) => Agent.DeactivateAsync(ct);
            public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Agent.HandleEventAsync(envelope, ct);
            public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
            public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        }

        internal sealed class FakeRoleAgent(string id) : IRoleAgent
        {
            public string Id { get; } = id;
            public string RoleName { get; private set; } = string.Empty;
            public WorkflowRoleInitializeEvent? LastInitializeEvent { get; private set; }

            public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
            {
                if (envelope.Payload?.Is(WorkflowRoleInitializeEvent.Descriptor) == true)
                {
                    var evt = envelope.Payload.Unpack<WorkflowRoleInitializeEvent>();
                    LastInitializeEvent = evt;
                    RoleName = evt.RoleName;
                }

                return Task.CompletedTask;
            }

            public Task<string> GetDescriptionAsync() => Task.FromResult("fake-role");
            public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
            public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        }

        internal sealed class FakeWorkflowRunChildAgent(string id) : IAgent
        {
            public string Id { get; } = id;
            public List<BindWorkflowRunDefinitionEvent> BindEvents { get; } = [];
            public List<StartWorkflowEvent> StartEvents { get; } = [];

            public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
            {
                if (envelope.Payload?.Is(BindWorkflowRunDefinitionEvent.Descriptor) == true)
                    BindEvents.Add(envelope.Payload.Unpack<BindWorkflowRunDefinitionEvent>());

                if (envelope.Payload?.Is(StartWorkflowEvent.Descriptor) == true)
                    StartEvents.Add(envelope.Payload.Unpack<StartWorkflowEvent>());

                return Task.CompletedTask;
            }

            public Task<string> GetDescriptionAsync() => Task.FromResult("fake-child-run");
            public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
            public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        }

        internal sealed class FakeNonRoleAgent(string id) : IAgent
        {
            public string Id { get; } = id;

            public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
            public Task<string> GetDescriptionAsync() => Task.FromResult("fake-non-role");
            public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
            public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        }

        internal sealed class RecordingEventModuleFactory : IEventModuleFactory<IWorkflowExecutionContext>
        {
            public List<string> CreatedNames { get; } = [];

            public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
            {
                CreatedNames.Add(name);
                module = new RecordingEventModule(name);
                return true;
            }
        }

        internal sealed class RecordingEventModule(string name) : IEventModule<IWorkflowExecutionContext>
        {
            public string Name { get; } = name;
            public int Priority => 0;
            public bool CanHandle(EventEnvelope envelope) => false;
            public Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct) => Task.CompletedTask;
        }

        internal sealed class StaticDependencyExpander(int order, params string[] moduleNames) : IWorkflowModuleDependencyExpander
        {
            public int Order { get; } = order;

            public void Expand(WorkflowDefinition? workflow, ISet<string> names)
            {
                _ = workflow;
                foreach (var moduleName in moduleNames)
                    names.Add(moduleName);
            }
        }

        internal sealed class RecordingModuleConfigurator : IWorkflowModuleConfigurator
        {
            public int Order => 0;
            public List<string> Configured { get; } = [];

            public void Configure(IEventModule<IWorkflowExecutionContext> module, WorkflowDefinition workflow)
            {
                Configured.Add($"{module.Name}:{workflow.Name}");
            }
        }

        internal sealed class TestModulePack(
            IReadOnlyList<IWorkflowModuleDependencyExpander> expanders,
            IReadOnlyList<IWorkflowModuleConfigurator> configurators) : IWorkflowModulePack
        {
            public string Name => "test-pack";
            public IReadOnlyList<WorkflowModuleRegistration> Modules => [];
            public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders { get; } = expanders;
            public IReadOnlyList<IWorkflowModuleConfigurator> Configurators { get; } = configurators;
        }
}
