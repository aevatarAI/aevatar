using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AppPlatform.Application.Services;

public sealed class AppFunctionInvocationApplicationService : IAppFunctionInvocationPort
{
    private readonly IAppFunctionExecutionTargetQueryPort _targetQueryPort;
    private readonly IServiceInvocationPort _serviceInvocationPort;
    private readonly IOperationCommandPort _operationCommandPort;
    private readonly IAppFunctionRuntimeInvocationPort? _runtimeInvocationPort;

    public AppFunctionInvocationApplicationService(
        IAppFunctionExecutionTargetQueryPort targetQueryPort,
        IServiceInvocationPort serviceInvocationPort,
        IOperationCommandPort operationCommandPort,
        IAppFunctionRuntimeInvocationPort? runtimeInvocationPort = null)
    {
        _targetQueryPort = targetQueryPort ?? throw new ArgumentNullException(nameof(targetQueryPort));
        _serviceInvocationPort = serviceInvocationPort ?? throw new ArgumentNullException(nameof(serviceInvocationPort));
        _operationCommandPort = operationCommandPort ?? throw new ArgumentNullException(nameof(operationCommandPort));
        _runtimeInvocationPort = runtimeInvocationPort;
    }

    public async Task<AppFunctionInvokeAcceptedReceipt> InvokeAsync(
        string appId,
        string functionId,
        AppFunctionInvokeRequest request,
        string? releaseId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Payload == null)
            throw new InvalidOperationException("payload is required.");

        var target = await _targetQueryPort.ResolveAsync(appId, functionId, releaseId, ct)
            ?? throw new InvalidOperationException("App function target was not found.");

        AppFunctionInvokeAcceptedReceipt? runtimeReceipt = null;
        if (_runtimeInvocationPort != null)
        {
            var accepted = await _runtimeInvocationPort.TryInvokeAsync(
                target,
                request,
                async (runtimeAccepted, token) =>
                {
                    var operation = await AcceptOperationAsync(target, runtimeAccepted, token);
                    runtimeReceipt = BuildAcceptedReceipt(target, runtimeAccepted, operation);
                    return operation.OperationId;
                },
                ct);

            if (accepted != null && runtimeReceipt != null)
                return runtimeReceipt;
        }

        var invocationReceipt = await _serviceInvocationPort.InvokeAsync(CreateServiceInvocationRequest(target, request), ct);
        var operation = await AcceptOperationAsync(
            target,
            new InvocationAccepted(
                invocationReceipt.RequestId ?? string.Empty,
                invocationReceipt.TargetActorId ?? string.Empty,
                invocationReceipt.CommandId ?? string.Empty,
                invocationReceipt.CorrelationId ?? string.Empty),
            ct);

        return BuildAcceptedReceipt(
            target,
            new InvocationAccepted(
                invocationReceipt.RequestId ?? string.Empty,
                invocationReceipt.TargetActorId ?? string.Empty,
                invocationReceipt.CommandId ?? string.Empty,
                invocationReceipt.CorrelationId ?? string.Empty),
            operation);
    }

    private static ServiceInvocationRequest CreateServiceInvocationRequest(
        AppFunctionExecutionTarget target,
        AppFunctionInvokeRequest request) =>
        new()
        {
            Identity = new ServiceIdentity
            {
                TenantId = target.ServiceRef.TenantId ?? string.Empty,
                AppId = target.ServiceRef.AppId ?? string.Empty,
                Namespace = target.ServiceRef.Namespace ?? string.Empty,
                ServiceId = target.ServiceRef.ServiceId ?? string.Empty,
            },
            EndpointId = target.Entry.EndpointId ?? string.Empty,
            Payload = request.Payload!.Clone(),
            CommandId = request.CommandId ?? string.Empty,
            CorrelationId = request.CorrelationId ?? string.Empty,
            Caller = new ServiceInvocationCaller
            {
                ServiceKey = request.Caller?.ServiceKey ?? string.Empty,
                TenantId = request.Caller?.TenantId ?? string.Empty,
                AppId = request.Caller?.AppId ?? string.Empty,
            },
        };

    private Task<AppOperationSnapshot> AcceptOperationAsync(
        AppFunctionExecutionTarget target,
        AppFunctionRuntimeInvokeAccepted accepted,
        CancellationToken ct) =>
        AcceptOperationAsync(
            target,
            new InvocationAccepted(
                accepted.RequestId,
                accepted.TargetActorId,
                accepted.CommandId,
                accepted.CorrelationId),
            ct);

    private Task<AppOperationSnapshot> AcceptOperationAsync(
        AppFunctionExecutionTarget target,
        InvocationAccepted accepted,
        CancellationToken ct) =>
        _operationCommandPort.AcceptAsync(new AppOperationSnapshot
        {
            Kind = AppOperationKind.FunctionInvoke,
            Status = AppOperationStatus.Accepted,
            AppId = target.Release.AppId ?? string.Empty,
            ReleaseId = target.Release.ReleaseId ?? string.Empty,
            FunctionId = target.Entry.EntryId ?? string.Empty,
            ServiceId = target.ServiceRef.ServiceId ?? string.Empty,
            EndpointId = target.Entry.EndpointId ?? string.Empty,
            RequestId = accepted.RequestId,
            TargetActorId = accepted.TargetActorId,
            CommandId = accepted.CommandId,
            CorrelationId = accepted.CorrelationId,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc)),
        }, ct);

    private static AppFunctionInvokeAcceptedReceipt BuildAcceptedReceipt(
        AppFunctionExecutionTarget target,
        AppFunctionRuntimeInvokeAccepted accepted,
        AppOperationSnapshot operation) =>
        BuildAcceptedReceipt(
            target,
            new InvocationAccepted(
                accepted.RequestId,
                accepted.TargetActorId,
                accepted.CommandId,
                accepted.CorrelationId),
            operation);

    private static AppFunctionInvokeAcceptedReceipt BuildAcceptedReceipt(
        AppFunctionExecutionTarget target,
        InvocationAccepted accepted,
        AppOperationSnapshot operation)
    {
        var statusUrl = $"/api/operations/{Uri.EscapeDataString(operation.OperationId ?? string.Empty)}";

        return new AppFunctionInvokeAcceptedReceipt
        {
            AppId = target.Release.AppId ?? string.Empty,
            ReleaseId = target.Release.ReleaseId ?? string.Empty,
            FunctionId = target.Entry.EntryId ?? string.Empty,
            ServiceId = target.ServiceRef.ServiceId ?? string.Empty,
            EndpointId = target.Entry.EndpointId ?? string.Empty,
            RequestId = accepted.RequestId,
            TargetActorId = accepted.TargetActorId,
            CommandId = accepted.CommandId,
            CorrelationId = accepted.CorrelationId,
            OperationId = operation.OperationId ?? string.Empty,
            StatusUrl = statusUrl,
            EventsUrl = $"{statusUrl}/events",
            ResultUrl = $"{statusUrl}/result",
            StreamUrl = $"{statusUrl}:stream",
        };
    }

    private sealed record InvocationAccepted(
        string RequestId,
        string TargetActorId,
        string CommandId,
        string CorrelationId);
}
