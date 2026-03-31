using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.Configuration;

namespace Aevatar.Tools.Cli.Hosting;

/// <summary>
/// Persists NyxID access token in ~/.aevatar/secrets.json
/// and user info in ~/.aevatar/config.json.
/// </summary>
internal static class NyxIdTokenStore
{
    private const string DefaultAuthority = "https://nyx.chrono-ai.fun";

    public static string ResolveAuthority()
    {
        var configured = CliAppConfigStore.TryGetConfigValue("Cli:App:NyxId:Authority");
        return string.IsNullOrWhiteSpace(configured) ? DefaultAuthority : configured.TrimEnd('/');
    }

    public static void SaveToken(string accessToken, string? email, string? name)
    {
        SaveSecret("NyxId:AccessToken", accessToken);

        if (email is not null || name is not null)
            SaveUserInfo(email, name);
    }

    public static string? LoadToken()
    {
        return LoadSecret("NyxId:AccessToken");
    }

    public static (string? Email, string? Name) LoadUserInfo()
    {
        var root = LoadConfigRoot();
        if (root["Cli"] is not JsonObject cli ||
            cli["App"] is not JsonObject app ||
            app["NyxId"] is not JsonObject nyxId ||
            nyxId["User"] is not JsonObject user)
        {
            return (null, null);
        }

        var email = user["Email"]?.GetValue<string>();
        var name = user["Name"]?.GetValue<string>();
        return (email, name);
    }

    public static void ClearToken()
    {
        RemoveSecret("NyxId:AccessToken");
        ClearUserInfo();
    }

    // ─── Secrets helpers ───

    private static void SaveSecret(string colonKey, string value)
    {
        var root = LoadSecretsRoot();
        var segments = colonKey.Split(':');
        var current = root;
        foreach (var segment in segments[..^1])
            current = EnsureObject(current, segment);
        current[segments[^1]] = value;
        SaveSecretsRoot(root);
    }

    private static string? LoadSecret(string colonKey)
    {
        JsonObject root;
        try { root = LoadSecretsRoot(); }
        catch { return null; }

        var segments = colonKey.Split(':');
        JsonNode? node = root;
        foreach (var segment in segments[..^1])
        {
            if (node is not JsonObject obj || obj[segment] is not JsonObject child)
                return null;
            node = child;
        }

        if (node is JsonObject parent &&
            parent[segments[^1]] is JsonValue val &&
            val.TryGetValue<string>(out var raw) &&
            !string.IsNullOrWhiteSpace(raw))
        {
            return raw.Trim();
        }

        return null;
    }

    private static void RemoveSecret(string colonKey)
    {
        JsonObject root;
        try { root = LoadSecretsRoot(); }
        catch { return; }

        var segments = colonKey.Split(':');
        JsonNode? node = root;
        var parents = new List<(JsonObject Parent, string Key)>();
        foreach (var segment in segments[..^1])
        {
            if (node is not JsonObject obj || obj[segment] is not JsonObject child)
                return;
            parents.Add((obj, segment));
            node = child;
        }

        if (node is JsonObject leaf)
        {
            leaf.Remove(segments[^1]);
            // Clean up empty parents
            for (var i = parents.Count - 1; i >= 0; i--)
            {
                var (p, k) = parents[i];
                if (p[k] is JsonObject child && child.Count == 0)
                    p.Remove(k);
                else
                    break;
            }
        }

        SaveSecretsRoot(root);
    }

    // ─── Config helpers ───

    private static void SaveUserInfo(string? email, string? name)
    {
        var root = LoadConfigRoot();
        var cli = EnsureObject(root, "Cli");
        var app = EnsureObject(cli, "App");
        var nyxId = EnsureObject(app, "NyxId");
        var user = EnsureObject(nyxId, "User");
        if (email is not null) user["Email"] = email;
        if (name is not null) user["Name"] = name;
        SaveConfigRoot(root);
    }

    private static void ClearUserInfo()
    {
        JsonObject root;
        try { root = LoadConfigRoot(); }
        catch { return; }

        if (root["Cli"] is not JsonObject cli ||
            cli["App"] is not JsonObject app ||
            app["NyxId"] is not JsonObject nyxId)
        {
            return;
        }

        nyxId.Remove("User");
        SaveConfigRoot(root);
    }

    // ─── JSON file I/O ───

    private static JsonObject LoadSecretsRoot()
    {
        var path = AevatarPaths.SecretsJson;
        if (!File.Exists(path)) return [];
        var raw = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return JsonNode.Parse(raw) as JsonObject ?? [];
    }

    private static void SaveSecretsRoot(JsonObject root)
    {
        var path = AevatarPaths.SecretsJson;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + Environment.NewLine);

        // Restrict permissions to owner-only on Unix
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best effort
            }
        }
    }

    private static JsonObject LoadConfigRoot()
    {
        var path = AevatarPaths.ConfigJson;
        if (!File.Exists(path)) return [];
        var raw = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return JsonNode.Parse(raw) as JsonObject ?? [];
    }

    private static void SaveConfigRoot(JsonObject root)
    {
        var path = AevatarPaths.ConfigJson;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + Environment.NewLine);
    }

    private static JsonObject EnsureObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject child)
            return child;
        child = [];
        parent[key] = child;
        return child;
    }
}
