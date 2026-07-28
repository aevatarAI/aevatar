using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public interface IChannelWorkflowResultDeliveryRepairCommandPort
{
    Task<ChannelRegistrationCommandAcceptedReceipt> RequestAsync(
        ChannelBotWorkflowResultDeliveryRepairRequestCommand command,
        CancellationToken ct = default);

    Task<ChannelRegistrationCommandAcceptedReceipt> PrepareAsync(
        ChannelBotWorkflowResultDeliveryRepairPrepareCommand command,
        CancellationToken ct = default);

    Task<ChannelRegistrationCommandAcceptedReceipt> CompleteAsync(
        ChannelBotWorkflowResultDeliveryRepairCompleteCommand command,
        CancellationToken ct = default);

    Task<ChannelRegistrationCommandAcceptedReceipt> FailAsync(
        ChannelBotWorkflowResultDeliveryRepairFailCommand command,
        CancellationToken ct = default);
}

internal sealed class ChannelWorkflowResultDeliveryRepairCommandPort
    : IChannelWorkflowResultDeliveryRepairCommandPort
{
    private readonly ICommandDispatchService<
        ChannelBotWorkflowResultDeliveryRepairRequestCommand,
        ChannelRegistrationCommandAcceptedReceipt,
        ChannelRegistrationCommandStartError> _requestDispatchService;
    private readonly ICommandDispatchService<
        ChannelBotWorkflowResultDeliveryRepairPrepareCommand,
        ChannelRegistrationCommandAcceptedReceipt,
        ChannelRegistrationCommandStartError> _prepareDispatchService;
    private readonly ICommandDispatchService<
        ChannelBotWorkflowResultDeliveryRepairCompleteCommand,
        ChannelRegistrationCommandAcceptedReceipt,
        ChannelRegistrationCommandStartError> _completeDispatchService;
    private readonly ICommandDispatchService<
        ChannelBotWorkflowResultDeliveryRepairFailCommand,
        ChannelRegistrationCommandAcceptedReceipt,
        ChannelRegistrationCommandStartError> _failDispatchService;

    public ChannelWorkflowResultDeliveryRepairCommandPort(
        ICommandDispatchService<
            ChannelBotWorkflowResultDeliveryRepairRequestCommand,
            ChannelRegistrationCommandAcceptedReceipt,
            ChannelRegistrationCommandStartError> requestDispatchService,
        ICommandDispatchService<
            ChannelBotWorkflowResultDeliveryRepairPrepareCommand,
            ChannelRegistrationCommandAcceptedReceipt,
            ChannelRegistrationCommandStartError> prepareDispatchService,
        ICommandDispatchService<
            ChannelBotWorkflowResultDeliveryRepairCompleteCommand,
            ChannelRegistrationCommandAcceptedReceipt,
            ChannelRegistrationCommandStartError> completeDispatchService,
        ICommandDispatchService<
            ChannelBotWorkflowResultDeliveryRepairFailCommand,
            ChannelRegistrationCommandAcceptedReceipt,
            ChannelRegistrationCommandStartError> failDispatchService)
    {
        _requestDispatchService = requestDispatchService ??
            throw new ArgumentNullException(nameof(requestDispatchService));
        _prepareDispatchService = prepareDispatchService ??
            throw new ArgumentNullException(nameof(prepareDispatchService));
        _completeDispatchService = completeDispatchService ??
            throw new ArgumentNullException(nameof(completeDispatchService));
        _failDispatchService = failDispatchService ??
            throw new ArgumentNullException(nameof(failDispatchService));
    }

    public Task<ChannelRegistrationCommandAcceptedReceipt> RequestAsync(
        ChannelBotWorkflowResultDeliveryRepairRequestCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(_requestDispatchService, command, ct);

    public Task<ChannelRegistrationCommandAcceptedReceipt> PrepareAsync(
        ChannelBotWorkflowResultDeliveryRepairPrepareCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(_prepareDispatchService, command, ct);

    public Task<ChannelRegistrationCommandAcceptedReceipt> CompleteAsync(
        ChannelBotWorkflowResultDeliveryRepairCompleteCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(_completeDispatchService, command, ct);

    public Task<ChannelRegistrationCommandAcceptedReceipt> FailAsync(
        ChannelBotWorkflowResultDeliveryRepairFailCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(_failDispatchService, command, ct);

    private static async Task<ChannelRegistrationCommandAcceptedReceipt> DispatchAsync<TCommand>(
        ICommandDispatchService<
            TCommand,
            ChannelRegistrationCommandAcceptedReceipt,
            ChannelRegistrationCommandStartError> dispatchService,
        TCommand command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = await dispatchService.DispatchAsync(command, ct);
        if (result.Succeeded && result.Receipt is not null)
            return result.Receipt;

        throw new InvalidOperationException(
            $"Channel workflow result delivery repair command dispatch failed: {result.Error}");
    }
}
