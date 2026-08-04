using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgentService.Core.GAgents;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Tests.TestSupport;

internal static class GAgentServiceTestKit
{
    public const string TestStaticServiceAgentKind = "tests.static-service-agent";
    public static IActorDispatchPort NoOpDispatchPort { get; } = new NoOpActorDispatchPort();

    private static readonly MethodInfo SetIdMethod = typeof(GAgentBase)
        .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GAgentBase.SetId was not found.");

    public static ServiceIdentity CreateIdentity(string serviceId = "svc") =>
        new()
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = serviceId,
        };

    public static ServiceEndpointSpec CreateEndpointSpec(
        string endpointId = "run",
        ServiceEndpointKind kind = ServiceEndpointKind.Command,
        string requestTypeUrl = "type.googleapis.com/test.command") =>
        new()
        {
            EndpointId = endpointId,
            DisplayName = endpointId,
            Kind = kind,
            RequestTypeUrl = requestTypeUrl,
        };

    public static ServiceEndpointDescriptor CreateEndpointDescriptor(
        string endpointId = "run",
        ServiceEndpointKind kind = ServiceEndpointKind.Command,
        string requestTypeUrl = "type.googleapis.com/test.command") =>
        new()
        {
            EndpointId = endpointId,
            DisplayName = endpointId,
            Kind = kind,
            RequestTypeUrl = requestTypeUrl,
        };

    public static ServiceDefinitionSpec CreateDefinitionSpec(
        ServiceIdentity? identity = null,
        params ServiceEndpointSpec[] endpoints)
    {
        var spec = new ServiceDefinitionSpec
        {
            Identity = (identity ?? CreateIdentity()).Clone(),
            DisplayName = "Service",
        };
        spec.Endpoints.Add((endpoints.Length == 0
            ? [CreateEndpointSpec()]
            : endpoints).Select(x => x.Clone()));
        return spec;
    }

    public static ServiceRevisionSpec CreateStaticRevisionSpec(
        ServiceIdentity? identity = null,
        string revisionId = "r1",
        string? actorTypeName = null,
        string? agentKind = null,
        params ServiceEndpointDescriptor[] endpoints)
    {
        var spec = new ServiceRevisionSpec
        {
            Identity = (identity ?? CreateIdentity()).Clone(),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Static,
            StaticSpec = new StaticServiceRevisionSpec
            {
                ActorTypeName = actorTypeName ?? typeof(TestStaticServiceAgent).AssemblyQualifiedName!,
                AgentKind = agentKind ?? TestStaticServiceAgentKind,
                PreferredActorId = $"static:{revisionId}",
            },
        };
        spec.StaticSpec.Endpoints.Add((endpoints.Length == 0
            ? [CreateEndpointDescriptor()]
            : endpoints).Select(x => x.Clone()));
        return spec;
    }

    public static PreparedServiceRevisionArtifact CreatePreparedStaticArtifact(
        ServiceIdentity? identity = null,
        string revisionId = "r1",
        params ServiceEndpointDescriptor[] endpoints)
    {
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = (identity ?? CreateIdentity()).Clone(),
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Static,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                StaticPlan = new StaticServiceDeploymentPlan
                {
                    ActorTypeName = typeof(TestStaticServiceAgent).AssemblyQualifiedName!,
                    AgentKind = TestStaticServiceAgentKind,
                    PreferredActorId = $"static:{revisionId}",
                },
            },
        };
        artifact.Endpoints.Add((endpoints.Length == 0
            ? [CreateEndpointDescriptor()]
            : endpoints).Select(x => x.Clone()));
        return artifact;
    }

    public static TAgent CreateStatefulAgent<TAgent, TState>(
        InMemoryEventStore eventStore,
        string actorId,
        Func<TAgent> factory,
        Action<IServiceCollection>? configureServices = null)
        where TAgent : GAgentBase<TState>
        where TState : class, IMessage<TState>, new()
    {
        TestBoundLlmRunExecutionScheduler? boundLlmRunExecutionScheduler = null;
        if (typeof(TAgent) == typeof(LlmSessionGAgent))
        {
            boundLlmRunExecutionScheduler = new TestBoundLlmRunExecutionScheduler();
            factory = () => (TAgent)(object)new LlmSessionGAgent(boundLlmRunExecutionScheduler);
        }

        var agent = factory();
        AssignActorId(agent, actorId);
        agent.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<TState>(eventStore);
        var services = new ServiceCollection()
            .AddSingleton<IStreamProvider, InMemoryStreamProvider>()
            .AddSingleton<InMemoryActorRuntimeCallbackScheduler>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(sp =>
                sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>())
            .AddSingleton<IAgentToolExecutionPort, RecordingAgentToolExecutionPort>()
            .AddSingleton<ILogger<LlmRunCore>>(NullLogger<LlmRunCore>.Instance)
            .AddSingleton<IEnumerable<IGAgentExecutionHook>>(Array.Empty<IGAgentExecutionHook>());
        configureServices?.Invoke(services);
        if (agent is LlmSessionGAgent llmSession)
        {
            services.TryAddSingleton<ILlmRunCore>(sp =>
            {
                var providerFactory = sp.GetService<ILLMProviderFactory>();
                return providerFactory is null
                    ? new MissingLlmProviderRunCore()
                    : new LlmRunCore(
                        providerFactory,
                        sp.GetServices<IResponsesToolProvider>(),
                        sp.GetService<IToolSetRegistry>() ?? TestToolSetRegistry.Empty,
                        sp.GetRequiredService<ILogger<LlmRunCore>>(),
                        sp.GetRequiredService<IAgentToolExecutionPort>());
            });
            services.TryAddSingleton<InlineLlmRunExecutor>(sp => new InlineLlmRunExecutor(
                sp.GetRequiredService<ILlmRunCore>(),
                llmSession));
            services.TryAddSingleton<ILlmRunExecutor>(sp => sp.GetRequiredService<InlineLlmRunExecutor>());
            services.TryAddSingleton<ILlmRunExecutionService>(sp =>
                sp.GetService<ILlmRunExecutor>() as ILlmRunExecutionService ??
                sp.GetRequiredService<InlineLlmRunExecutor>());
            services.TryAddSingleton<ILlmRunExecutionScheduler>(sp =>
                new InlineLlmRunExecutionScheduler(sp.GetRequiredService<ILlmRunExecutionService>()));
        }
        var serviceProvider = services.BuildServiceProvider();
        boundLlmRunExecutionScheduler?.Bind(() => serviceProvider.GetRequiredService<ILlmRunExecutionScheduler>());
        agent.Services = serviceProvider;
        return agent;
    }

    public static void AssignActorId(IAgent agent, string actorId)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        SetIdMethod.Invoke(agent, [actorId]);
    }

    private sealed class NoOpActorDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default) =>
            Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }

    private sealed class InlineLlmRunExecutor(
        ILlmRunCore runCore,
        LlmSessionGAgent actor) : ILlmRunExecutor, ILlmRunExecutionService
    {
        public Task<DispatchAdmission> StartAsync(
            LlmRunExecutorRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(DispatchAdmissionFactory.Create(
                request.SessionActorId,
                new EventEnvelope
                {
                    Id = $"start-{request.ResponseId}",
                    Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new RecordLlmRunStarted
                    {
                        Command = request.Command.Clone(),
                        StartedAt = request.Command.RequestedAt?.Clone(),
                    }),
                }));

        public Task ExecuteAsync(
            LlmRunExecutionRequest request,
            CancellationToken ct = default) =>
            runCore.RunAsync(
                new LlmRunCoreRequest(request.Command.Clone(), request.RunId, request.OriginPlatform),
                new InlineLlmRunSink(actor, request.RunId),
                ct);
    }

    private sealed class InlineLlmRunExecutionScheduler(
        ILlmRunExecutionService executionService) : ILlmRunExecutionScheduler
    {
        public async ValueTask ScheduleAsync(
            LlmRunExecutionRequest request,
            CancellationToken ct = default) =>
            await executionService.ExecuteAsync(request, ct).ConfigureAwait(false);
    }

    private sealed class TestBoundLlmRunExecutionScheduler : ILlmRunExecutionScheduler
    {
        private Func<ILlmRunExecutionScheduler>? _resolve;

        public void Bind(Func<ILlmRunExecutionScheduler> resolve) =>
            _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));

        public ValueTask ScheduleAsync(
            LlmRunExecutionRequest request,
            CancellationToken ct = default)
        {
            if (_resolve is null)
                throw new InvalidOperationException("LLM run execution scheduler has not been bound.");

            return _resolve().ScheduleAsync(request, ct);
        }
    }

    private sealed class MissingLlmProviderRunCore : ILlmRunCore
    {
        public Task RunAsync(
            LlmRunCoreRequest request,
            ILlmRunSink sink,
            CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "Tests that execute an LLM run must register ILLMProviderFactory or ILlmRunCore.");
    }

    private sealed class InlineLlmRunSink(
        LlmSessionGAgent actor,
        string runId) : ILlmRunSink
    {
        private long _recordIndex;

        public async Task<LlmRunRecordDecision> RecordStreamChunkObservedAsync(
            LlmStreamChunkObserved observed,
            CancellationToken ct = default)
        {
            _ = ct;
            var recordId = NextRecordId("chunk");
            await actor.HandleRecordStreamChunkObservedAsync(new RecordLlmStreamChunkObserved
            {
                ResponseId = observed.ResponseId,
                RunId = ResolveRunId(observed.RunId),
                RecordId = recordId,
                Round = observed.Round,
                DeltaText = observed.DeltaText ?? string.Empty,
                ToolCallDelta = observed.ToolCallDelta?.Clone(),
                Usage = observed.Usage?.Clone(),
                ObservedAt = observed.ObservedAt?.Clone(),
            }).ConfigureAwait(false);
            return LlmRunRecordDecision.Continue;
        }

        public async Task<LlmRunRecordDecision> RecordToolCallObservedAsync(
            LlmToolCallObserved observed,
            CancellationToken ct = default)
        {
            _ = ct;
            var recordId = NextRecordId("tool");
            await actor.HandleRecordToolCallObservedAsync(new RecordLlmToolCallObserved
            {
                ResponseId = observed.ResponseId,
                RunId = ResolveRunId(observed.RunId),
                RecordId = recordId,
                Round = observed.Round,
                ToolCall = observed.ToolCall?.Clone(),
                Forwarded = observed.Forwarded,
                LocalResultJson = observed.LocalResultJson ?? string.Empty,
                ObservedAt = observed.ObservedAt?.Clone(),
                LocalResult = observed.LocalResult?.Clone(),
            }).ConfigureAwait(false);
            return LlmRunRecordDecision.Continue;
        }

        public async Task<LlmRunRecordDecision> RecordRunCompletedAsync(
            LlmRunCompleted completed,
            CancellationToken ct = default)
        {
            _ = ct;
            var recordId = NextRecordId("completed");
            var command = new RecordLlmRunCompleted
            {
                ResponseId = completed.ResponseId,
                RunId = ResolveRunId(completed.RunId),
                RecordId = recordId,
                OutputText = completed.OutputText ?? string.Empty,
                Usage = completed.Usage?.Clone(),
                CompletedAt = completed.CompletedAt?.Clone(),
            };
            command.ForwardedToolCalls.AddRange(completed.ForwardedToolCalls.Select(static call => call.Clone()));
            command.ForwardedToolCallRecords.AddRange(completed.ForwardedToolCallRecords.Select(static call => call.Clone()));
            await actor.HandleRecordRunCompletedAsync(command).ConfigureAwait(false);
            return LlmRunRecordDecision.Continue;
        }

        public async Task<LlmRunRecordDecision> RecordRunFailedAsync(
            LlmRunFailed failed,
            CancellationToken ct = default)
        {
            _ = ct;
            var recordId = NextRecordId("failed");
            await actor.HandleRecordRunFailedAsync(new RecordLlmRunFailed
            {
                ResponseId = failed.ResponseId,
                RunId = ResolveRunId(failed.RunId),
                RecordId = recordId,
                FailureCode = failed.FailureCode ?? string.Empty,
                FailureMessage = failed.FailureMessage ?? string.Empty,
                FailedAt = failed.FailedAt?.Clone(),
            }).ConfigureAwait(false);
            return LlmRunRecordDecision.Continue;
        }

        public async Task<LlmRunRecordDecision> RecordRunCancelledAsync(
            LlmRunCancelled cancelled,
            CancellationToken ct = default)
        {
            _ = ct;
            var recordId = NextRecordId("cancelled");
            await actor.HandleRecordRunCancelledAsync(new RecordLlmRunCancelled
            {
                ResponseId = cancelled.ResponseId,
                RunId = ResolveRunId(cancelled.RunId),
                RecordId = recordId,
                CancelledAt = cancelled.CancelledAt?.Clone(),
            }).ConfigureAwait(false);
            return LlmRunRecordDecision.Continue;
        }

        private string NextRecordId(string kind)
        {
            var index = Interlocked.Increment(ref _recordIndex);
            return $"{runId}:{kind}:{index}";
        }

        private string ResolveRunId(string? candidate) =>
            string.IsNullOrWhiteSpace(candidate) ? runId : candidate.Trim();
    }
}

