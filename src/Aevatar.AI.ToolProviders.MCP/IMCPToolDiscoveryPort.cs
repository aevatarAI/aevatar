using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.MCP;

public interface IMCPToolDiscoveryPort
{
    Task<IReadOnlyList<IAgentTool>> ConnectAndDiscoverAsync(
        MCPServerConfig config,
        CancellationToken ct = default);
}
