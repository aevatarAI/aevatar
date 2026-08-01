using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.Bootstrap.Connectors;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Extensions.AI.Connectors;

public sealed class MCPConnectorBuilder : IConnectorBuilder
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IAgentToolExecutionPort? _toolExecutionPort;

    public MCPConnectorBuilder()
    {
    }

    public MCPConnectorBuilder(
        IHttpClientFactory httpClientFactory,
        IAgentToolExecutionPort toolExecutionPort)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _toolExecutionPort = toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
    }

    public string Type => "mcp";

    public bool TryBuild(ConnectorConfigEntry entry, ILogger logger, out IConnector? connector)
    {
        connector = null;
        if (string.IsNullOrWhiteSpace(entry.MCP.Command) &&
            string.IsNullOrWhiteSpace(entry.MCP.Url))
        {
            logger.LogWarning("Skip connector {Name}: mcp.command or mcp.url is required", entry.Name);
            return false;
        }

        HttpClient? transportHttpClient = null;
        if (!string.IsNullOrWhiteSpace(entry.MCP.Url))
        {
            var innerHandler = new HttpClientHandler();
            if (ClientCredentialsConnectorAuthorizationProvider.IsConfigured(entry.MCP.Auth))
            {
                var authorizationProvider = new ClientCredentialsConnectorAuthorizationProvider(
                    entry.MCP.Auth,
                    _httpClientFactory,
                    BuildHttpClientName(entry.Name));
                transportHttpClient = new HttpClient(
                    new ConnectorRequestAuthorizationHandler(authorizationProvider, innerHandler));
            }
            else
            {
                transportHttpClient = new HttpClient(innerHandler);
            }

            // Remote MCP uses a long-lived session transport, so request-scoped timeout belongs to workflow
            // cancellation, not HttpClient.Timeout.
            transportHttpClient.Timeout = Timeout.InfiniteTimeSpan;
        }

        var server = new MCPServerConfig
        {
            Name = string.IsNullOrWhiteSpace(entry.MCP.ServerName) ? entry.Name : entry.MCP.ServerName,
            Command = entry.MCP.Command,
            Url = entry.MCP.Url,
            Arguments = entry.MCP.Arguments,
            Environment = entry.MCP.Environment,
            AdditionalHeaders = entry.MCP.AdditionalHeaders,
            HttpClient = transportHttpClient,
            OwnsHttpClient = transportHttpClient != null,
        };

        connector = new MCPConnector(
            entry.Name,
            server,
            entry.MCP.DefaultTool,
            entry.MCP.AllowedTools,
            entry.MCP.AllowedInputKeys,
            toolExecutionPort: _toolExecutionPort
                ?? throw new InvalidOperationException("IAgentToolExecutionPort is required to build MCP connectors."),
            logger: logger);
        return true;
    }

    private static string BuildHttpClientName(string connectorName)
    {
        var normalized = string.IsNullOrWhiteSpace(connectorName)
            ? "default"
            : new string(connectorName.Trim()
                .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
                .ToArray());
        return $"aevatar.connector.mcp.{normalized}";
    }
}