internal sealed class RecordingAgentToolExecutionPort : IAgentToolExecutionPort
{
    public List<AgentToolExecutionRequest> Requests { get; } = [];

    public async Task<AgentToolExecutionOutcome> ExecuteAsync(
        AgentToolExecutionRequest request,
        CancellationToken ct = default)
    {
        Requests.Add(request);
        try
        {
            var resultJson = await request.Tool.ExecuteAsync(request.ArgumentsJson, ct);
            return CreateOutcome(
                request,
                AgentToolExecutionOutcomeKind.Executed,
                AgentToolReceiptStatus.Success,
                resultJson,
                string.Empty,
                string.Empty);
        }
        catch (Exception)
        {
            const string failureCode = "aevatar_local_tool_execution_failed";
            var resultJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                error = new
                {
                    code = failureCode,
                    tool_name = request.Tool.Name,
                    message = "Local tool execution failed.",
                },
            });
            return CreateOutcome(
                request,
                AgentToolExecutionOutcomeKind.Failed,
                AgentToolReceiptStatus.Error,
                resultJson,
                failureCode,
                "Local tool execution failed.");
        }
    }

    private static AgentToolExecutionOutcome CreateOutcome(
        AgentToolExecutionRequest request,
        AgentToolExecutionOutcomeKind kind,
        AgentToolReceiptStatus status,
        string resultJson,
        string failureCode,
        string safeMessage) =>
        new(
            kind,
            resultJson,
            new AgentToolReceipt
            {
                CallId = request.ExecutionContext.Request.CallId,
                ToolName = request.Tool.Name,
                Status = status,
                ResultJson = resultJson,
                ErrorCode = failureCode,
                ErrorMessage = safeMessage,
            },
            IsMutation: false,
            failureCode,
            safeMessage,
            kind == AgentToolExecutionOutcomeKind.Executed
                ? AgentToolExecutionFailureStage.None
                : AgentToolExecutionFailureStage.TerminalExecution,
            TerminalInvoked: true,
            Retryable: false,
            AuditCompleted: true);
}

internal sealed class RecordingActorDispatchPort : IActorDispatchPort
{
    public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

    public Task<DispatchAdmission> DispatchAsync(
        string actorId,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        Calls.Add((actorId, envelope));
        return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }
}

[GAgent(GAgentServiceTestKit.TestStaticServiceAgentKind)]
internal sealed class TestStaticServiceAgent : IAgent
{
    public string Id { get; private set; } = Guid.NewGuid().ToString("N");

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult("test-static-service-agent");

    public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<Type>>([]);

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}
