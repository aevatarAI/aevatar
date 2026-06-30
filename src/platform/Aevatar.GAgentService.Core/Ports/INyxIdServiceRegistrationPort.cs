using Aevatar.GAgentService.Core.Models;

namespace Aevatar.GAgentService.Core.Ports;

public interface INyxIdServiceRegistrationPort
{
    Task<NyxIdServiceRegistrationResult> RegisterAsync(
        NyxIdServiceRegistrationRequest request,
        CancellationToken ct = default);

    Task<NyxIdServiceRegistrationResult> UpdateAsync(
        NyxIdServiceRegistrationRequest request,
        CancellationToken ct = default);

    Task<NyxIdServiceLookupResult> GetAsync(
        NyxIdServiceLookupRequest request,
        CancellationToken ct = default);

    Task<NyxIdServiceRetirementResult> RetireAsync(
        NyxIdServiceRetirementRequest request,
        CancellationToken ct = default);
}
