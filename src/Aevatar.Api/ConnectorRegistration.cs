// ─────────────────────────────────────────────────────────────
// ConnectorRegistration — 从 ~/.aevatar/connectors.json 加载并注册到 IConnectorRegistry
// ─────────────────────────────────────────────────────────────

using Aevatar.Cognitive.Connectors;
using Aevatar.Config;
using Aevatar.Connectors;
using Aevatar.AI.MCP;
using Microsoft.Extensions.Logging;

namespace Aevatar.Api;

internal static class ConnectorRegistration
{
    public static void RegisterConnectors(
        IConnectorRegistry registry,
        ILogger logger,
        string? connectorsJsonPath = null)
    {
        var entries = AevatarConnectorConfig.LoadConnectors(connectorsJsonPath);
        if (entries.Count == 0)
            return;

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
                    break;

                case "mcp":
                    if (string.IsNullOrWhiteSpace(entry.Mcp.Command))
                    {
                        logger.LogWarning("Skip connector {Name}: mcp.command is required", entry.Name);
                        break;
                    }
                    var server = new MCPServerConfig
                    {
                        Name = string.IsNullOrWhiteSpace(entry.Mcp.ServerName) ? entry.Name : entry.Mcp.ServerName,
                        Command = entry.Mcp.Command,
                        Arguments = entry.Mcp.Arguments,
                        Environment = entry.Mcp.Environment,
                    };
                    registry.Register(new MCPConnector(
                        entry.Name,
                        server,
                        entry.Mcp.DefaultTool,
                        entry.Mcp.AllowedTools,
                        entry.Mcp.AllowedInputKeys,
                        logger: logger));
                    break;

                default:
                    logger.LogWarning("Skip connector {Name}: unsupported type {Type}", entry.Name, entry.Type);
                    break;
            }
        }
    }
}
