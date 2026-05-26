using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Device;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

internal static class DeviceCommandFacadeTestSupport
{
    public static DeviceRegistrationCommandFacade CreateRegistrationFacade(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort)
    {
        var contextPolicy = new DefaultCommandContextPolicy();
        var envelopeFactory = new DeviceRegistrationCommandEnvelopeFactory();
        var targetDispatcher = new ActorCommandTargetDispatcher<DeviceRegistrationCommandTarget>(dispatchPort);
        var receiptFactory = new DeviceRegistrationCommandReceiptFactory();

        return new DeviceRegistrationCommandFacade(
            CreateRegistrationDispatchService<DeviceRegisterCommand>(actorRuntime, contextPolicy, envelopeFactory, targetDispatcher, receiptFactory),
            CreateRegistrationDispatchService<DeviceUnregisterCommand>(actorRuntime, contextPolicy, envelopeFactory, targetDispatcher, receiptFactory));
    }

    public static DeviceCallbackCommandFacade CreateCallbackFacade(
        IDeviceRegistrationQueryPort queryPort,
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort)
    {
        var contextPolicy = new DefaultCommandContextPolicy();
        var resolver = new DeviceCallbackCommandTargetResolver(queryPort, actorRuntime);
        var envelopeFactory = new DeviceCallbackCommandEnvelopeFactory();
        var targetDispatcher = new ActorCommandTargetDispatcher<DeviceCallbackCommandTarget>(dispatchPort);
        var receiptFactory = new DeviceCallbackCommandReceiptFactory();
        var pipeline = new DefaultCommandDispatchPipeline<DeviceCallbackDispatchCommand, DeviceCallbackCommandTarget, DeviceCommandAcceptedReceipt, DeviceCallbackCommandStartError>(
            resolver,
            contextPolicy,
            envelopeFactory,
            targetDispatcher,
            receiptFactory);
        return new DeviceCallbackCommandFacade(
            new DefaultCommandDispatchService<DeviceCallbackDispatchCommand, DeviceCallbackCommandTarget, DeviceCommandAcceptedReceipt, DeviceCallbackCommandStartError>(pipeline));
    }

    private static ICommandDispatchService<TCommand, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError> CreateRegistrationDispatchService<TCommand>(
        IActorRuntime actorRuntime,
        ICommandContextPolicy contextPolicy,
        ICommandEnvelopeFactory<TCommand> envelopeFactory,
        ICommandTargetDispatcher<DeviceRegistrationCommandTarget> targetDispatcher,
        ICommandReceiptFactory<DeviceRegistrationCommandTarget, DeviceCommandAcceptedReceipt> receiptFactory)
    {
        var resolver = new DeviceRegistrationCommandTargetResolver<TCommand>(actorRuntime);
        var pipeline = new DefaultCommandDispatchPipeline<TCommand, DeviceRegistrationCommandTarget, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>(
            resolver,
            contextPolicy,
            envelopeFactory,
            targetDispatcher,
            receiptFactory);
        return new DefaultCommandDispatchService<TCommand, DeviceRegistrationCommandTarget, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>(pipeline);
    }
}
