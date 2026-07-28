using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

internal static class ChannelRegistrationCommandFacadeTestSupport
{
    public static ChannelRegistrationCommandFacade CreateFacade(IActorRuntime actorRuntime, IActorDispatchPort dispatchPort)
    {
        var contextPolicy = new DefaultCommandContextPolicy();
        var envelopeFactory = new ChannelBotRegistrationCommandEnvelopeFactory();
        var targetDispatcher = new ActorCommandTargetDispatcher<ChannelBotRegistrationCommandTarget>(dispatchPort);
        var receiptFactory = new ChannelRegistrationCommandReceiptFactory();

        return new ChannelRegistrationCommandFacade(
            CreateDispatchService<ChannelBotRegisterCommand>(actorRuntime, contextPolicy, envelopeFactory, targetDispatcher, receiptFactory),
            CreateDispatchService<ChannelBotUnregisterCommand>(actorRuntime, contextPolicy, envelopeFactory, targetDispatcher, receiptFactory));
    }

    public static IChannelWorkflowResultDeliveryRepairCommandPort CreateRepairPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort)
    {
        var contextPolicy = new DefaultCommandContextPolicy();
        var envelopeFactory = new ChannelBotRegistrationCommandEnvelopeFactory();
        var targetDispatcher = new ActorCommandTargetDispatcher<ChannelBotRegistrationCommandTarget>(dispatchPort);
        var receiptFactory = new ChannelRegistrationCommandReceiptFactory();

        return new ChannelWorkflowResultDeliveryRepairCommandPort(
            CreateDispatchService<ChannelBotWorkflowResultDeliveryRepairRequestCommand>(actorRuntime, contextPolicy, envelopeFactory, targetDispatcher, receiptFactory),
            CreateDispatchService<ChannelBotWorkflowResultDeliveryRepairPrepareCommand>(actorRuntime, contextPolicy, envelopeFactory, targetDispatcher, receiptFactory),
            CreateDispatchService<ChannelBotWorkflowResultDeliveryRepairCompleteCommand>(actorRuntime, contextPolicy, envelopeFactory, targetDispatcher, receiptFactory),
            CreateDispatchService<ChannelBotWorkflowResultDeliveryRepairFailCommand>(actorRuntime, contextPolicy, envelopeFactory, targetDispatcher, receiptFactory));
    }

    private static ICommandDispatchService<TCommand, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError> CreateDispatchService<TCommand>(
        IActorRuntime actorRuntime,
        ICommandContextPolicy contextPolicy,
        ICommandEnvelopeFactory<TCommand> envelopeFactory,
        ICommandTargetDispatcher<ChannelBotRegistrationCommandTarget> targetDispatcher,
        ICommandReceiptFactory<ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandAcceptedReceipt> receiptFactory)
    {
        var resolver = new ChannelBotRegistrationCommandTargetResolver<TCommand>(actorRuntime);
        var pipeline = new DefaultCommandDispatchPipeline<TCommand, ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError>(
            resolver,
            contextPolicy,
            envelopeFactory,
            targetDispatcher,
            receiptFactory);
        return new DefaultCommandDispatchService<TCommand, ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError>(pipeline);
    }
}
