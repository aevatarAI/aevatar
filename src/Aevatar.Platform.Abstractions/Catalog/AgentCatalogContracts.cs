namespace Aevatar.Platform.Abstractions.Catalog;

public sealed record AgentCapability(
    string AgentType,
    string Subsystem,
    string CommandEndpoint,
    string QueryEndpoint,
    string StreamEndpoint);

public interface IAgentCatalog
{
    IReadOnlyList<AgentCapability> List();
}

public interface IAgentCommandRouter
{
    Uri? Resolve(string subsystem, string command);
}

public interface IAgentQueryRouter
{
    Uri? Resolve(string subsystem, string query);
}
