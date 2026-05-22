using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRegistrationCommandFacadeTests
{
    [Fact]
    public async Task RebuildProjectionAsync_WhenStoreActorIsMissing_ShouldCreateActorBeforeDispatch()
    {
        EventEnvelope? capturedEnvelope = null;
        var createdActor = Substitute.For<IActor>();
        var actorRuntime = Substitute.For<IActorRuntime>();
        var dispatchPort = Substitute.For<IActorDispatchPort>();

        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createdActor));
        dispatchPort.DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var facade = ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, dispatchPort);

        var receipt = await facade.RebuildProjectionAsync("manual-debug");

        receipt.ActorId.Should().Be(ChannelBotRegistrationGAgent.WellKnownId);
        receipt.CommandId.Should().NotBeNullOrWhiteSpace();
        receipt.CorrelationId.Should().Be(receipt.CommandId);
        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Unpack<ChannelBotRebuildProjectionCommand>().Reason.Should().Be("manual-debug");
        await actorRuntime.Received(1).CreateAsync<ChannelBotRegistrationGAgent>(
            ChannelBotRegistrationGAgent.WellKnownId,
            Arg.Any<CancellationToken>());
        await dispatchPort.Received(1).DispatchAsync(
            ChannelBotRegistrationGAgent.WellKnownId,
            Arg.Any<EventEnvelope>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RebuildProjectionAsync_WhenStoreActorCannotBeCreated_ShouldFailWithoutDispatch()
    {
        var actorRuntime = Substitute.For<IActorRuntime>();
        var dispatchPort = Substitute.For<IActorDispatchPort>();

        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(null!));
        var facade = ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, dispatchPort);

        var act = () => facade.RebuildProjectionAsync("manual-debug");

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*StoreActorUnavailable*");
        await dispatchPort.DidNotReceiveWithAnyArgs().DispatchAsync(default!, default!, default);
    }
}
