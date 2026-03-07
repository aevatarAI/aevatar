namespace Aevatar.Scripting.Core.Ports;

public interface IScriptingActorAddressResolver
{
    string GetEvolutionSessionActorId(string proposalId);

    string GetCatalogActorId();

    string GetDefinitionActorId(string scriptId);
}
