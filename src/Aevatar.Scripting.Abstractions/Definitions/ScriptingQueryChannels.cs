namespace Aevatar.Scripting.Abstractions.Definitions;

public static class ScriptingQueryChannels
{
    public const string DefinitionPublisherId = "scripting.query.definition";
    public const string CatalogPublisherId = "scripting.query.catalog";
    public const string EvolutionPublisherId = "scripting.query.evolution";

    public const string DefinitionReplyStreamPrefix = DefinitionPublisherId + ".reply";
    public const string CatalogReplyStreamPrefix = CatalogPublisherId + ".reply";
    public const string EvolutionReplyStreamPrefix = EvolutionPublisherId + ".reply";
}
