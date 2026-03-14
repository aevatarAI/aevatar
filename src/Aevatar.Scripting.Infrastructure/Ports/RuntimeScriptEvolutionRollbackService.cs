using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionRollbackService : IScriptEvolutionRollbackService
{
    private readonly IScriptCatalogCommandPort _catalogCommandPort;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public RuntimeScriptEvolutionRollbackService(
        IScriptCatalogCommandPort catalogCommandPort,
        IScriptingActorAddressResolver addressResolver)
    {
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
    }

    public Task RollbackAsync(
        ScriptRollbackRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var catalogActorId = string.IsNullOrWhiteSpace(request.CatalogActorId)
            ? _addressResolver.GetCatalogActorId()
            : request.CatalogActorId;

        return _catalogCommandPort.RollbackCatalogRevisionAsync(
            catalogActorId,
            request.ScriptId,
            request.TargetRevision,
            request.Reason,
            request.ProposalId,
            request.ExpectedCurrentRevision,
            ct);
    }
}
