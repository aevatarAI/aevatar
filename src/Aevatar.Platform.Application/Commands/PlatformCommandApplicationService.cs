using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.Platform.Abstractions.Catalog;
using Aevatar.Platform.Application.Abstractions.Commands;
using Aevatar.Platform.Application.Abstractions.Ports;
using Microsoft.Extensions.Logging;

namespace Aevatar.Platform.Application.Commands;

public sealed class PlatformCommandApplicationService : IPlatformCommandApplicationService
{
    private readonly IAgentCommandRouter _commandRouter;
    private readonly ICommandContextPolicy _commandContextPolicy;
    private readonly ICommandBus _commandBus;
    private readonly IPlatformCommandStateStore _stateStore;
    private readonly IPlatformCommandSagaTracker _sagaTracker;
    private readonly ILogger<PlatformCommandApplicationService> _logger;

    public PlatformCommandApplicationService(
        IAgentCommandRouter commandRouter,
        ICommandContextPolicy commandContextPolicy,
        ICommandBus commandBus,
        IPlatformCommandStateStore stateStore,
        IPlatformCommandSagaTracker sagaTracker,
        ILogger<PlatformCommandApplicationService> logger)
    {
        _commandRouter = commandRouter;
        _commandContextPolicy = commandContextPolicy;
        _commandBus = commandBus;
        _stateStore = stateStore;
        _sagaTracker = sagaTracker;
        _logger = logger;
    }

    public async Task<PlatformCommandEnqueueResult> EnqueueAsync(
        PlatformCommandRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Subsystem) ||
            string.IsNullOrWhiteSpace(request.Command) ||
            string.IsNullOrWhiteSpace(request.Method))
        {
            return new PlatformCommandEnqueueResult(PlatformCommandStartError.InvalidRequest, null);
        }

        var target = _commandRouter.Resolve(request.Subsystem, request.Command);
        if (target == null)
            return new PlatformCommandEnqueueResult(PlatformCommandStartError.SubsystemNotFound, null);

        var commandContext = _commandContextPolicy.Create(request.Subsystem);
        var acceptedAt = DateTimeOffset.UtcNow;
        var started = new PlatformCommandStarted(
            commandContext.CommandId,
            request.Subsystem,
            request.Command,
            request.Method,
            target.ToString(),
            acceptedAt);

        var acceptedStatus = new PlatformCommandStatus
        {
            CommandId = started.CommandId,
            Subsystem = started.Subsystem,
            Command = started.Command,
            Method = started.Method,
            TargetEndpoint = started.TargetEndpoint,
            State = "Accepted",
            Succeeded = false,
            AcceptedAt = acceptedAt,
            UpdatedAt = acceptedAt,
        };
        await _stateStore.UpsertAsync(acceptedStatus, ct);
        await _sagaTracker.TrackAsync(acceptedStatus, commandContext.CorrelationId, ct);

        try
        {
            var envelope = CommandEnvelope.Create(
                commandId: started.CommandId,
                correlationId: commandContext.CorrelationId,
                target: started.Subsystem,
                metadata: commandContext.Metadata);

            var command = new PlatformDispatchCommand(
                CommandId: started.CommandId,
                Subsystem: started.Subsystem,
                Command: started.Command,
                Method: started.Method,
                TargetEndpoint: started.TargetEndpoint,
                PayloadJson: request.PayloadJson ?? string.Empty,
                ContentType: request.ContentType ?? "application/json",
                AcceptedAt: started.AcceptedAt);

            await _commandBus.EnqueueAsync(envelope, command, ct);

            var queuedStatus = new PlatformCommandStatus
            {
                CommandId = started.CommandId,
                Subsystem = started.Subsystem,
                Command = started.Command,
                Method = started.Method,
                TargetEndpoint = started.TargetEndpoint,
                State = "Queued",
                Succeeded = false,
                AcceptedAt = started.AcceptedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _stateStore.UpsertAsync(queuedStatus, ct);
            await _sagaTracker.TrackAsync(queuedStatus, commandContext.CorrelationId, ct);

            return new PlatformCommandEnqueueResult(PlatformCommandStartError.None, started);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Platform command enqueue failed. commandId={CommandId}, subsystem={Subsystem}, command={Command}",
                started.CommandId,
                started.Subsystem,
                started.Command);

            var failedStatus = new PlatformCommandStatus
            {
                CommandId = started.CommandId,
                Subsystem = started.Subsystem,
                Command = started.Command,
                Method = started.Method,
                TargetEndpoint = started.TargetEndpoint,
                State = "Failed",
                Succeeded = false,
                Error = ex.Message,
                AcceptedAt = started.AcceptedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _stateStore.UpsertAsync(failedStatus, ct);
            await _sagaTracker.TrackAsync(failedStatus, commandContext.CorrelationId, ct);

            return new PlatformCommandEnqueueResult(PlatformCommandStartError.EnqueueFailed, null);
        }
    }
}
