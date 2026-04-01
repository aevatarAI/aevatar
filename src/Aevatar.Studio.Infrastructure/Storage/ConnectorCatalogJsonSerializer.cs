using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.Storage;

internal static class ConnectorCatalogJsonSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task<IReadOnlyList<StoredConnectorDefinition>> ReadCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseConnectors(document.RootElement);
    }

    public static async Task WriteCatalogAsync(
        Stream stream,
        IReadOnlyList<StoredConnectorDefinition> connectors,
        CancellationToken cancellationToken)
    {
        var payload = new ConnectorJsonDocument
        {
            Connectors = connectors
                .Select(ToConnectorJsonEntry)
                .ToList(),
        };

        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
    }

    public static async Task<ParsedConnectorDraft> ReadDraftAsync(
        Stream stream,
        DateTimeOffset fallbackUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var updatedAtUtc = TryGetPropertyIgnoreCase(root, "updatedAtUtc", out var updatedAtNode) &&
                           updatedAtNode.ValueKind == JsonValueKind.String &&
                           DateTimeOffset.TryParse(updatedAtNode.GetString(), out var parsedUpdatedAt)
            ? parsedUpdatedAt
            : fallbackUpdatedAtUtc;

        var draftNode = TryGetPropertyIgnoreCase(root, "connector", out var connectorNode) ? connectorNode : root;
        var draft = draftNode.ValueKind == JsonValueKind.Object ? ParseConnector(draftNode, null) : null;
        return new ParsedConnectorDraft(updatedAtUtc, draft);
    }

    public static async Task WriteDraftAsync(
        Stream stream,
        StoredConnectorDefinition? draft,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var payload = new ConnectorDraftJsonDocument
        {
            UpdatedAtUtc = updatedAtUtc,
            Connector = draft is null ? new ConnectorJsonEntry() : ToConnectorJsonEntry(draft),
        };

        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
    }

    internal sealed record ParsedConnectorDraft(
        DateTimeOffset UpdatedAtUtc,
        StoredConnectorDefinition? Draft);

    private static ConnectorJsonEntry ToConnectorJsonEntry(StoredConnectorDefinition connector) =>
        new()
        {
            Name = connector.Name,
            Type = connector.Type,
            Enabled = connector.Enabled,
            TimeoutMs = connector.TimeoutMs,
            Retry = connector.Retry,
            Http = new HttpConnectorJsonConfig
            {
                BaseUrl = connector.Http.BaseUrl,
                AllowedMethods = connector.Http.AllowedMethods.ToArray(),
                AllowedPaths = connector.Http.AllowedPaths.ToArray(),
                AllowedInputKeys = connector.Http.AllowedInputKeys.ToArray(),
                DefaultHeaders = connector.Http.DefaultHeaders.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase),
                Auth = ToConnectorAuthJsonConfig(connector.Http.Auth),
            },
            Cli = new CliConnectorJsonConfig
            {
                Command = connector.Cli.Command,
                FixedArguments = connector.Cli.FixedArguments.ToArray(),
                AllowedOperations = connector.Cli.AllowedOperations.ToArray(),
                AllowedInputKeys = connector.Cli.AllowedInputKeys.ToArray(),
                WorkingDirectory = connector.Cli.WorkingDirectory,
                Environment = connector.Cli.Environment.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase),
            },
            Mcp = new McpConnectorJsonConfig
            {
                ServerName = connector.Mcp.ServerName,
                Command = connector.Mcp.Command,
                Url = connector.Mcp.Url,
                Arguments = connector.Mcp.Arguments.ToArray(),
                Environment = connector.Mcp.Environment.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase),
                AdditionalHeaders = connector.Mcp.AdditionalHeaders.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase),
                Auth = ToConnectorAuthJsonConfig(connector.Mcp.Auth),
                DefaultTool = connector.Mcp.DefaultTool,
                AllowedTools = connector.Mcp.AllowedTools.ToArray(),
                AllowedInputKeys = connector.Mcp.AllowedInputKeys.ToArray(),
            },
        };

    private static IReadOnlyList<StoredConnectorDefinition> ParseConnectors(JsonElement root)
    {
        var connectorsNode = TryGetPropertyIgnoreCase(root, "connectors", out var configuredNode)
            ? configuredNode
            : root;

        var results = new List<StoredConnectorDefinition>();
        if (connectorsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in connectorsNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var connector = ParseConnector(item, null);
                if (connector is not null)
                {
                    results.Add(connector);
                }
            }

            return results;
        }

        if (connectorsNode.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (TryGetPropertyIgnoreCase(connectorsNode, "definitions", out var definitionsNode) &&
            definitionsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in definitionsNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var connector = ParseConnector(item, null);
                if (connector is not null)
                {
                    results.Add(connector);
                }
            }

            return results;
        }

        foreach (var property in connectorsNode.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var connector = ParseConnector(property.Value, property.Name);
            if (connector is not null)
            {
                results.Add(connector);
            }
        }

        return results;
    }

    private static StoredConnectorDefinition? ParseConnector(JsonElement connectorNode, string? fallbackName)
    {
        var name = ReadString(connectorNode, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = fallbackName ?? string.Empty;
        }

        var type = ReadString(connectorNode, "type");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        return new StoredConnectorDefinition(
            Name: name,
            Type: type,
            Enabled: ReadBool(connectorNode, "enabled", true),
            TimeoutMs: Math.Clamp(ReadInt(connectorNode, "timeoutMs", 30_000), 100, 300_000),
            Retry: Math.Clamp(ReadInt(connectorNode, "retry", 0), 0, 5),
            Http: TryGetPropertyIgnoreCase(connectorNode, "http", out var httpNode) ? ParseHttpConfig(httpNode) : EmptyHttpConfig(),
            Cli: TryGetPropertyIgnoreCase(connectorNode, "cli", out var cliNode) ? ParseCliConfig(cliNode) : EmptyCliConfig(),
            Mcp: TryGetPropertyIgnoreCase(connectorNode, "mcp", out var mcpNode) ? ParseMcpConfig(mcpNode) : EmptyMcpConfig());
    }

    private static StoredHttpConnectorConfig ParseHttpConfig(JsonElement node) =>
        node.ValueKind != JsonValueKind.Object
            ? EmptyHttpConfig()
            : new StoredHttpConnectorConfig(
                BaseUrl: ReadString(node, "baseUrl"),
                AllowedMethods: ReadStringArray(node, "allowedMethods"),
                AllowedPaths: ReadStringArray(node, "allowedPaths"),
                AllowedInputKeys: ReadStringArray(node, "allowedInputKeys"),
                DefaultHeaders: ReadStringMap(node, "defaultHeaders"),
                Auth: TryGetPropertyIgnoreCase(node, "auth", out var authNode) ? ParseAuthConfig(authNode) : EmptyAuthConfig());

    private static StoredCliConnectorConfig ParseCliConfig(JsonElement node) =>
        node.ValueKind != JsonValueKind.Object
            ? EmptyCliConfig()
            : new StoredCliConnectorConfig(
                Command: ReadString(node, "command"),
                FixedArguments: ReadStringArray(node, "fixedArguments"),
                AllowedOperations: ReadStringArray(node, "allowedOperations"),
                AllowedInputKeys: ReadStringArray(node, "allowedInputKeys"),
                WorkingDirectory: ReadString(node, "workingDirectory"),
                Environment: ReadStringMap(node, "environment"));

    private static StoredMcpConnectorConfig ParseMcpConfig(JsonElement node) =>
        node.ValueKind != JsonValueKind.Object
            ? EmptyMcpConfig()
            : new StoredMcpConnectorConfig(
                ServerName: ReadString(node, "serverName"),
                Command: ReadString(node, "command"),
                Url: ReadString(node, "url"),
                Arguments: ReadStringArray(node, "arguments"),
                Environment: ReadStringMap(node, "environment"),
                AdditionalHeaders: ReadStringMap(node, "additionalHeaders"),
                Auth: TryGetPropertyIgnoreCase(node, "auth", out var authNode) ? ParseAuthConfig(authNode) : EmptyAuthConfig(),
                DefaultTool: ReadString(node, "defaultTool"),
                AllowedTools: ReadStringArray(node, "allowedTools"),
                AllowedInputKeys: ReadStringArray(node, "allowedInputKeys"));

    private static StoredConnectorAuthConfig ParseAuthConfig(JsonElement node) =>
        node.ValueKind != JsonValueKind.Object
            ? EmptyAuthConfig()
            : new StoredConnectorAuthConfig(
                Type: ReadString(node, "type"),
                TokenUrl: ReadString(node, "tokenUrl"),
                ClientId: ReadString(node, "clientId"),
                ClientSecret: ReadString(node, "clientSecret"),
                Scope: ReadString(node, "scope"));

    private static StoredHttpConnectorConfig EmptyHttpConfig() =>
        new(string.Empty, [], [], [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), EmptyAuthConfig());

    private static StoredCliConnectorConfig EmptyCliConfig() =>
        new(string.Empty, [], [], [], string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static StoredMcpConnectorConfig EmptyMcpConfig() =>
        new(
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            EmptyAuthConfig(),
            string.Empty,
            [],
            []);

    private static StoredConnectorAuthConfig EmptyAuthConfig() =>
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string ReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool ReadBool(JsonElement element, string propertyName, bool fallback)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)
            ? parsed
            : fallback;
    }

    private static int ReadInt(JsonElement element, string propertyName, int fallback)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numberValue))
        {
            return numberValue;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out numberValue)
            ? numberValue
            : fallback;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.ToString();
        }

        return result;
    }

    private static ConnectorAuthJsonConfig ToConnectorAuthJsonConfig(StoredConnectorAuthConfig auth) =>
        new()
        {
            Type = auth.Type,
            TokenUrl = auth.TokenUrl,
            ClientId = auth.ClientId,
            ClientSecret = auth.ClientSecret,
            Scope = auth.Scope,
        };

    private sealed class ConnectorJsonDocument
    {
        [JsonPropertyName("connectors")]
        public List<ConnectorJsonEntry> Connectors { get; set; } = [];
    }

    private sealed class ConnectorDraftJsonDocument
    {
        [JsonPropertyName("updatedAtUtc")]
        public DateTimeOffset UpdatedAtUtc { get; set; }

        [JsonPropertyName("connector")]
        public ConnectorJsonEntry Connector { get; set; } = new();
    }

    private sealed class ConnectorJsonEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("timeoutMs")]
        public int TimeoutMs { get; set; } = 30_000;

        [JsonPropertyName("retry")]
        public int Retry { get; set; }

        [JsonPropertyName("http")]
        public HttpConnectorJsonConfig Http { get; set; } = new();

        [JsonPropertyName("cli")]
        public CliConnectorJsonConfig Cli { get; set; } = new();

        [JsonPropertyName("mcp")]
        public McpConnectorJsonConfig Mcp { get; set; } = new();
    }

    private sealed class HttpConnectorJsonConfig
    {
        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; set; } = string.Empty;

        [JsonPropertyName("allowedMethods")]
        public string[] AllowedMethods { get; set; } = [];

        [JsonPropertyName("allowedPaths")]
        public string[] AllowedPaths { get; set; } = [];

        [JsonPropertyName("allowedInputKeys")]
        public string[] AllowedInputKeys { get; set; } = [];

        [JsonPropertyName("defaultHeaders")]
        public Dictionary<string, string> DefaultHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("auth")]
        public ConnectorAuthJsonConfig Auth { get; set; } = new();
    }

    private sealed class CliConnectorJsonConfig
    {
        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;

        [JsonPropertyName("fixedArguments")]
        public string[] FixedArguments { get; set; } = [];

        [JsonPropertyName("allowedOperations")]
        public string[] AllowedOperations { get; set; } = [];

        [JsonPropertyName("allowedInputKeys")]
        public string[] AllowedInputKeys { get; set; } = [];

        [JsonPropertyName("workingDirectory")]
        public string WorkingDirectory { get; set; } = string.Empty;

        [JsonPropertyName("environment")]
        public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class McpConnectorJsonConfig
    {
        [JsonPropertyName("serverName")]
        public string ServerName { get; set; } = string.Empty;

        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public string[] Arguments { get; set; } = [];

        [JsonPropertyName("environment")]
        public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("additionalHeaders")]
        public Dictionary<string, string> AdditionalHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("auth")]
        public ConnectorAuthJsonConfig Auth { get; set; } = new();

        [JsonPropertyName("defaultTool")]
        public string DefaultTool { get; set; } = string.Empty;

        [JsonPropertyName("allowedTools")]
        public string[] AllowedTools { get; set; } = [];

        [JsonPropertyName("allowedInputKeys")]
        public string[] AllowedInputKeys { get; set; } = [];
    }

    private sealed class ConnectorAuthJsonConfig
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("tokenUrl")]
        public string TokenUrl { get; set; } = string.Empty;

        [JsonPropertyName("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [JsonPropertyName("clientSecret")]
        public string ClientSecret { get; set; } = string.Empty;

        [JsonPropertyName("scope")]
        public string Scope { get; set; } = string.Empty;
    }
}
