using System.Text.Json;
using System.Text.RegularExpressions;

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
    public MCPConnectorConfig MCP { get; init; } = new();
    public TelegramUserConnectorConfig TelegramUser { get; init; } = new();
}

/// <summary>HTTP connector policy settings.</summary>
public sealed class HttpConnectorConfig
{
    public string BaseUrl { get; init; } = "";
    public string[] AllowedMethods { get; init; } = ["POST"];
    public string[] AllowedPaths { get; init; } = ["/"];
    public string[] AllowedInputKeys { get; init; } = [];
    public Dictionary<string, string> DefaultHeaders { get; init; } = [];
    public ConnectorAuthConfig Auth { get; init; } = new();
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
public sealed class MCPConnectorConfig
{
    public string ServerName { get; init; } = "";
    public string Command { get; init; } = "";
    public string Url { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public Dictionary<string, string> Environment { get; init; } = [];
    public string DefaultTool { get; init; } = "";
    public string[] AllowedTools { get; init; } = [];
    public string[] AllowedInputKeys { get; init; } = [];
    public Dictionary<string, string> AdditionalHeaders { get; init; } = [];
    public ConnectorAuthConfig Auth { get; init; } = new();
}

/// <summary>Connector authentication policy settings.</summary>
public sealed class ConnectorAuthConfig
{
    public string Type { get; init; } = "";
    public string TokenUrl { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string Scope { get; init; } = "";
}

/// <summary>Telegram user-account connector settings (MTProto client).</summary>
public sealed class TelegramUserConnectorConfig
{
    public string ApiId { get; init; } = "";
    public string ApiHash { get; init; } = "";
    public string PhoneNumber { get; init; } = "";
    public string VerificationCode { get; init; } = "";
    public string Password { get; init; } = "";
    public string SessionPath { get; init; } = "";
    public string DeviceModel { get; init; } = "";
    public string SystemVersion { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public string SystemLangCode { get; init; } = "";
    public string LangCode { get; init; } = "";
    public string[] AllowedOperations { get; init; } = ["/sendMessage", "/getUpdates"];
}

/// <summary>
/// Loads connector settings from ~/.aevatar/connectors.json.
/// Supported shapes:
/// 1) { "connectors": [ { "name": "...", ... } ] }
/// 2) { "connectors": { "my_name": { ... } } }
/// 3) { "connectors": { "definitions": [ ... ] } }
/// </summary>
public static partial class AevatarConnectorConfig
{
    public static IReadOnlyList<ConnectorConfigEntry> LoadConnectors(string? filePath = null)
    {
        var path = filePath ?? AevatarPaths.ConnectorsJson;
        if (!File.Exists(path)) return [];

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var connectorsNode = TryGetPropertyIgnoreCase(root, "connectors", out var configuredNode)
                ? configuredNode
                : root;

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
            ? ParseMCP(mcpNode)
            : new MCPConnectorConfig();
        var telegramUser = TryGetPropertyIgnoreCase(obj, "telegramUser", out var telegramUserNode)
            ? ParseTelegramUser(telegramUserNode)
            : TryGetPropertyIgnoreCase(obj, "telegram_user", out telegramUserNode)
                ? ParseTelegramUser(telegramUserNode)
                : new TelegramUserConnectorConfig();

        return new ConnectorConfigEntry
        {
            Name = name,
            Type = type,
            Enabled = enabled,
            TimeoutMs = timeoutMs,
            Retry = retry,
            Http = http,
            Cli = cli,
            MCP = mcp,
            TelegramUser = telegramUser,
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
            Auth = TryGetPropertyIgnoreCase(obj, "auth", out var authNode)
                ? ParseAuth(authNode)
                : new ConnectorAuthConfig(),
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

    private static MCPConnectorConfig ParseMCP(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return new MCPConnectorConfig();
        return new MCPConnectorConfig
        {
            ServerName = ReadString(obj, "serverName"),
            Command = ReadString(obj, "command"),
            Url = ReadString(obj, "url"),
            Arguments = ReadStringArray(obj, "arguments"),
            Environment = ReadStringMap(obj, "environment"),
            DefaultTool = ReadString(obj, "defaultTool"),
            AllowedTools = ReadStringArray(obj, "allowedTools"),
            AllowedInputKeys = ReadStringArray(obj, "allowedInputKeys"),
            AdditionalHeaders = ReadStringMap(obj, "additionalHeaders"),
            Auth = TryGetPropertyIgnoreCase(obj, "auth", out var authNode)
                ? ParseAuth(authNode)
                : new ConnectorAuthConfig(),
        };
    }

    private static ConnectorAuthConfig ParseAuth(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return new ConnectorAuthConfig();

        return new ConnectorAuthConfig
        {
            Type = ReadString(obj, "type"),
            TokenUrl = ReadString(obj, "tokenUrl"),
            ClientId = ReadString(obj, "clientId"),
            ClientSecret = ReadString(obj, "clientSecret"),
            Scope = ReadString(obj, "scope"),
        };
    }

    private static TelegramUserConnectorConfig ParseTelegramUser(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return new TelegramUserConnectorConfig();

        var allowedOperations = ReadStringArray(obj, "allowedOperations");
        if (allowedOperations.Length == 0)
            allowedOperations = ["/sendMessage", "/getUpdates"];

        return new TelegramUserConnectorConfig
        {
            ApiId = ReadString(obj, "apiId"),
            ApiHash = ReadString(obj, "apiHash"),
            PhoneNumber = ReadString(obj, "phoneNumber"),
            VerificationCode = ReadString(obj, "verificationCode"),
            Password = ReadString(obj, "password"),
            SessionPath = ReadString(obj, "sessionPath"),
            DeviceModel = ReadString(obj, "deviceModel"),
            SystemVersion = ReadString(obj, "systemVersion"),
            AppVersion = ReadString(obj, "appVersion"),
            SystemLangCode = ReadString(obj, "systemLangCode"),
            LangCode = ReadString(obj, "langCode"),
            AllowedOperations = allowedOperations,
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
            ? ExpandEnvironmentPlaceholders(val.GetString() ?? "")
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
            .Select(x => x.ValueKind == JsonValueKind.String ? ExpandEnvironmentPlaceholders(x.GetString() ?? "") : "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var val) || val.ValueKind != JsonValueKind.Object) return [];
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in val.EnumerateObject())
        {
            map[prop.Name] = ExpandEnvironmentPlaceholders(prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString());
        }
        return map;
    }

    private static string ExpandEnvironmentPlaceholders(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return EnvironmentPlaceholderPattern().Replace(value, match =>
        {
            var variableName = match.Groups[1].Value;
            var envValue = Environment.GetEnvironmentVariable(variableName);
            return envValue ?? match.Value;
        });
    }

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled)]
    private static partial Regex EnvironmentPlaceholderPattern();
}
