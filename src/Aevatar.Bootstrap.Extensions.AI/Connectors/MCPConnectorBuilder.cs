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
        var hasCommand = !string.IsNullOrWhiteSpace(entry.MCP.Command);
        var hasUrl = !string.IsNullOrWhiteSpace(entry.MCP.Url);
        if (hasCommand == hasUrl)
        {
            logger.LogWarning(
                "Skip connector {Name}: exactly one of mcp.command or mcp.url is required",
                entry.Name);
            return false;
        }

        HttpClient? transportHttpClient = null;
        if (hasUrl)
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

            // MCP may span discovery, fallback, and multi-round-trip tool calls. The actor-owned
            // workflow timeout governs the operation, so HttpClient must not terminate one HTTP leg.
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
            InitializationTimeout = TimeSpan.FromMilliseconds(
                Math.Clamp(entry.TimeoutMs <= 0 ? 30_000 : entry.TimeoutMs, 100, 300_000)),
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
