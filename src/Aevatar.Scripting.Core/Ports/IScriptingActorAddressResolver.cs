namespace Aevatar.Scripting.Core.Ports;

public interface IScriptingActorAddressResolver
{
    string GetEvolutionManagerActorId();

    string GetEvolutionSessionActorId(string proposalId);

    string GetCatalogActorId();

    string GetDefinitionActorId(string scriptId);
}
