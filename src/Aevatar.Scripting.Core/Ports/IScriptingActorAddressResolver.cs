namespace Aevatar.Scripting.Core.Ports;

public interface IScriptingActorAddressResolver
{
    string GetEvolutionManagerActorId();

    string GetEvolutionManagerActorId(string? scopeId) => GetEvolutionManagerActorId();

    string GetEvolutionSessionActorId(string proposalId);

    string GetEvolutionSessionActorId(string proposalId, string? scopeId) =>
        GetEvolutionSessionActorId(proposalId);

    string GetCatalogActorId();

    string GetCatalogActorId(string? scopeId) => GetCatalogActorId();

    string GetDefinitionActorId(string scriptId);

    string GetDefinitionActorId(string scriptId, string? scopeId) =>
        GetDefinitionActorId(scriptId);
}
