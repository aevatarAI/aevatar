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
    private readonly ILogger<PlatformDispatchCommandHandler> _logger;

    public PlatformDispatchCommandHandler(
        IPlatformCommandDispatchGateway dispatchGateway,
        IPlatformCommandStateStore stateStore,
        ILogger<PlatformDispatchCommandHandler> logger)
    {
        _dispatchGateway = dispatchGateway;
        _stateStore = stateStore;
        _logger = logger;
    }

    public async Task HandleAsync(
        CommandEnvelope envelope,
        PlatformDispatchCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ct.ThrowIfCancellationRequested();

        await _stateStore.UpsertAsync(new PlatformCommandStatus
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
        }, ct);

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
            await _stateStore.UpsertAsync(new PlatformCommandStatus
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
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Platform command dispatch failed. commandId={CommandId}, subsystem={Subsystem}, command={Command}",
                command.CommandId,
                command.Subsystem,
                command.Command);

            await _stateStore.UpsertAsync(new PlatformCommandStatus
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
            }, ct);

            throw;
        }
    }
}
