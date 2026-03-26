namespace Aevatar.AppPlatform.Abstractions.Ports;

public sealed record AppFunctionRuntimeInvokeAccepted(
    string RequestId,
    string TargetActorId,
    string CommandId,
    string CorrelationId);
