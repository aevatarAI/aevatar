namespace Aevatar.GAgentService.Abstractions.Ports;

/// <summary>
/// Write contract for the implementation-agnostic service-run registry.
/// Used by the invocation dispatcher to register a run before returning the accepted receipt,
/// so Studio Observe can query the run from the durable readmodel even on immediate refresh.
/// </summary>
public interface IServiceRunRegistrationPort
{
    Task<ServiceRunRegistrationResult> RegisterAsync(
        ServiceRunRecord record,
        CancellationToken ct = default);

    Task UpdateStatusAsync(
        string runActorId,
        string runId,
        ServiceRunStatus status,
        CancellationToken ct = default);
}

public sealed record ServiceRunRegistrationResult(
    string RunActorId,
    string RunId);
