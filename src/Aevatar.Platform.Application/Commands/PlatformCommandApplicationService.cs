using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Platform.Abstractions.Catalog;
using Aevatar.Platform.Application.Abstractions.Commands;
using Aevatar.Platform.Application.Abstractions.Ports;
using Microsoft.Extensions.Logging;

namespace Aevatar.Platform.Application.Commands;

public sealed class PlatformCommandApplicationService : IPlatformCommandApplicationService
{
    private readonly IAgentCommandRouter _commandRouter;
    private readonly ICommandContextPolicy _commandContextPolicy;
    private readonly IPlatformCommandStateStore _stateStore;
    private readonly IPlatformCommandDispatchGateway _dispatchGateway;
    private readonly ILogger<PlatformCommandApplicationService> _logger;

    public PlatformCommandApplicationService(
        IAgentCommandRouter commandRouter,
        ICommandContextPolicy commandContextPolicy,
        IPlatformCommandStateStore stateStore,
        IPlatformCommandDispatchGateway dispatchGateway,
        ILogger<PlatformCommandApplicationService> logger)
    {
        _commandRouter = commandRouter;
        _commandContextPolicy = commandContextPolicy;
        _stateStore = stateStore;
        _dispatchGateway = dispatchGateway;
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

        var initialStatus = new PlatformCommandStatus
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

        await _stateStore.UpsertAsync(initialStatus, ct);

        _ = Task.Run(
            async () => await DispatchAndUpdateStateAsync(started, request, CancellationToken.None),
            CancellationToken.None);

        return new PlatformCommandEnqueueResult(PlatformCommandStartError.None, started);
    }

    private async Task DispatchAndUpdateStateAsync(
        PlatformCommandStarted started,
        PlatformCommandRequest request,
        CancellationToken ct)
    {
        try
        {
            var dispatchResult = await _dispatchGateway.DispatchAsync(
                new PlatformCommandDispatchRequest(
                    started.Method,
                    new Uri(started.TargetEndpoint),
                    request.PayloadJson ?? string.Empty,
                    request.ContentType ?? "application/json"),
                ct);

            var state = dispatchResult.Succeeded ? "Completed" : "Failed";
            await _stateStore.UpsertAsync(new PlatformCommandStatus
            {
                CommandId = started.CommandId,
                Subsystem = started.Subsystem,
                Command = started.Command,
                Method = started.Method,
                TargetEndpoint = started.TargetEndpoint,
                State = state,
                Succeeded = dispatchResult.Succeeded,
                ResponseStatusCode = dispatchResult.ResponseStatusCode,
                ResponseContentType = dispatchResult.ResponseContentType,
                ResponseBody = dispatchResult.ResponseBody,
                Error = dispatchResult.Error,
                AcceptedAt = started.AcceptedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Platform command dispatch failed. commandId={CommandId}, subsystem={Subsystem}, command={Command}",
                started.CommandId,
                started.Subsystem,
                started.Command);

            await _stateStore.UpsertAsync(new PlatformCommandStatus
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
            }, CancellationToken.None);
        }
    }
}
