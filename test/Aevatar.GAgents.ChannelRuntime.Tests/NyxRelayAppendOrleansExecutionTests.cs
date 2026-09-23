using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Telegram;
using Aevatar.Tests.Shared;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans.Hosting;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxRelayAppendOrleansExecutionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(NyxRelayAppendMessageKind.Progress, true)]
    [InlineData(NyxRelayAppendMessageKind.Content, true)]
    [InlineData(NyxRelayAppendMessageKind.Progress, false)]
    [InlineData(NyxRelayAppendMessageKind.Content, false)]
    public async Task Renderer_AfterAsynchronousPreparation_CommitsDispatchOnActorBeforeSending(
        NyxRelayAppendMessageKind kind, bool asynchronousRegistration)
    {
        var innerVault = new InMemorySecretVault();
        var stored = await innerVault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelNyxIdAgentKey, "scope-alpha", "key-alpha",
            "test-agent-key", "append-orleans-test"));
        var registration = new ChannelBotRegistrationEntry
        {
            Id = "registration-alpha", ScopeId = "scope-alpha", Platform = "telegram", NyxAgentApiKeyId = "key-alpha",
            ChannelAgentKey = new ChannelAgentKeyCredential { ApiKeyId = "key-alpha", SecretReference = stored.Reference },
        };
        var byIdentity = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        byIdentity.ListByNyxAgentApiKeyIdAsync("key-alpha", Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ChannelBotRegistrationEntry>>>(_ => asynchronousRegistration
                ? ReadRegistrationAfterYieldAsync(registration)
                : Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>([registration]));
        var vault = Substitute.For<ISecretVault>();
        vault.ResolveAsync(Arg.Any<ResolveSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ResolveSecretResult>>(call => asynchronousRegistration
                ? innerVault.ResolveAsync(call.Arg<ResolveSecretRequest>(), call.Arg<CancellationToken>())
                : ResolveSecretAfterYieldAsync(innerVault, call.Arg<ResolveSecretRequest>(), call.Arg<CancellationToken>()));
        var probe = new ExecutionProbe();
        using var handler = new RecordingHandler(probe);
        using var http = new HttpClient(handler);
        var client = new NyxIdApiClient(new NyxIdToolOptions { ApiBaseUrl = "https://nyx.example" }, http);
        var outbound = new NyxRelayAppendOutboundPort(client,
            Substitute.For<IChannelBotRegistrationRuntimeQueryPort>(), byIdentity, vault, [new TelegramMessageComposer()]);
        using var host = await SharedOrleansPortAllocator.StartHostAsync(ports => Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .UseOrleans(silo =>
            {
                silo.UseLocalhostClustering(siloPort: ports.SiloPort, gatewayPort: ports.GatewayPort,
                    serviceId: $"append-execution-service-{Guid.NewGuid():N}",
                    clusterId: $"append-execution-cluster-{Guid.NewGuid():N}");
                silo.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
                silo.ConfigureServices(services =>
                {
                    services.AddAevatarAgentKindRegistry(builder => builder.Register<AppendExecutionAgent>());
                    services.AddSingleton(probe);
                    services.AddSingleton<INyxRelayAppendOutboundPort>(outbound);
                });
            }).Build(), TestTimeout);
        try
        {
            var actor = await host.Services.GetRequiredService<IActorRuntime>()
                .CreateAsync<AppendExecutionAgent>($"append-execution-{Guid.NewGuid():N}");
            var step = new ReplyOperationStepEvent
            {
                CorrelationId = "correlation-alpha",
                NyxRelayAppend = new NyxRelayAppendOperationStepPayload
                {
                    Kind = kind, SegmentIndex = kind == NyxRelayAppendMessageKind.Progress ? 0 : 1,
                    OperationGeneration = 1,
                },
            };
            await host.Services.GetRequiredService<IActorDispatchPort>().DispatchAsync(actor.Id, new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"), Payload = Any.Pack(step),
                Route = EnvelopeRouteSemantics.CreateDirect("append-test", actor.Id),
            });

            var completion = await probe.Completion.Task.WaitAsync(TestTimeout);

            completion.State.Should().Be(NyxRelayTextOperationResultState.Succeeded,
                "the dispatch commit must finish before HTTP; raw error was {0}", completion.RawResult.RawErrorCode);
            completion.Kind.Should().Be(kind);
            completion.RequestDispatched.Should().BeTrue();
            completion.RawResult.PlatformMessageId.Should().Be("platform-alpha");
            probe.DispatchScheduler.Should().BeSameAs(probe.ActorScheduler);
            probe.DispatchCommitted.Should().BeTrue();
            handler.Count.Should().Be(1);
            handler.DispatchCommittedAtSend.Should().BeTrue();
        }
        finally
        {
            await host.StopAsync().WaitAsync(TestTimeout);
        }
    }

    private static async Task<IReadOnlyList<ChannelBotRegistrationEntry>> ReadRegistrationAfterYieldAsync(
        ChannelBotRegistrationEntry registration)
    {
        // Yield on the activation scheduler so the caller must await an incomplete task.
        await Task.Yield();
        return [registration];
    }

    private static async Task<ResolveSecretResult> ResolveSecretAfterYieldAsync(
        ISecretVault vault, ResolveSecretRequest request, CancellationToken ct)
    {
        await Task.Yield();
        return await vault.ResolveAsync(request, ct);
    }

    private sealed class ExecutionProbe
    {
        public TaskCompletionSource<NyxRelayAppendOperationCompletedEvent> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskScheduler? ActorScheduler { get; set; }
        public TaskScheduler? DispatchScheduler { get; set; }
        public bool DispatchCommitted { get; set; }
    }

    [GAgent("tests.append-orleans-execution")]
    public sealed class AppendExecutionAgent : GAgentBase<Int32Value>, IReplyOperationActorContext,
        INyxRelayAppendOperationActorContext
    {
        [EventHandler]
        public async Task HandleAppendAsync(ReplyOperationStepEvent step)
        {
            var probe = Services.GetRequiredService<ExecutionProbe>();
            probe.ActorScheduler = TaskScheduler.Current;
            try
            {
                await PersistDomainEventAsync(new StringValue { Value = "operation-claimed" });
                var renderer = new NyxRelayAppendReplyStreamRenderer(
                    Services.GetRequiredService<INyxRelayAppendOutboundPort>(), TimeProvider.System, TestTimeout);
                await renderer.ExecuteAsync(this, step, CancellationToken.None);
            }
            catch (Exception error)
            {
                probe.Completion.TrySetException(error);
            }
        }

        Task<NyxRelayAppendExecution?> INyxRelayAppendOperationActorContext.ClaimAppendOperationAsync(
            string correlationId, NyxRelayAppendOperationStepPayload step, CancellationToken ct) =>
            Task.FromResult<NyxRelayAppendExecution?>(new(new ChatActivity
            {
                ChannelId = ChannelId.From("telegram"), Bot = BotInstanceId.From("registration-alpha"),
                Conversation = ConversationReference.Create(ChannelId.From("telegram"), BotInstanceId.From("registration-alpha"),
                    ConversationScope.DirectMessage, "conversation-alpha", "dm", "sender-alpha"),
                OutboundDelivery = new OutboundDeliveryContext { ReplyMessageId = "inbound-alpha", CorrelationId = correlationId },
                TransportExtras = new TransportExtras
                {
                    NyxPlatform = "telegram", NyxAgentApiKeyId = "key-alpha", NyxRegistrationScopeId = "scope-alpha",
                },
            }, step.Kind == NyxRelayAppendMessageKind.Progress ? "正在处理，请稍候..." : "The final answer.", 4096));

        async Task INyxRelayAppendOperationActorContext.MarkAppendDispatchedAsync(
            string correlationId, NyxRelayAppendOperationStepPayload step, CancellationToken ct)
        {
            var probe = Services.GetRequiredService<ExecutionProbe>();
            probe.DispatchScheduler = TaskScheduler.Current;
            await PersistDomainEventAsync(new StringValue { Value = "dispatch-fenced" }, ct);
            probe.DispatchCommitted = true;
        }

        public Task DispatchReplyOperationCompletionAsync(
            IMessage evt, string correlationId, string operationName, CancellationToken ct)
        {
            Services.GetRequiredService<ExecutionProbe>().Completion.TrySetResult(
                ((NyxRelayAppendOperationCompletedEvent)evt).Clone());
            return Task.CompletedTask;
        }

        protected override Int32Value TransitionState(Int32Value current, IMessage evt) =>
            evt is StringValue ? new Int32Value { Value = current.Value + 1 } : current;

        public bool MatchesNyxRelayTextInFlight(string correlationId, NyxRelayTextOperationKind operation,
            long sequence, long generation) => false;
        public bool MatchesLarkCardInFlight(string correlationId, LarkCardOperationPhase operation,
            long sequence, long generation, string? cardId) => false;
        public ConversationTurnRuntimeContext BuildNyxRelayRuntimeContext(string? correlationId, ChatActivity? activity,
            string? replyToken, long replyTokenExpiresAtUnixMs) => ConversationTurnRuntimeContext.Empty;
        public void RestoreRuntimeTransportCredentials(ChatActivity? activity, ConversationTurnRuntimeContext runtimeContext) { }
    }

    private sealed class RecordingHandler(ExecutionProbe probe) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public bool DispatchCommittedAtSend { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Count++;
            DispatchCommittedAtSend = probe.DispatchCommitted;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message_id\":\"reply-alpha\",\"platform_message_id\":\"platform-alpha\"}",
                    Encoding.UTF8, "application/json"),
            });
        }
    }
}
