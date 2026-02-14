using Aevatar.AI.ToolProviders.MCP;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflows.Core.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap;

public static class ConnectorRegistration
{
    public static int RegisterConnectors(
        IConnectorRegistry registry,
        ILogger logger,
        string? connectorsJsonPath = null)
    {
        var entries = AevatarConnectorConfig.LoadConnectors(connectorsJsonPath);
        if (entries.Count == 0)
            return 0;

        var added = 0;
        foreach (var entry in entries)
        {
            switch (entry.Type.ToLowerInvariant())
            {
                case "http":
                    if (string.IsNullOrWhiteSpace(entry.Http.BaseUrl))
                    {
                        logger.LogWarning("Skip connector {Name}: http.baseUrl is required", entry.Name);
                        break;
                    }

                    registry.Register(new HttpConnector(
                        entry.Name,
                        entry.Http.BaseUrl,
                        entry.Http.AllowedMethods,
                        entry.Http.AllowedPaths,
                        entry.Http.AllowedInputKeys,
                        entry.Http.DefaultHeaders,
                        entry.TimeoutMs));
                    added++;
                    break;

                case "cli":
                    if (string.IsNullOrWhiteSpace(entry.Cli.Command))
                    {
                        logger.LogWarning("Skip connector {Name}: cli.command is required", entry.Name);
                        break;
                    }

                    if (entry.Cli.Command.Contains("://", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogWarning("Skip connector {Name}: cli.command must be a preinstalled command", entry.Name);
                        break;
                    }

                    registry.Register(new CliConnector(
                        entry.Name,
                        entry.Cli.Command,
                        entry.Cli.FixedArguments,
                        entry.Cli.AllowedOperations,
                        entry.Cli.AllowedInputKeys,
                        entry.Cli.WorkingDirectory,
                        entry.Cli.Environment,
                        entry.TimeoutMs));
                    added++;
                    break;

                case "mcp":
                    if (string.IsNullOrWhiteSpace(entry.MCP.Command))
                    {
                        logger.LogWarning("Skip connector {Name}: mcp.command is required", entry.Name);
                        break;
                    }

                    var server = new MCPServerConfig
                    {
                        Name = string.IsNullOrWhiteSpace(entry.MCP.ServerName) ? entry.Name : entry.MCP.ServerName,
                        Command = entry.MCP.Command,
                        Arguments = entry.MCP.Arguments,
                        Environment = entry.MCP.Environment,
                    };

                    registry.Register(new MCPConnector(
                        entry.Name,
                        server,
                        entry.MCP.DefaultTool,
                        entry.MCP.AllowedTools,
                        entry.MCP.AllowedInputKeys,
                        logger: logger));
                    added++;
                    break;

                default:
                    logger.LogWarning("Skip connector {Name}: unsupported type {Type}", entry.Name, entry.Type);
                    break;
            }
        }

        return added;
    }
}
