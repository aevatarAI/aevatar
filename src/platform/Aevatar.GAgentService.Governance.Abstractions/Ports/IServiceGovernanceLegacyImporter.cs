using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

/// <summary>
/// One-time migration port that imports legacy governance actor state into the
/// unified ServiceConfiguration model. The implementation necessarily reads
/// IEventStore directly because the legacy actors have no read models (they are
/// being retired by this migration). This is permitted under the migration /
/// disaster-recovery exemption.
/// <para>
/// Remove this interface and its implementation once all deployments have
/// completed the legacy governance migration.
/// </para>
/// </summary>
[Obsolete("Legacy migration port. Remove after all deployments complete governance migration.")]
public interface IServiceGovernanceLegacyImporter
{
    Task<bool> ImportIfNeededAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
