using System.Text.Json;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Infrastructure.Storage;

internal static class ConnectorCatalogStorageSerializer
{
    // Refactor (iter22/cluster-001-studio-json-internal-catalog-storage):
    //   Old pattern: Studio connector catalog and draft facts were durable JSON documents.
    //   New principle: Durable storage payloads are protobuf facts; JSON is only import fallback.
    public static async Task<IReadOnlyList<StoredConnectorDefinition>> ReadCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var payload = await ReadPayloadAsync(stream, cancellationToken);
        if (!IsJsonPayload(payload))
        {
            var state = ConnectorCatalogState.Parser.ParseFrom(payload);
            return state.Connectors
                .Select(ToStoredConnectorDefinition)
                .ToList()
                .AsReadOnly();
        }

        using var document = JsonDocument.Parse(payload);
        return ParseConnectors(document.RootElement);
    }

    // Refactor (iter22/cluster-001-studio-json-internal-catalog-storage):
    //   Old pattern: Studio connector catalog writes emitted durable JSON.
    //   New principle: Catalog writes emit the protobuf catalog state fact.
    public static async Task WriteCatalogAsync(
        Stream stream,
        IReadOnlyList<StoredConnectorDefinition> connectors,
        CancellationToken cancellationToken)
    {
        var payload = new ConnectorCatalogState();
        payload.Connectors.AddRange(connectors.Select(ToProtoConnectorDefinition));
        await stream.WriteAsync(payload.ToByteArray(), cancellationToken);
    }

    // Refactor (iter22/cluster-001-studio-json-internal-catalog-storage):
    //   Old pattern: Studio connector draft reads treated JSON as the durable format.
    //   New principle: Draft reads prefer protobuf and keep JSON only as a bounded legacy import fallback.
    public static async Task<ParsedConnectorDraft> ReadDraftAsync(
        Stream stream,
        DateTimeOffset fallbackUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        var payload = await ReadPayloadAsync(stream, cancellationToken);
        if (!IsJsonPayload(payload))
        {
            var draftEntry = ConnectorDraftEntry.Parser.ParseFrom(payload);
            var protobufUpdatedAtUtc = draftEntry.UpdatedAtUtc?.ToDateTimeOffset() ?? fallbackUpdatedAtUtc;
            var protobufDraft = draftEntry.Draft is not null ? ToStoredConnectorDefinition(draftEntry.Draft) : null;
            return new ParsedConnectorDraft(protobufUpdatedAtUtc, protobufDraft);
        }

        using var document = JsonDocument.Parse(payload);
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

    // Refactor (iter22/cluster-001-studio-json-internal-catalog-storage):
    //   Old pattern: Studio connector draft writes emitted durable JSON.
    //   New principle: Draft writes emit the protobuf draft fact.
    public static async Task WriteDraftAsync(
        Stream stream,
        StoredConnectorDefinition? draft,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var payload = new ConnectorDraftEntry
        {
            Draft = draft is not null ? ToProtoConnectorDefinition(draft) : null,
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(updatedAtUtc),
        };

        await stream.WriteAsync(payload.ToByteArray(), cancellationToken);
    }

    internal sealed record ParsedConnectorDraft(
        DateTimeOffset UpdatedAtUtc,
        StoredConnectorDefinition? Draft);

    private static async Task<byte[]> ReadPayloadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static bool IsJsonPayload(ReadOnlySpan<byte> payload)
    {
        foreach (var value in payload)
        {
            if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            return value is (byte)'{' or (byte)'[';
        }

        return false;
    }

    private static StoredConnectorDefinition ToStoredConnectorDefinition(ConnectorDefinitionEntry entry) =>
        new(
            Name: entry.Name,
            Type: entry.Type,
            Enabled: entry.Enabled,
            TimeoutMs: entry.TimeoutMs,
            Retry: entry.Retry,
            Http: entry.Http is not null ? ToStoredHttpConfig(entry.Http) : EmptyHttpConfig(),
            Cli: entry.Cli is not null ? ToStoredCliConfig(entry.Cli) : EmptyCliConfig(),
            Mcp: entry.Mcp is not null ? ToStoredMcpConfig(entry.Mcp) : EmptyMcpConfig());

    private static StoredHttpConnectorConfig ToStoredHttpConfig(HttpConnectorConfigEntry entry) =>
        new(
            BaseUrl: entry.BaseUrl,
            AllowedMethods: entry.AllowedMethods.ToList().AsReadOnly(),
            AllowedPaths: entry.AllowedPaths.ToList().AsReadOnly(),
            AllowedInputKeys: entry.AllowedInputKeys.ToList().AsReadOnly(),
            DefaultHeaders: new Dictionary<string, string>(entry.DefaultHeaders, StringComparer.OrdinalIgnoreCase),
            Auth: entry.Auth is not null ? ToStoredAuthConfig(entry.Auth) : EmptyAuthConfig());

    private static StoredCliConnectorConfig ToStoredCliConfig(CliConnectorConfigEntry entry) =>
        new(
            Command: entry.Command,
            FixedArguments: entry.FixedArguments.ToList().AsReadOnly(),
            AllowedOperations: entry.AllowedOperations.ToList().AsReadOnly(),
            AllowedInputKeys: entry.AllowedInputKeys.ToList().AsReadOnly(),
            WorkingDirectory: entry.WorkingDirectory,
            Environment: new Dictionary<string, string>(entry.Environment, StringComparer.OrdinalIgnoreCase));

    private static StoredMcpConnectorConfig ToStoredMcpConfig(McpConnectorConfigEntry entry) =>
        new(
            ServerName: entry.ServerName,
            Command: entry.Command,
            Url: entry.Url,
            Arguments: entry.Arguments.ToList().AsReadOnly(),
            Environment: new Dictionary<string, string>(entry.Environment, StringComparer.OrdinalIgnoreCase),
            AdditionalHeaders: new Dictionary<string, string>(entry.AdditionalHeaders, StringComparer.OrdinalIgnoreCase),
            Auth: entry.Auth is not null ? ToStoredAuthConfig(entry.Auth) : EmptyAuthConfig(),
            DefaultTool: entry.DefaultTool,
            AllowedTools: entry.AllowedTools.ToList().AsReadOnly(),
            AllowedInputKeys: entry.AllowedInputKeys.ToList().AsReadOnly());

    private static StoredConnectorAuthConfig ToStoredAuthConfig(ConnectorAuthEntry entry) =>
        new(
            Type: entry.Type,
            TokenUrl: entry.TokenUrl,
            ClientId: entry.ClientId,
            ClientSecret: entry.ClientSecret,
            Scope: entry.Scope);

    private static ConnectorDefinitionEntry ToProtoConnectorDefinition(StoredConnectorDefinition def)
    {
        var entry = new ConnectorDefinitionEntry
        {
            Name = def.Name,
            Type = def.Type,
            Enabled = def.Enabled,
            TimeoutMs = def.TimeoutMs,
            Retry = def.Retry,
            Http = new HttpConnectorConfigEntry
            {
                BaseUrl = def.Http.BaseUrl,
                Auth = ToProtoAuthConfig(def.Http.Auth),
            },
            Cli = new CliConnectorConfigEntry
            {
                Command = def.Cli.Command,
                WorkingDirectory = def.Cli.WorkingDirectory,
            },
            Mcp = new McpConnectorConfigEntry
            {
                ServerName = def.Mcp.ServerName,
                Command = def.Mcp.Command,
                Url = def.Mcp.Url,
                Auth = ToProtoAuthConfig(def.Mcp.Auth),
                DefaultTool = def.Mcp.DefaultTool,
            },
        };

        entry.Http.AllowedMethods.AddRange(def.Http.AllowedMethods);
        entry.Http.AllowedPaths.AddRange(def.Http.AllowedPaths);
        entry.Http.AllowedInputKeys.AddRange(def.Http.AllowedInputKeys);
        AddMapEntries(entry.Http.DefaultHeaders, def.Http.DefaultHeaders);
        entry.Cli.FixedArguments.AddRange(def.Cli.FixedArguments);
        entry.Cli.AllowedOperations.AddRange(def.Cli.AllowedOperations);
        entry.Cli.AllowedInputKeys.AddRange(def.Cli.AllowedInputKeys);
        AddMapEntries(entry.Cli.Environment, def.Cli.Environment);
        entry.Mcp.Arguments.AddRange(def.Mcp.Arguments);
        AddMapEntries(entry.Mcp.Environment, def.Mcp.Environment);
        AddMapEntries(entry.Mcp.AdditionalHeaders, def.Mcp.AdditionalHeaders);
        entry.Mcp.AllowedTools.AddRange(def.Mcp.AllowedTools);
        entry.Mcp.AllowedInputKeys.AddRange(def.Mcp.AllowedInputKeys);
        return entry;
    }

    private static ConnectorAuthEntry ToProtoAuthConfig(StoredConnectorAuthConfig auth) =>
        new()
        {
            Type = auth.Type,
            TokenUrl = auth.TokenUrl,
            ClientId = auth.ClientId,
            ClientSecret = auth.ClientSecret,
            Scope = auth.Scope,
        };

    private static void AddMapEntries(
        Google.Protobuf.Collections.MapField<string, string> target,
        IReadOnlyDictionary<string, string> source)
    {
        foreach (var item in source)
        {
            target[item.Key] = item.Value;
        }
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
