using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelWorkflowResultDeliveryRepairCommandPortTests
{
    [Fact]
    public async Task RepairCommands_UseStandardDirectCommandSkeletonAndAcceptedReceipts()
    {
        var envelopes = new List<EventEnvelope>();
        var actorRuntime = Substitute.For<IActorRuntime>();
        var dispatchPort = Substitute.For<IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        dispatchPort.DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => envelopes.Add(envelope.Clone())),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);
        var port = ChannelRegistrationCommandFacadeTestSupport.CreateRepairPort(
            actorRuntime,
            dispatchPort);

        var receipts = new[]
        {
            await port.RequestAsync(new ChannelBotWorkflowResultDeliveryRepairRequestCommand
            {
                RegistrationId = "reg-alpha",
                RequestId = "repair-alpha",
            }),
            await port.PrepareAsync(new ChannelBotWorkflowResultDeliveryRepairPrepareCommand
            {
                RegistrationId = "reg-alpha",
                RequestId = "repair-alpha",
            }),
            await port.CompleteAsync(new ChannelBotWorkflowResultDeliveryRepairCompleteCommand
            {
                RegistrationId = "reg-alpha",
                RequestId = "repair-alpha",
            }),
            await port.FailAsync(new ChannelBotWorkflowResultDeliveryRepairFailCommand
            {
                RegistrationId = "reg-alpha",
                RequestId = "repair-alpha",
            }),
        };

        receipts.Should().OnlyContain(receipt =>
            receipt.ActorId == ChannelBotRegistrationGAgent.WellKnownId &&
            !string.IsNullOrWhiteSpace(receipt.CommandId) &&
            receipt.CorrelationId == receipt.CommandId);
        envelopes.Should().HaveCount(4);
        envelopes.Select(static envelope => envelope.Payload.TypeUrl).Should().Equal(
            Any.Pack(new ChannelBotWorkflowResultDeliveryRepairRequestCommand()).TypeUrl,
            Any.Pack(new ChannelBotWorkflowResultDeliveryRepairPrepareCommand()).TypeUrl,
            Any.Pack(new ChannelBotWorkflowResultDeliveryRepairCompleteCommand()).TypeUrl,
            Any.Pack(new ChannelBotWorkflowResultDeliveryRepairFailCommand()).TypeUrl);
        envelopes.Should().OnlyContain(envelope =>
            !string.IsNullOrWhiteSpace(envelope.Id) &&
            envelope.Route.RouteCase == EnvelopeRoute.RouteOneofCase.Direct &&
            envelope.Route.Direct.TargetActorId == ChannelBotRegistrationGAgent.WellKnownId);
    }

    [Fact]
    public void AddNyxIdRelayChannel_RegistersEveryRepairCommandPipeline()
    {
        var services = new ServiceCollection();

        services.AddNyxIdRelayChannel();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IChannelWorkflowResultDeliveryRepairCommandPort) &&
            descriptor.ImplementationType == typeof(ChannelWorkflowResultDeliveryRepairCommandPort));
        AssertDispatchRegistered<ChannelBotWorkflowResultDeliveryRepairRequestCommand>(services);
        AssertDispatchRegistered<ChannelBotWorkflowResultDeliveryRepairPrepareCommand>(services);
        AssertDispatchRegistered<ChannelBotWorkflowResultDeliveryRepairCompleteCommand>(services);
        AssertDispatchRegistered<ChannelBotWorkflowResultDeliveryRepairFailCommand>(services);
    }

    private static void AssertDispatchRegistered<TCommand>(IServiceCollection services)
    {
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(
            ICommandDispatchService<
                TCommand,
                ChannelRegistrationCommandAcceptedReceipt,
                ChannelRegistrationCommandStartError>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ICommandEnvelopeFactory<TCommand>));
    }
}
