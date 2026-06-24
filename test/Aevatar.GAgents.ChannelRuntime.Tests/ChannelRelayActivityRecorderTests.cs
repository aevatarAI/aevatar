using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRelayActivityRecorderTests
{
    private static (IChannelBotRegistrationQueryByNyxIdentityPort query, IActorRuntime runtime) Mocks(
        ChannelBotRegistrationEntry? entry)
    {
        var query = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        query.GetByNyxAgentApiKeyIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(entry));

        var runtime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var actor = Substitute.For<IActor>();
        actor.Id.Returns(ChannelBotRegistrationGAgent.WellKnownId);
        runtime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId).Returns(Task.FromResult<IActor?>(actor));
        return (query, runtime);
    }

    [Fact]
    public async Task RecordInbound_DispatchesActivation_ForOwnedNotYetActiveBot()
    {
        var (query, runtime) = Mocks(new ChannelBotRegistrationEntry
        {
            Id = "reg-1",
            Platform = "lark",
            ScopeId = "scope-1",
            NyxAgentApiKeyId = "key-1",
            // no LastInboundAtUtc → not yet activated
        });
        EventEnvelope? captured = null;
        ((IActorDispatchPort)runtime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(e => captured = e),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        var recorder = new ChannelRelayActivityRecorder(query, runtime, (IActorDispatchPort)runtime, NullLogger<ChannelRelayActivityRecorder>.Instance);
        await recorder.RecordInboundAsync("key-1", CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Payload.Is(ChannelBotRecordInboundCommand.Descriptor).Should().BeTrue();
        captured.Payload.Unpack<ChannelBotRecordInboundCommand>().RegistrationId.Should().Be("reg-1");
    }

    [Fact]
    public async Task RecordInbound_SkipsDispatch_WhenAlreadyActive()
    {
        var (query, runtime) = Mocks(new ChannelBotRegistrationEntry
        {
            Id = "reg-1",
            NyxAgentApiKeyId = "key-1",
            LastInboundAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        var recorder = new ChannelRelayActivityRecorder(query, runtime, (IActorDispatchPort)runtime, NullLogger<ChannelRelayActivityRecorder>.Instance);
        await recorder.RecordInboundAsync("key-1", CancellationToken.None);

        await ((IActorDispatchPort)runtime).DidNotReceiveWithAnyArgs().DispatchAsync(default!, default!, default);
    }

    [Fact]
    public async Task RecordInbound_SkipsDispatch_WhenRegistrationMissing()
    {
        var (query, runtime) = Mocks(entry: null);

        var recorder = new ChannelRelayActivityRecorder(query, runtime, (IActorDispatchPort)runtime, NullLogger<ChannelRelayActivityRecorder>.Instance);
        await recorder.RecordInboundAsync("key-unknown", CancellationToken.None);

        await ((IActorDispatchPort)runtime).DidNotReceiveWithAnyArgs().DispatchAsync(default!, default!, default);
    }
}
