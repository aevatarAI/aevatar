using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.Household;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
    public async Task DispatchCallbackAsync_WhenAdmissionUsesEventId_ShouldMapEnvelopeIdentityTimestampAndOperationId()
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
        var admission = await CreateAdmission("reg-1", "nxmsg-1", eventId: "evt-3", correlationKey: "ha-corr-1");

        var result = await facade.DispatchCallbackAsync(new DeviceCallbackDispatchCommand(
            "reg-1",
            new DeviceInbound
            {
                EventId = "evt-3",
                EventType = "temperature_change",
            },
            admission));

        result.Succeeded.Should().BeTrue();
        result.Receipt!.CommandId.Should().Be("nxmsg-1");
        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Id.Should().Be("nxmsg-1");
        capturedEnvelope.Timestamp.ToDateTimeOffset().Should().Be(admission.OccurredAt);
        capturedEnvelope.Runtime.DeliveryIdentity.OperationId.Should().Be("device-event:reg-1:evt-3");
    }

    [Fact]
    public async Task DispatchCallbackAsync_WhenEventIdMissing_ShouldUseHomeAlertCorrelationKeyForOperationId()
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
        var admission = await CreateAdmission("reg-1", "nxmsg-2", eventId: "", correlationKey: "ha-corr-2");

        var result = await facade.DispatchCallbackAsync(new DeviceCallbackDispatchCommand(
            "reg-1",
            new DeviceInbound
            {
                EventType = "alarm_triggered",
                HomeAlert = new HomeAlertDeviceInboundPayload
                {
                    CorrelationKey = "ha-corr-2",
                },
            },
            admission));

        result.Succeeded.Should().BeTrue();
        admission.DeliveryId.Should().Be("ha-corr-2");
        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Runtime.DeliveryIdentity.OperationId.Should().Be("device-event:reg-1:ha-corr-2");
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

    private static async Task<DeviceCallbackAdmission> CreateAdmission(
        string registrationId,
        string messageId,
        string eventId,
        string correlationKey)
    {
        const string hmacKey = "key-a";
        const string timestampValue = "2026-04-09T10:00:00Z";
        var body = JsonSerializer.Serialize(new
        {
            message_id = messageId,
            content = new
            {
                text = JsonSerializer.Serialize(new
                {
                    event_id = eventId,
                    event_type = "alarm_triggered",
                    correlation_key = correlationKey,
                }),
            },
            timestamp = timestampValue,
        });
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(hmacKey));
        context.Request.Headers["X-NyxID-Signature"] = Convert.ToHexStringLower(
            hmac.ComputeHash(DeviceEventEndpoints.BuildSignaturePayload(bodyBytes)));

        var result = await DeviceEventEndpoints.AdmitCallback(
            context,
            bodyBytes,
            new DeviceRegistrationEntry
            {
                Id = registrationId,
                HmacKey = hmacKey,
            },
            new DeviceEventOptions { CallbackFreshnessWindow = TimeSpan.FromSeconds(10) },
            DateTimeOffset.Parse(timestampValue).AddSeconds(1),
            new InMemorySecretVault(),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        return result.Admission!;
    }
}
