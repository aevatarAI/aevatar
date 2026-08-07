using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Tests.Shared;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;

namespace Aevatar.Integration.Tests;

public sealed class WorkflowRoleGAgentOrleansStreamingIntegrationTests
{
    [Fact]
    public async Task CheckpointRecovery_ShouldRetainActivationContext_AfterDurableCredentialResolution()
    {
        var roleActorId = $"workflow-role-recovery-{Guid.NewGuid():N}";
        var provider = new YieldingLlmProvider(failAfterTool: false);
        var tool = new ReadOnlyLookupTool();
        var eventStore = new FailOnceToolCompletionCheckpointEventStore();
        var vault = new YieldingSecretVault();
        var credential = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
            "schedule:activation-recovery",
            "subject-activation-recovery",
            "synthetic-token",
            "seed activation recovery test",
            DateTimeOffset.UtcNow.AddMinutes(5),
            "credential-activation-recovery"));
        var host = await StartSiloHostAsync(provider, tool, vault, eventStore);

        try
        {
            var grain = host.Services
                .GetRequiredService<IGrainFactory>()
                .GetGrain<IRuntimeActorGrain>(roleActorId);
            (await grain.InitializeAgentByKindAsync(WorkflowRoleConventions.DefaultAgentKind))
                .Should()
                .BeTrue();

            await grain.HandleEnvelopeAsync(BuildEnvelope(
                roleActorId,
                new WorkflowRoleInitializeEvent
                {
                    RoleId = "assistant",
                    RoleName = "Assistant",
                    ProviderName = provider.Name,
                    SystemPrompt = "Reply to the recovered workflow turn.",
                }));

            var completionSource =
                new TaskCompletionSource<WorkflowLlmInvocationCompletedEvent>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = host.Services
                .GetRequiredService<IStreamProvider>()
                .GetStream(roleActorId);
            await using var subscription = await stream.SubscribeAsync<EventEnvelope>(envelope =>
            {
                if (envelope.Payload?.Is(WorkflowLlmInvocationCompletedEvent.Descriptor) == true)
                {
                    completionSource.TrySetResult(
                        envelope.Payload.Unpack<WorkflowLlmInvocationCompletedEvent>());
                }

                return Task.CompletedTask;
            });

            await grain.HandleEnvelopeAsync(BuildEnvelope(
                roleActorId,
                new WorkflowLlmExecutionIntent
                {
                    RunId = "run-recovery",
                    StepId = "step-recovery",
                    SessionId = "session-recovery",
                    Prompt = "hello",
                    SenderNyxIdAccessToken = "synthetic-sender-token",
                    CallerCredential = new WorkflowCallerCredential
                    {
                        DurableCallerCredential = new DurableCallerCredentialRef
                        {
                            Ref = credential.Reference.Ref,
                            Purpose = credential.Reference.Purpose,
                            OwnerScopeKey = credential.Reference.OwnerScopeKey,
                            SubjectId = "subject-activation-recovery",
                            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
                        },
                    },
                }));

            var completion = await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(20));
            completion.Success.Should().BeTrue();
            completion.Content.Should().Be("streamed response");
            provider.CallCount.Should().Be(2);
            tool.CallCount.Should().Be(1);
            eventStore.InjectedFailureCount.Should().Be(1);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Theory]
    [InlineData(false, true, "streamed response", "")]
    [InlineData(true, false, "", "llm_request_failed: provider failed after tool")]
    public async Task StreamingTurn_ShouldRetainActivationContext_ThroughToolCheckpointAndTerminal(
        bool failAfterTool,
        bool expectedSuccess,
        string expectedContent,
        string expectedError)
    {
        var roleActorId = $"workflow-role-{Guid.NewGuid():N}";
        var provider = new YieldingLlmProvider(failAfterTool);
        var tool = new ReadOnlyLookupTool();
        var host = await StartSiloHostAsync(provider, tool);

        try
        {
            var grain = host.Services
                .GetRequiredService<IGrainFactory>()
                .GetGrain<IRuntimeActorGrain>(roleActorId);
            (await grain.InitializeAgentByKindAsync(WorkflowRoleConventions.DefaultAgentKind))
                .Should()
                .BeTrue();

            await grain.HandleEnvelopeAsync(BuildEnvelope(
                roleActorId,
                new WorkflowRoleInitializeEvent
                {
                    RoleId = "assistant",
                    RoleName = "Assistant",
                    ProviderName = provider.Name,
                    SystemPrompt = "Reply to the workflow turn.",
                }));

            var completionSource =
                new TaskCompletionSource<WorkflowLlmInvocationCompletedEvent>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = host.Services
                .GetRequiredService<IStreamProvider>()
                .GetStream(roleActorId);
            await using var subscription = await stream.SubscribeAsync<EventEnvelope>(envelope =>
            {
                if (envelope.Payload?.Is(WorkflowLlmInvocationCompletedEvent.Descriptor) == true)
                {
                    completionSource.TrySetResult(
                        envelope.Payload.Unpack<WorkflowLlmInvocationCompletedEvent>());
                }

                return Task.CompletedTask;
            });

            await grain.HandleEnvelopeAsync(BuildEnvelope(
                roleActorId,
                new WorkflowLlmExecutionIntent
                {
                    RunId = "run-alpha",
                    StepId = "step-alpha",
                    SessionId = "session-alpha",
                    Prompt = "hello",
                }));

            var completion = await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(20));
            completion.Success.Should().Be(expectedSuccess);
            completion.Content.Should().Be(expectedContent);
            completion.Error.Should().Be(expectedError);
            provider.CallCount.Should().Be(2);
            tool.CallCount.Should().Be(1);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static Task<IHost> StartSiloHostAsync(
        YieldingLlmProvider provider,
        ReadOnlyLookupTool tool,
        ISecretVault? secretVault = null,
        IEventStore? eventStore = null) =>
        SharedOrleansPortAllocator.StartHostAsync(ports => Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    ports.SiloPort,
                    ports.GatewayPort,
                    serviceId: $"aevatar-workflow-role-stream-service-{Guid.NewGuid():N}",
                    clusterId: $"aevatar-workflow-role-stream-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
                siloBuilder.ConfigureServices(services =>
                    services.AddAevatarAgentKindRegistry(builder => builder
                        .Register<WorkflowRoleGAgent>()));
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<ILLMProviderFactory>(provider);
                services.AddSingleton(secretVault ?? new YieldingSecretVault());
                if (eventStore is not null)
                    services.AddSingleton(eventStore);
                services.AddSingleton<IAgentToolSource>(new StaticToolSource(tool));
                services.AddSingleton<IAgentToolExecutionPort, ExecutingAgentToolExecutionPort>();
            })
            .Build());

    private static byte[] BuildEnvelope(string targetActorId, IMessage payload) =>
        new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("test", targetActorId),
            Runtime = new EnvelopeRuntime
            {
                Dispatch = new EnvelopeDispatchControl { PropagateFailure = true },
            },
        }.ToByteArray();

    private sealed class YieldingLlmProvider(bool failAfterTool) : ILLMProviderFactory, ILLMProvider
    {
        private int _callCount;

        public string Name => "yielding";
        public int CallCount => Volatile.Read(ref _callCount);

        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            var call = Interlocked.Increment(ref _callCount);
            await CompleteOnThreadPoolAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (call == 1)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call-lookup",
                        Name = "lookup",
                        ArgumentsJson = "{}",
                    },
                };
                yield return new LLMStreamChunk { IsLast = true, FinishReason = "tool_calls" };
                yield break;
            }

            if (failAfterTool)
                throw new InvalidOperationException("provider failed after tool");

            yield return new LLMStreamChunk { DeltaContent = "streamed " };
            await CompleteOnThreadPoolAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk { DeltaContent = "response" };
            yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
        }
    }

    private sealed class StaticToolSource(IAgentTool tool) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
        }
    }

    private sealed class ReadOnlyLookupTool : IAgentTool
    {
        private int _callCount;

        public string Name => "lookup";
        public string Description => "Looks up a test value.";
        public string ParametersSchema => "{\"type\":\"object\"}";
        public bool IsReadOnly => true;
        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<string> ExecuteAsync(
            string argumentsJson,
            CancellationToken ct = default)
        {
            _ = argumentsJson;
            await CompleteOnThreadPoolAsync(ct).ConfigureAwait(false);
            Interlocked.Increment(ref _callCount);
            return "{\"value\":\"found\"}";
        }
    }

    private sealed class ExecutingAgentToolExecutionPort : IAgentToolExecutionPort
    {

        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            var result = await request.Tool
                .ExecuteAsync(request.ArgumentsJson, ct)
                .ConfigureAwait(false);
            var receipt = new AgentToolReceipt
            {
                CallId = request.ExecutionContext.Request.CallId,
                ToolName = request.Tool.Name,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = result,
            };
            return new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                result,
                receipt,
                IsMutation: false,
                FailureCode: string.Empty,
                SafeMessage: string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: true,
                Retryable: false,
                AuditCompleted: true);
        }
    }

    private sealed class YieldingSecretVault : ISecretVault
    {
        private readonly InMemorySecretVault _inner = new();

        public async Task<StoreSecretResult> PutAsync(
            StoreSecretRequest request,
            CancellationToken ct = default)
        {
            await CompleteOnThreadPoolAsync(ct).ConfigureAwait(false);
            return await _inner.PutAsync(request, ct).ConfigureAwait(false);
        }

        public async Task<ResolveSecretResult> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken ct = default)
        {
            await CompleteOnThreadPoolAsync(ct).ConfigureAwait(false);
            return await _inner.ResolveAsync(request, ct).ConfigureAwait(false);
        }

        public Task<RotateSecretResult> RotateAsync(
            RotateSecretRequest request,
            CancellationToken ct = default) =>
            _inner.RotateAsync(request, ct);

        public Task<RevokeSecretResult> RevokeAsync(
            RevokeSecretRequest request,
            CancellationToken ct = default) =>
            _inner.RevokeAsync(request, ct);
    }

    private sealed class FailOnceToolCompletionCheckpointEventStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        private int _injectedFailureCount;

        public int InjectedFailureCount => Volatile.Read(ref _injectedFailureCount);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.Select(static stateEvent => stateEvent.Clone()).ToArray();
            if (batch.Any(stateEvent =>
                    stateEvent.EventData.Is(RoleChatRecoveryCheckpointUpdatedEvent.Descriptor) &&
                    stateEvent.EventData.Unpack<RoleChatRecoveryCheckpointUpdatedEvent>()
                        .Checkpoint.ToolCompletions.Count > 0) &&
                Interlocked.CompareExchange(ref _injectedFailureCount, 1, 0) == 0)
            {
                throw new InvalidOperationException("completion checkpoint append failed");
            }

            return _inner.AppendAsync(agentId, batch, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            _inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            _inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            _inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private static Task CompleteOnThreadPoolAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.QueueUserWorkItem(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            completion);
        return completion.Task.WaitAsync(ct);
    }
}
