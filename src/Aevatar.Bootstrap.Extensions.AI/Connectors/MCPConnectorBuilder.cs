using Aevatar.AI.ToolProviders.MCP;
using Aevatar.Bootstrap.Connectors;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Extensions.AI.Connectors;

public sealed class MCPConnectorBuilder : IConnectorBuilder
{
    public string Type => "mcp";

    /// <summary>
    /// Convert a <see cref="ConnectorConfigEntry"/> with type "mcp" into an <see cref="MCPServerConfig"/>.
    /// Returns null when the entry has neither a URL nor a command.
    /// </summary>
    public static MCPServerConfig? ToMCPServerConfig(ConnectorConfigEntry entry)
    {
        var hasUrl = !string.IsNullOrWhiteSpace(entry.MCP.Url);
        var hasCommand = !string.IsNullOrWhiteSpace(entry.MCP.Command);

        if (!hasUrl && !hasCommand)
            return null;

        MCPAuthConfig? auth = null;
        if (entry.MCP.Auth is { } authConfig && !string.IsNullOrWhiteSpace(authConfig.TokenUrl))
        {
            auth = new MCPAuthConfig
            {
                Type = authConfig.Type,
                TokenUrl = authConfig.TokenUrl,
                ClientId = authConfig.ClientId,
                ClientSecret = authConfig.ClientSecret,
                Scope = authConfig.Scope,
            };
        }

        return new MCPServerConfig
        {
            Name = string.IsNullOrWhiteSpace(entry.MCP.ServerName) ? entry.Name : entry.MCP.ServerName,
            Command = hasCommand ? entry.MCP.Command : null,
            Url = hasUrl ? entry.MCP.Url : null,
            Arguments = entry.MCP.Arguments,
            Environment = entry.MCP.Environment,
            Headers = entry.MCP.Headers,
            Auth = auth,
        };
    }

    public bool TryBuild(ConnectorConfigEntry entry, ILogger logger, out IConnector? connector)
    {
        connector = null;
        var server = ToMCPServerConfig(entry);
        if (server is null)
        {
            logger.LogWarning("Skip connector {Name}: mcp requires either url or command", entry.Name);
            return false;
        }

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
