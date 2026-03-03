using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class DefaultScriptingActorAddressResolver : IScriptingActorAddressResolver
{
    private const string EvolutionManagerActorId = "script-evolution-manager";
    private const string CatalogActorId = "script-catalog";

    public string GetEvolutionManagerActorId() => EvolutionManagerActorId;

    public string GetEvolutionSessionActorId(string proposalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        return $"script-evolution-session:{proposalId}";
    }

    public string GetCatalogActorId() => CatalogActorId;

    public string GetDefinitionActorId(string scriptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        return $"script-definition:{scriptId}";
    }
}
