using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.MCP;

public interface IMCPToolDiscoveryPort
{
    Task<MCPToolDiscoveryResult> ConnectAndDiscoverAsync(
        MCPServerConfig config,
        CancellationToken ct = default);
}

/// <summary>One MCP tool-list snapshot and its server-declared freshness.</summary>
/// <param name="Tools">Tools discovered across every result page.</param>
/// <param name="TimeToLive">
/// Maximum duration the snapshot may be reused. <see langword="null"/> means the caller's
/// configured topology is static and has no protocol-driven expiry.
/// </param>
public sealed record MCPToolDiscoveryResult(
    IReadOnlyList<IAgentTool> Tools,
    TimeSpan? TimeToLive);
