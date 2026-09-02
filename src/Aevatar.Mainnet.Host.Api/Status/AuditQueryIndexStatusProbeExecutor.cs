using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.GAgents.StatusDashboard;
using Aevatar.GAgents.StatusDashboard.Executors;

namespace Aevatar.Mainnet.Host.Api.Status;

internal sealed class AuditQueryIndexStatusProbeExecutor : IHealthProbeExecutor
{
    private readonly IAuditTrailQueryPort _queryPort;

    public AuditQueryIndexStatusProbeExecutor(IAuditTrailQueryPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public string Kind => "audit_query_index";

    public async Task<HealthProbeOutcome> ProbeAsync(
        HealthProbeTargetDescriptor descriptor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var from = DateTimeOffset.UtcNow.AddDays(1);
        try
        {
            _ = await _queryPort.QueryAsync(
                new AuditTrailQuery
                {
                    OccurredFrom = from,
                    OccurredTo = from.AddMinutes(1),
                    Take = 1,
                },
                ct);
            return new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Ok,
                Detail = "audit_query_index_available",
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Down,
                Detail = $"audit_query_index_unavailable:{exception.GetType().Name}",
                ErrorMessage = "Audit trail query/index probe failed.",
            };
        }
    }
}
