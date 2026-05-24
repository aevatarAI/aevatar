using System.Net;
using System.Net.Sockets;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeCallbackSchedulerGrainCredentialGuardIntegrationTests
{
    [Fact]
    public async Task ScheduleTimeoutAsync_ShouldPersistSanitizedLarkCardTimeoutAndRejectRuntimeCredential()
    {
        var host = await StartSiloHostAsync();

        try
        {
            var grain = ResolveSchedulerGrain(host, "credential-guard-lark-card");
            var sanitized = CreateEnvelope("evt-lark-sanitized", CreateLarkCardTimeout(nyxUserAccessToken: string.Empty));
            var unsanitized = CreateEnvelope("evt-lark-unsanitized", CreateLarkCardTimeout(nyxUserAccessToken: "runtime-user-token"));

            var generation = await grain.ScheduleTimeoutAsync("lark-card-sanitized", sanitized, dueTimeMs: 60000);
            generation.Should().Be(1);

            var act = () => grain.ScheduleTimeoutAsync("lark-card-unsanitized", unsanitized, dueTimeMs: 60000);

            await act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*nyx_user_access_token*");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ScheduleTimeoutAsync_ShouldPersistSanitizedNyxRelayTextTimeoutAndRejectRuntimeCredential()
    {
        var host = await StartSiloHostAsync();

        try
        {
            var grain = ResolveSchedulerGrain(host, "credential-guard-nyx-relay");
            var sanitized = CreateEnvelope(
                "evt-nyx-relay-sanitized",
                CreateNyxRelayTimeout(replyToken: string.Empty, replyTokenExpiresAtUnixMs: 0));
            var unsanitized = CreateEnvelope(
                "evt-nyx-relay-unsanitized",
                CreateNyxRelayTimeout(
                    replyToken: "runtime-reply-token",
                    replyTokenExpiresAtUnixMs: DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds()));

            var generation = await grain.ScheduleTimeoutAsync("nyx-relay-sanitized", sanitized, dueTimeMs: 60000);
            generation.Should().Be(1);

            var act = () => grain.ScheduleTimeoutAsync("nyx-relay-unsanitized", unsanitized, dueTimeMs: 60000);

            await act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*reply_token*");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static IRuntimeCallbackSchedulerGrain ResolveSchedulerGrain(IHost host, string actorId) =>
        host.Services.GetRequiredService<IGrainFactory>().GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);

    private static async Task<IHost> StartSiloHostAsync()
    {
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        var host = Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: siloPort,
                    gatewayPort: gatewayPort,
                    serviceId: $"aevatar-runtime-callback-credential-guard-service-{Guid.NewGuid():N}",
                    clusterId: $"aevatar-runtime-callback-credential-guard-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static EventEnvelope CreateEnvelope(string id, IMessage payload) => new()
    {
        Id = id,
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Payload = Any.Pack(payload),
        Route = EnvelopeRouteSemantics.CreateDirect("test", "credential-guard-actor"),
    };

    private static LarkCardOperationTimeoutFiredEvent CreateLarkCardTimeout(string nyxUserAccessToken) => new()
    {
        CorrelationId = "corr-lark-card",
        Operation = LarkCardOperationPhase.Finalize,
        Sequence = 1,
        OperationGeneration = 1,
        CardId = "card-1",
        CardMessageId = "om-card-1",
        CommandId = "cmd-1",
        Activity = CreateActivity(nyxUserAccessToken),
        FinalText = "final text",
        LastFlushedText = "final",
        FiredAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private static NyxRelayTextOperationTimeoutFiredEvent CreateNyxRelayTimeout(
        string replyToken,
        long replyTokenExpiresAtUnixMs) => new()
    {
        CorrelationId = "corr-nyx-relay",
        Operation = NyxRelayTextOperationKind.Interim,
        Sequence = 1,
        OperationGeneration = 1,
        Chunk = new LlmReplyStreamChunkEvent
        {
            CorrelationId = "corr-nyx-relay",
            RegistrationId = "reg-1",
            Activity = CreateActivity(nyxUserAccessToken: string.Empty),
            AccumulatedText = "hello",
            ChunkAtUnixMs = 42,
            ReplyToken = replyToken,
            ReplyTokenExpiresAtUnixMs = replyTokenExpiresAtUnixMs,
        },
        CurrentPlatformMessageId = "om-current",
        CommandId = "cmd-1",
        FinalText = "final text",
        LastFlushedText = "final",
        EditCount = 1,
        FiredAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private static ChatActivity CreateActivity(string nyxUserAccessToken) => new()
    {
        Id = "activity-1",
        Type = ActivityType.Message,
        ChannelId = new ChannelId { Value = "lark" },
        Bot = new BotInstanceId { Value = "lark-bot" },
        Conversation = new ConversationReference
        {
            Channel = new ChannelId { Value = "lark" },
            Bot = new BotInstanceId { Value = "lark-bot" },
            Scope = ConversationScope.Group,
            CanonicalKey = "conv:lark:group",
        },
        Content = new MessageContent { Text = "user question" },
        OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-message-1",
            CorrelationId = "corr-1",
        },
        TransportExtras = new TransportExtras
        {
            NyxMessageId = "nyx-message-1",
            NyxAgentApiKeyId = "nyx-agent-key-1",
            NyxPlatform = "lark",
            NyxConversationId = "oc-1",
            NyxUserAccessToken = nyxUserAccessToken,
            NyxPlatformMessageId = "om-1",
            NyxLarkUnionId = "on-1",
            NyxLarkChatId = "oc-lark-1",
            NyxRegistrationScopeId = "scope-1",
            NyxSenderUserId = "user-1",
        },
    };
}
