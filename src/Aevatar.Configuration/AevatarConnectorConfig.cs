using System.Text.Json;

namespace Aevatar.Configuration;

/// <summary>
/// Named connector definition loaded from connectors.json.
/// </summary>
public sealed class ConnectorConfigEntry
{
    public required string Name { get; init; }
    public string Type { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public int TimeoutMs { get; init; } = 30_000;
    public int Retry { get; init; } = 0;
    public HttpConnectorConfig Http { get; init; } = new();
    public CliConnectorConfig Cli { get; init; } = new();
    public McpConnectorConfig Mcp { get; init; } = new();
}

/// <summary>HTTP connector policy settings.</summary>
public sealed class HttpConnectorConfig
{
    public string BaseUrl { get; init; } = "";
    public string[] AllowedMethods { get; init; } = ["POST"];
    public string[] AllowedPaths { get; init; } = ["/"];
    public string[] AllowedInputKeys { get; init; } = [];
    public Dictionary<string, string> DefaultHeaders { get; init; } = [];
}

/// <summary>CLI connector policy settings.</summary>
public sealed class CliConnectorConfig
{
    public string Command { get; init; } = "";
    public string[] FixedArguments { get; init; } = [];
    public string[] AllowedOperations { get; init; } = [];
    public string[] AllowedInputKeys { get; init; } = [];
    public string WorkingDirectory { get; init; } = "";
    public Dictionary<string, string> Environment { get; init; } = [];
}

/// <summary>MCP connector policy settings.</summary>
public sealed class McpConnectorConfig
{
    public string ServerName { get; init; } = "";
    public string Command { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public Dictionary<string, string> Environment { get; init; } = [];
    public string DefaultTool { get; init; } = "";
    public string[] AllowedTools { get; init; } = [];
    public string[] AllowedInputKeys { get; init; } = [];
}

/// <summary>
/// Loads connector settings from ~/.aevatar/connectors.json.
/// Supported shapes:
/// 1) { "connectors": [ { "name": "...", ... } ] }
/// 2) { "connectors": { "my_name": { ... } } }
/// 3) { "connectors": { "definitions": [ ... ] } }
/// </summary>
public static class AevatarConnectorConfig
{
    public static IReadOnlyList<ConnectorConfigEntry> LoadConnectors(string? filePath = null)
    {
        var path = filePath ?? AevatarPaths.ConnectorsJson;
        if (!File.Exists(path)) return [];

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!TryGetPropertyIgnoreCase(root, "connectors", out var connectorsNode))
                return [];

            var entries = ParseConnectorsNode(connectorsNode);
            return entries
                .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Type))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static List<ConnectorConfigEntry> ParseConnectorsNode(JsonElement node)
    {
        var result = new List<ConnectorConfigEntry>();

        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var entry = ParseConnector(item, null);
                if (entry != null) result.Add(entry);
            }
            return result;
        }

        if (node.ValueKind != JsonValueKind.Object) return result;

        if (TryGetPropertyIgnoreCase(node, "definitions", out var defs) && defs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in defs.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var entry = ParseConnector(item, null);
                if (entry != null) result.Add(entry);
            }
            return result;
        }

        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var entry = ParseConnector(prop.Value, prop.Name);
            if (entry != null) result.Add(entry);
        }

        return result;
    }

    private static ConnectorConfigEntry? ParseConnector(JsonElement obj, string? fallbackName)
    {
        var name = ReadString(obj, "name");
        if (string.IsNullOrWhiteSpace(name))
            name = fallbackName ?? "";

        var type = ReadString(obj, "type");
        if (string.IsNullOrWhiteSpace(type)) return null;

        var enabled = ReadBool(obj, "enabled", true);
        var timeoutMs = Math.Clamp(ReadInt(obj, "timeoutMs", 30_000), 100, 300_000);
        var retry = Math.Clamp(ReadInt(obj, "retry", 0), 0, 5);

        var http = TryGetPropertyIgnoreCase(obj, "http", out var httpNode)
            ? ParseHttp(httpNode)
            : new HttpConnectorConfig();
        var cli = TryGetPropertyIgnoreCase(obj, "cli", out var cliNode)
            ? ParseCli(cliNode)
            : new CliConnectorConfig();
        var mcp = TryGetPropertyIgnoreCase(obj, "mcp", out var mcpNode)
            ? ParseMcp(mcpNode)
            : new McpConnectorConfig();

        return new ConnectorConfigEntry
        {
            Name = name,
            Type = type,
            Enabled = enabled,
            TimeoutMs = timeoutMs,
            Retry = retry,
            Http = http,
            Cli = cli,
            Mcp = mcp,
        };
    }

    private static HttpConnectorConfig ParseHttp(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return new HttpConnectorConfig();
        return new HttpConnectorConfig
        {
            BaseUrl = ReadString(obj, "baseUrl"),
            AllowedMethods = ReadStringArray(obj, "allowedMethods"),
            AllowedPaths = ReadStringArray(obj, "allowedPaths"),
            AllowedInputKeys = ReadStringArray(obj, "allowedInputKeys"),
            DefaultHeaders = ReadStringMap(obj, "defaultHeaders"),
        };
    }

    private static CliConnectorConfig ParseCli(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return new CliConnectorConfig();
        return new CliConnectorConfig
        {
            Command = ReadString(obj, "command"),
            FixedArguments = ReadStringArray(obj, "fixedArguments"),
            AllowedOperations = ReadStringArray(obj, "allowedOperations"),
            AllowedInputKeys = ReadStringArray(obj, "allowedInputKeys"),
            WorkingDirectory = ReadString(obj, "workingDirectory"),
            Environment = ReadStringMap(obj, "environment"),
        };
    }

    private static McpConnectorConfig ParseMcp(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return new McpConnectorConfig();
        return new McpConnectorConfig
        {
            ServerName = ReadString(obj, "serverName"),
            Command = ReadString(obj, "command"),
            Arguments = ReadStringArray(obj, "arguments"),
            Environment = ReadStringMap(obj, "environment"),
            DefaultTool = ReadString(obj, "defaultTool"),
            AllowedTools = ReadStringArray(obj, "allowedTools"),
            AllowedInputKeys = ReadStringArray(obj, "allowedInputKeys"),
        };
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string ReadString(JsonElement obj, string name) =>
        TryGetPropertyIgnoreCase(obj, name, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() ?? ""
            : "";

    private static int ReadInt(JsonElement obj, string name, int fallback)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var val)) return fallback;
        if (val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var i)) return i;
        if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out i)) return i;
        return fallback;
    }

    private static bool ReadBool(JsonElement obj, string name, bool fallback)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var val)) return fallback;
        if (val.ValueKind == JsonValueKind.True) return true;
        if (val.ValueKind == JsonValueKind.False) return false;
        if (val.ValueKind == JsonValueKind.String && bool.TryParse(val.GetString(), out var b)) return b;
        return fallback;
    }

    private static string[] ReadStringArray(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var val) || val.ValueKind != JsonValueKind.Array) return [];
        return val.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var val) || val.ValueKind != JsonValueKind.Object) return [];
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in val.EnumerateObject())
        {
            map[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString();
        }
        return map;
    }
}
