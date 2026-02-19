using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Runtime.Abstractions.Dispatch;
using Aevatar.Platform.Application.Abstractions.Commands;
using Aevatar.Platform.Application.Abstractions.Ports;
using Microsoft.Extensions.Logging;

namespace Aevatar.Platform.Infrastructure.Dispatch;

internal sealed class PlatformDispatchCommandHandler : ICommandHandler<PlatformDispatchCommand>
{
    private readonly IPlatformCommandDispatchGateway _dispatchGateway;
    private readonly IPlatformCommandStateStore _stateStore;
    private readonly IPlatformCommandSagaTracker _sagaTracker;
    private readonly ILogger<PlatformDispatchCommandHandler> _logger;

    public PlatformDispatchCommandHandler(
        IPlatformCommandDispatchGateway dispatchGateway,
        IPlatformCommandStateStore stateStore,
        IPlatformCommandSagaTracker sagaTracker,
        ILogger<PlatformDispatchCommandHandler> logger)
    {
        _dispatchGateway = dispatchGateway;
        _stateStore = stateStore;
        _sagaTracker = sagaTracker;
        _logger = logger;
    }

    public async Task HandleAsync(
        CommandEnvelope envelope,
        PlatformDispatchCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ct.ThrowIfCancellationRequested();

        var runningStatus = new PlatformCommandStatus
        {
            CommandId = command.CommandId,
            Subsystem = command.Subsystem,
            Command = command.Command,
            Method = command.Method,
            TargetEndpoint = command.TargetEndpoint,
            State = "Running",
            Succeeded = false,
            AcceptedAt = command.AcceptedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _stateStore.UpsertAsync(runningStatus, ct);
        await _sagaTracker.TrackAsync(runningStatus, envelope.CorrelationId, ct);

        try
        {
            var dispatchResult = await _dispatchGateway.DispatchAsync(
                new PlatformCommandDispatchRequest(
                    command.Method,
                    new Uri(command.TargetEndpoint),
                    command.PayloadJson,
                    command.ContentType),
                ct);

            var state = dispatchResult.Succeeded ? "Completed" : "Failed";
            var resultStatus = new PlatformCommandStatus
            {
                CommandId = command.CommandId,
                Subsystem = command.Subsystem,
                Command = command.Command,
                Method = command.Method,
                TargetEndpoint = command.TargetEndpoint,
                State = state,
                Succeeded = dispatchResult.Succeeded,
                ResponseStatusCode = dispatchResult.ResponseStatusCode,
                ResponseContentType = dispatchResult.ResponseContentType,
                ResponseBody = dispatchResult.ResponseBody,
                Error = dispatchResult.Error,
                AcceptedAt = command.AcceptedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _stateStore.UpsertAsync(resultStatus, ct);
            await _sagaTracker.TrackAsync(resultStatus, envelope.CorrelationId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Platform command dispatch failed. commandId={CommandId}, subsystem={Subsystem}, command={Command}",
                command.CommandId,
                command.Subsystem,
                command.Command);

            var failedStatus = new PlatformCommandStatus
            {
                CommandId = command.CommandId,
                Subsystem = command.Subsystem,
                Command = command.Command,
                Method = command.Method,
                TargetEndpoint = command.TargetEndpoint,
                State = "Failed",
                Succeeded = false,
                Error = ex.Message,
                AcceptedAt = command.AcceptedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _stateStore.UpsertAsync(failedStatus, ct);
            await _sagaTracker.TrackAsync(failedStatus, envelope.CorrelationId, ct);

            throw;
        }
    }
}
