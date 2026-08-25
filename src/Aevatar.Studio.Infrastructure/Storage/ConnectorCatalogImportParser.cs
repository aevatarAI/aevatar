using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ConnectorCatalogImportParser : IConnectorCatalogImportParser
{
    public async Task<IReadOnlyList<StoredConnectorDefinition>> ParseCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseConnectors(document.RootElement);
    }

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
            Mcp: TryGetPropertyIgnoreCase(connectorNode, "mcp", out var mcpNode) ? ParseMcpConfig(mcpNode) : EmptyMcpConfig(),
            HostCallback: TryGetPropertyIgnoreCase(connectorNode, "host_callback", out var hostCallbackNode)
                ? ParseHostCallbackConfig(hostCallbackNode)
                : TryGetPropertyIgnoreCase(connectorNode, "hostCallback", out hostCallbackNode)
                    ? ParseHostCallbackConfig(hostCallbackNode)
                    : EmptyHostCallbackConfig());
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

    private static StoredHostCallbackConnectorConfig ParseHostCallbackConfig(JsonElement node) =>
        node.ValueKind != JsonValueKind.Object
            ? EmptyHostCallbackConfig()
            : new StoredHostCallbackConnectorConfig(
                Handler: ReadString(node, "handler"),
                AllowedOperations: ReadStringArray(node, "allowedOperations"),
                AllowedInputKeys: ReadStringArray(node, "allowedInputKeys"));

    private static StoredConnectorAuthConfig ParseAuthConfig(JsonElement node) =>
        node.ValueKind != JsonValueKind.Object
            ? EmptyAuthConfig()
            : new StoredConnectorAuthConfig(
                Type: ReadString(node, "type"),
                TokenUrl: ReadString(node, "tokenUrl"),
                ClientId: ReadString(node, "clientId"),
                ClientSecret: ReadString(node, "clientSecret"),
                Scope: ReadString(node, "scope"),
                SecretRef: ReadString(node, "secretRef"),
                HeaderName: ReadString(node, "headerName"),
                HeaderValuePrefix: ReadString(node, "headerValuePrefix"));

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

    private static StoredHostCallbackConnectorConfig EmptyHostCallbackConfig() =>
        new(string.Empty, [], []);

    private static StoredConnectorAuthConfig EmptyAuthConfig() =>
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

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
}
