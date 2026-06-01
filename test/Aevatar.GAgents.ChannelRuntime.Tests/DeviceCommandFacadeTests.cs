using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.Household;
using FluentAssertions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class DeviceCommandFacadeTests
{
    [Fact]
    public async Task RegisterAsync_WhenStoreActorIsMissing_ShouldCreateStoreActorAndDispatchTypedEnvelope()
    {
        EventEnvelope? capturedEnvelope = null;
        var actor = Substitute.For<IActor>();
        actor.Id.Returns(DeviceRegistrationGAgent.WellKnownId);
        var actorRuntime = Substitute.For<IActorRuntime>();
        var dispatchPort = Substitute.For<IActorDispatchPort>();
        actorRuntime.GetAsync(DeviceRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<DeviceRegistrationGAgent>(
                DeviceRegistrationGAgent.WellKnownId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(actor));
        dispatchPort.DispatchAsync(
                DeviceRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);
        var facade = DeviceCommandFacadeTestSupport.CreateRegistrationFacade(actorRuntime, dispatchPort);

        var receipt = await facade.RegisterAsync(new DeviceRegisterCommand
        {
            ScopeId = "scope-a",
            HmacKey = "key-a",
            DeviceEventTargetActorId = "household-scope-a",
        });

        receipt.ActorId.Should().Be(DeviceRegistrationGAgent.WellKnownId);
        receipt.CommandId.Should().NotBeNullOrWhiteSpace();
        receipt.CorrelationId.Should().Be(receipt.CommandId);
        capturedEnvelope.Should().NotBeNull();
        var command = capturedEnvelope!.Payload.Unpack<DeviceRegisterCommand>();
        command.ScopeId.Should().Be("scope-a");
        command.DeviceEventTargetActorId.Should().Be("household-scope-a");
        await actorRuntime.Received(1).CreateAsync<DeviceRegistrationGAgent>(
            DeviceRegistrationGAgent.WellKnownId,
            Arg.Any<CancellationToken>());
        await dispatchPort.Received(1).DispatchAsync(
            DeviceRegistrationGAgent.WellKnownId,
            Arg.Any<EventEnvelope>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchCallbackAsync_WhenRegistrationMissing_ShouldReturnAdmissionErrorAndNotDispatch()
    {
        var queryPort = Substitute.For<IDeviceRegistrationQueryPort>();
        var actorRuntime = Substitute.For<IActorRuntime>();
        var dispatchPort = Substitute.For<IActorDispatchPort>();
        queryPort.GetAsync("reg-missing", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DeviceRegistrationEntry?>(null));
        var facade = DeviceCommandFacadeTestSupport.CreateCallbackFacade(queryPort, actorRuntime, dispatchPort);

        var result = await facade.DispatchCallbackAsync(new DeviceCallbackDispatchCommand(
            "reg-missing",
            new DeviceInbound { EventId = "evt-1" }));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(DeviceCallbackCommandStartError.RegistrationNotFound);
        await actorRuntime.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);
        await dispatchPort.DidNotReceiveWithAnyArgs().DispatchAsync(default!, default!, default);
    }

    [Fact]
    public async Task DispatchCallbackAsync_WhenRegistrationHasNoTarget_ShouldReturnAdmissionErrorAndNotCreateHousehold()
    {
        var queryPort = Substitute.For<IDeviceRegistrationQueryPort>();
        var actorRuntime = Substitute.For<IActorRuntime>();
        var dispatchPort = Substitute.For<IActorDispatchPort>();
        queryPort.GetAsync("reg-targetless", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DeviceRegistrationEntry?>(new DeviceRegistrationEntry
            {
                Id = "reg-targetless",
                ScopeId = "scope-a",
                HmacKey = "key-a",
            }));
        var facade = DeviceCommandFacadeTestSupport.CreateCallbackFacade(queryPort, actorRuntime, dispatchPort);

        var result = await facade.DispatchCallbackAsync(new DeviceCallbackDispatchCommand(
            "reg-targetless",
            new DeviceInbound { EventId = "evt-2" }));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(DeviceCallbackCommandStartError.RegistrationNotAdmitted);
        await actorRuntime.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);
        await dispatchPort.DidNotReceiveWithAnyArgs().DispatchAsync(default!, default!, default);
    }

    [Fact]
    public async Task DispatchCallbackAsync_WhenTargetExists_ShouldDispatchInboundAndReturnAcceptedReceipt()
    {
        EventEnvelope? capturedEnvelope = null;
        var targetActor = Substitute.For<IActor>();
        targetActor.Id.Returns("household-scope-a");
        var queryPort = Substitute.For<IDeviceRegistrationQueryPort>();
        var actorRuntime = Substitute.For<IActorRuntime>();
        var dispatchPort = Substitute.For<IActorDispatchPort>();
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DeviceRegistrationEntry?>(new DeviceRegistrationEntry
            {
                Id = "reg-1",
                ScopeId = "scope-a",
                HmacKey = "key-a",
                DeviceEventTargetActorId = "household-scope-a",
            }));
        actorRuntime.GetAsync("household-scope-a")
            .Returns(Task.FromResult<IActor?>(targetActor));
        dispatchPort.DispatchAsync(
                "household-scope-a",
                Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);
        var facade = DeviceCommandFacadeTestSupport.CreateCallbackFacade(queryPort, actorRuntime, dispatchPort);

        var result = await facade.DispatchCallbackAsync(new DeviceCallbackDispatchCommand(
            "reg-1",
            new DeviceInbound
            {
                EventId = "evt-3",
                EventType = "temperature_change",
            },
            CommandId: "cmd-1",
            CorrelationId: "corr-1"));

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().NotBeNull();
        result.Receipt!.ActorId.Should().Be("household-scope-a");
        result.Receipt.CommandId.Should().Be("cmd-1");
        result.Receipt.CorrelationId.Should().Be("corr-1");
        result.Receipt.RegistrationId.Should().Be("reg-1");
        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Id.Should().Be("cmd-1");
        var inbound = capturedEnvelope.Payload.Unpack<DeviceInbound>();
        inbound.EventId.Should().Be("evt-3");
        capturedEnvelope.Payload.TypeUrl.Should().EndWith("/aevatar.gagents.household.DeviceInbound");
        DeviceInbound.Descriptor.FullName.Should().Be("aevatar.gagents.household.DeviceInbound");
        await actorRuntime.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);
        await dispatchPort.Received(1).DispatchAsync(
            "household-scope-a",
            Arg.Any<EventEnvelope>(),
            Arg.Any<CancellationToken>());
    }
}
