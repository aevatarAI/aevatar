using Aevatar.AI.ToolProviders.MCP;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Connectors;

public sealed class MCPConnectorBuilder : IConnectorBuilder
{
    public string Type => "mcp";

    public bool TryBuild(ConnectorConfigEntry entry, ILogger logger, out IConnector? connector)
    {
        connector = null;
        if (string.IsNullOrWhiteSpace(entry.MCP.Command))
        {
            logger.LogWarning("Skip connector {Name}: mcp.command is required", entry.Name);
            return false;
        }

        var server = new MCPServerConfig
        {
            Name = string.IsNullOrWhiteSpace(entry.MCP.ServerName) ? entry.Name : entry.MCP.ServerName,
            Command = entry.MCP.Command,
            Arguments = entry.MCP.Arguments,
            Environment = entry.MCP.Environment,
        };

        connector = new MCPConnector(
            entry.Name,
            server,
            entry.MCP.DefaultTool,
            entry.MCP.AllowedTools,
            entry.MCP.AllowedInputKeys,
            logger: logger);
        return true;
    }
}
