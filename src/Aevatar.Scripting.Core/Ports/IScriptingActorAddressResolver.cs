namespace Aevatar.Scripting.Core.Ports;

public interface IScriptingActorAddressResolver
{
    string GetEvolutionManagerActorId();

    string GetCatalogActorId();

    string GetDefinitionActorId(string scriptId);
}
