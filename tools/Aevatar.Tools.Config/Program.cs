// Aevatar Config Tool — local web UI to configure ~/.aevatar/secrets.json (LLM API keys, etc.).
// Usage: aevatar-config [--no-browser] [--port <port>]
// Default: http://localhost:6677

using System.Diagnostics;
using Aevatar.Tools.Config;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aevatar.Configuration;
using Microsoft.AspNetCore.Http.Json;

#if AEVATAR_CONFIG_TOOL
var noBrowser = args.Contains("--no-browser");
var portIndex = Array.IndexOf(args, "--port");
var port = 6677;
if (portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out var customPort))
    port = customPort;
var toolDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory;
var webRootPath = Path.Combine(toolDir, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = webRootPath, ContentRootPath = toolDir });
var url = $"http://localhost:{port}";
builder.WebHost.UseUrls(url);
#else
var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:6667", "http://localhost:6677");
#endif

builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton<AevatarSecretsStore>(sp => new AevatarSecretsStore(AevatarPaths.SecretsJson));
builder.Services.AddSingleton<ISecretsStore>(sp => new SecretsStoreAdapter(sp.GetRequiredService<AevatarSecretsStore>()));

var app = builder.Build();

#if AEVATAR_CONFIG_TOOL
Console.WriteLine();
Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
Console.WriteLine("║                    aevatar-config                         ║");
Console.WriteLine("╠═══════════════════════════════════════════════════════════╣");
Console.WriteLine($"║  🌐 Web UI: {url,-45} ║");
Console.WriteLine($"║  📁 Secrets: {AevatarPaths.SecretsJson,-42} ║");
if (AevatarPaths.SecretsJson.Length > 51) Console.WriteLine($"║     {AevatarPaths.SecretsJson,-49} ║");
Console.WriteLine("║  Press Ctrl+C to stop                                      ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
Console.WriteLine();
app.Lifetime.ApplicationStarted.Register(() => { if (!noBrowser) OpenBrowser(url); });
#endif

app.UseDefaultFiles();
app.UseStaticFiles();

static bool IsLocal(HttpContext ctx) => ctx.Connection.RemoteIpAddress == null || System.Net.IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress!);

// ─── Health ───
app.MapGet("/api/health", () => Results.Text("ok"));

// ─── Stubs (UI expects these; return empty/disabled so sidebar works) ───
app.MapGet("/api/config/source", (HttpContext http) => IsLocal(http) ? Results.Json(new { ok = true, mongoConfigured = false, fileConfigured = true }) : Results.Forbid());
app.MapGet("/api/crypto/secp256k1/status", (HttpContext http) => IsLocal(http) ? Results.Json(new { ok = true, configured = false, privateKey = new { configured = false, masked = "", keyPath = "", backupsPrefix = "", backupCount = 0 }, publicKey = new { configured = false, hex = "" } }) : Results.Forbid());
app.MapGet("/api/embeddings", (HttpContext http) => IsLocal(http) ? Results.Json(new { ok = true, embeddings = new { enabled = (bool?)null, providerType = "", model = "", endpoint = "", configured = false, masked = "" } }) : Results.Forbid());
app.MapGet("/api/skillsmp/status", (HttpContext http) => IsLocal(http) ? Results.Json(new { ok = true, configured = false, masked = "", keyPath = "SkillsMP:ApiKey", baseUrl = "" }) : Results.Forbid());
app.MapGet("/api/websearch", (HttpContext http) => IsLocal(http) ? Results.Json(new { ok = true, webSearch = new { enabled = (bool?)null, effectiveEnabled = false, provider = "", endpoint = "", timeoutMs = (int?)null, searchDepth = "", configured = false, masked = "", available = false } }) : Results.Forbid());
app.MapGet("/api/agents", (HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var dir = AevatarPaths.Agents;
    if (!Directory.Exists(dir)) return Results.Json(new { ok = true, agents = Array.Empty<object>(), exists = false });
    var files = Directory.GetFiles(dir, "*.yaml").Concat(Directory.GetFiles(dir, "*.yml")).Select(f => new FileInfo(f)).OrderBy(f => f.Name).Select(f => new { filename = f.Name, path = f.FullName, sizeBytes = f.Length, lastModified = f.LastWriteTimeUtc.ToString("o") }).ToList();
    return Results.Json(new { ok = true, agents = files, exists = true, directory = dir });
});
app.MapGet("/api/agents/{filename}", (string filename, HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var sanitized = Path.GetFileName(filename);
    if (string.IsNullOrEmpty(sanitized) || sanitized != filename || (!sanitized.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) && !sanitized.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
        return Results.BadRequest(new { ok = false, error = "Invalid filename" });
    var path = Path.Combine(AevatarPaths.Agents, sanitized);
    if (!File.Exists(path)) return Results.NotFound(new { ok = false, error = "Agent file not found", filename = sanitized });
    try { return Results.Json(new { ok = true, filename = sanitized, content = File.ReadAllText(path), sizeBytes = new FileInfo(path).Length, lastModified = new FileInfo(path).LastWriteTimeUtc.ToString("o") }); }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});
app.MapPut("/api/agents/{filename}", async (string filename, AgentFileRequest? req, HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var sanitized = Path.GetFileName(filename);
    if (string.IsNullOrEmpty(sanitized) || sanitized != filename || (!sanitized.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) && !sanitized.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
        return Results.BadRequest(new { ok = false, error = "Invalid filename" });
    if (req?.Content == null) return Results.BadRequest(new { ok = false, error = "content field is required" });
    var dir = AevatarPaths.Agents;
    var path = Path.Combine(dir, sanitized);
    try { Directory.CreateDirectory(dir); var existed = File.Exists(path); await File.WriteAllTextAsync(path, req.Content); return Results.Json(new { ok = true, filename = sanitized, path, created = !existed }); }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});
app.MapDelete("/api/agents/{filename}", (string filename, HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var sanitized = Path.GetFileName(filename);
    if (string.IsNullOrEmpty(sanitized) || sanitized != filename) return Results.BadRequest(new { ok = false, error = "Invalid filename" });
    var path = Path.Combine(AevatarPaths.Agents, sanitized);
    if (!File.Exists(path)) return Results.NotFound(new { ok = false, error = "Agent file not found", filename = sanitized });
    try { File.Delete(path); return Results.Json(new { ok = true, filename = sanitized, deleted = true }); }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});
app.MapGet("/api/trash/api-keys", (HttpContext http) => IsLocal(http) ? Results.Json(new { ok = true, items = Array.Empty<object>() }) : Results.Forbid());
app.MapGet("/api/config/raw", (HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var path = AevatarPaths.ConfigJson;
    if (!File.Exists(path)) return Results.Json(new { ok = true, json = "{}", keyCount = 0, exists = false });
    try
    {
        var content = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(content);
        var flat = NestedToFlat(doc.RootElement);
        var nested = FlatToNested(flat);
        return Results.Json(new { ok = true, json = JsonSerializer.Serialize(nested, new JsonSerializerOptions { WriteIndented = true }), keyCount = flat.Count, exists = true });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});
app.MapPut("/api/config/raw", async (HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    RawSecretsRequest? req;
    try { req = await http.Request.ReadFromJsonAsync<RawSecretsRequest>(); }
    catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
    if (req?.Json == null) return Results.BadRequest(new { ok = false, error = "json field is required" });
    try { using var _ = JsonDocument.Parse(req.Json); }
    catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
    var path = AevatarPaths.ConfigJson;
    var dir = Path.GetDirectoryName(path)!;
    try
    {
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(JsonDocument.Parse(req.Json).RootElement, new JsonSerializerOptions { WriteIndented = true }));
        return Results.Json(new { ok = true, path });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});

// ─── LLM ───
app.MapGet("/api/llm/providers", (ISecretsStore secrets, HttpContext http) => IsLocal(http) ? Results.Json(new { ok = true, providers = ProviderCatalog.BuildProviderTypes(secrets) }) : Results.Forbid());
app.MapGet("/api/llm/instances", (ISecretsStore secrets, HttpContext http) => IsLocal(http) ? Results.Json(new { ok = true, instances = ProviderCatalog.BuildInstances(secrets) }) : Results.Forbid());
app.MapGet("/api/llm/default", (ISecretsStore secrets, HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    EnsureDefaultProviderKeyBestEffort(secrets, null);
    return Results.Json(new { ok = true, providerName = ResolveEffectiveDefaultProviderName(secrets) });
});
app.MapPost("/api/llm/default", (SetLLMDefaultRequest req, ISecretsStore secrets, HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var name = (req.ProviderName ?? "").Trim();
    if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { ok = false, error = "providerName is required" });
    if (!IsProviderRunnable(secrets, name)) return Results.BadRequest(new { ok = false, error = "providerName has no configured apiKey" });
    secrets.Set("LLMProviders:Default", name);
    return Results.Json(new { ok = true, providerName = name });
});
app.MapGet("/api/llm/provider/{providerName}", (string providerName, ISecretsStore secrets, HttpContext http) => IsLocal(http) ? Results.Json(new { ok = true, provider = LLMProviderResolver.Resolve(secrets, providerName).Public }) : Results.Forbid());
app.MapGet("/api/llm/test/{providerName}", async (string providerName, ISecretsStore secrets, HttpContext http, CancellationToken ct) => IsLocal(http) ? Results.Json(await LLMProbe.TestAsync(LLMProviderResolver.Resolve(secrets, providerName), ct)) : Results.Forbid());
app.MapGet("/api/llm/models/{providerName}", async (string providerName, ISecretsStore secrets, HttpContext http, int? limit, CancellationToken ct) => IsLocal(http) ? Results.Json(await LLMProbe.FetchModelsAsync(LLMProviderResolver.Resolve(secrets, providerName), limit ?? 200, ct)) : Results.Forbid());
app.MapGet("/api/llm/api-key/{providerName}", (string providerName, bool? reveal, ISecretsStore secrets, HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var name = (providerName ?? "").Trim();
    if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { ok = false, error = "providerName is required" });
    var keyPath = $"LLMProviders:Providers:{name}:ApiKey";
    if (!secrets.TryGet(keyPath, out var value) || string.IsNullOrWhiteSpace(value))
        return Results.Json(new { ok = true, providerName = name, configured = false, masked = "" });
    var masked = SecretMask.MaskMiddle(value.Trim());
    if (reveal == true) return Results.Json(new { ok = true, providerName = name, configured = true, masked, value = value.Trim() });
    return Results.Json(new { ok = true, providerName = name, configured = true, masked });
});
app.MapPost("/api/llm/api-key", (SetLLMApiKeyRequest req, ISecretsStore secrets, HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var providerName = (req.ProviderName ?? "").Trim();
    if (string.IsNullOrEmpty(providerName)) return Results.BadRequest(new { error = "providerName is required" });
    var apiKey = (req.ApiKey ?? "").Trim();
    if (string.IsNullOrEmpty(apiKey)) return Results.BadRequest(new { error = "apiKey is required" });
    var keyPath = $"LLMProviders:Providers:{providerName}:ApiKey";
    secrets.Set(keyPath, apiKey);
    EnsureDefaultProviderKeyBestEffort(secrets, providerName);
    return Results.Json(new { ok = true, providerName, keyPath });
});
app.MapPost("/api/llm/instance", (UpsertLLMInstanceRequest req, ISecretsStore secrets, HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var name = (req.ProviderName ?? "").Trim();
    if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { ok = false, error = "providerName is required" });
    var providerType = (req.ProviderType ?? "").Trim();
    if (string.IsNullOrEmpty(providerType)) return Results.BadRequest(new { ok = false, error = "providerType is required" });
    var model = (req.Model ?? "").Trim();
    if (string.IsNullOrEmpty(model)) return Results.BadRequest(new { ok = false, error = "model is required" });
    secrets.Set($"LLMProviders:Providers:{name}:ProviderType", providerType);
    secrets.Set($"LLMProviders:Providers:{name}:Model", model);
    var endpoint = (req.Endpoint ?? "").Trim();
    var endpointPath = $"LLMProviders:Providers:{name}:Endpoint";
    if (string.IsNullOrEmpty(endpoint)) secrets.Remove(endpointPath);
    else secrets.Set(endpointPath, endpoint);
    var apiKeyPath = $"LLMProviders:Providers:{name}:ApiKey";
    var apiKey = (req.ApiKey ?? "").Trim();
    var copyFrom = (req.CopyApiKeyFrom ?? "").Trim();
    if (!string.IsNullOrEmpty(apiKey)) secrets.Set(apiKeyPath, apiKey);
    else if (!string.IsNullOrEmpty(copyFrom))
    {
        var fromPath = $"LLMProviders:Providers:{copyFrom}:ApiKey";
        if (!secrets.TryGet(fromPath, out var fromKey) || string.IsNullOrWhiteSpace(fromKey))
            return Results.BadRequest(new { ok = false, error = "copyApiKeyFrom has no configured apiKey" });
        secrets.Set(apiKeyPath, fromKey!.Trim());
    }
    EnsureDefaultProviderKeyBestEffort(secrets, name);
    var resolved = LLMProviderResolver.Resolve(secrets, name);
    return Results.Json(new { ok = true, providerName = name, providerType, keyPaths = new[] { $"LLMProviders:Providers:{name}:ProviderType", $"LLMProviders:Providers:{name}:Model", endpointPath, apiKeyPath }, provider = resolved.Public });
});
app.MapDelete("/api/llm/api-key/{providerName}", (string providerName, ISecretsStore secrets, HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var name = (providerName ?? "").Trim();
    if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { error = "providerName is required" });
    var keyPath = $"LLMProviders:Providers:{name}:ApiKey";
    var removed = secrets.Remove(keyPath);
    return Results.Json(new { ok = true, providerName = name, keyPath, removed });
});
app.MapPost("/api/llm/probe/test", async (ProbeLLMRequest req, HttpContext http, CancellationToken ct) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var providerType = (req.ProviderType ?? "").Trim();
    if (string.IsNullOrEmpty(providerType)) return Results.BadRequest(new { ok = false, error = "providerType is required" });
    var apiKey = (req.ApiKey ?? "").Trim();
    if (string.IsNullOrEmpty(apiKey)) return Results.BadRequest(new { ok = false, error = "apiKey is required" });
    var profile = ProviderProfiles.Get(providerType);
    var endpoint = string.IsNullOrWhiteSpace(req.Endpoint) ? (profile.DefaultEndpoint ?? "") : req.Endpoint.Trim();
    if (string.IsNullOrEmpty(endpoint)) return Results.BadRequest(new { ok = false, error = "endpoint is required" });
    var provider = new ResolvedProvider($"probe:{providerType}", providerType, "probe", profile.DisplayName, profile.Kind, endpoint, "probe", "", "probe", true, apiKey,
        new ResolvedProviderPublic($"probe:{providerType}", providerType, "probe", profile.DisplayName, profile.Kind.ToString(), true, endpoint, "probe", "", "probe"));
    return Results.Json(await LLMProbe.TestAsync(provider, ct));
});
app.MapPost("/api/llm/probe/models", async (ProbeLLMRequest req, int? limit, HttpContext http, CancellationToken ct) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var providerType = (req.ProviderType ?? "").Trim();
    if (string.IsNullOrEmpty(providerType)) return Results.BadRequest(new { ok = false, error = "providerType is required" });
    var apiKey = (req.ApiKey ?? "").Trim();
    if (string.IsNullOrEmpty(apiKey)) return Results.BadRequest(new { ok = false, error = "apiKey is required" });
    var profile = ProviderProfiles.Get(providerType);
    var endpoint = string.IsNullOrWhiteSpace(req.Endpoint) ? (profile.DefaultEndpoint ?? "") : req.Endpoint.Trim();
    if (string.IsNullOrEmpty(endpoint)) return Results.BadRequest(new { ok = false, error = "endpoint is required" });
    var provider = new ResolvedProvider($"probe:{providerType}", providerType, "probe", profile.DisplayName, profile.Kind, endpoint, "probe", "", "probe", true, apiKey,
        new ResolvedProviderPublic($"probe:{providerType}", providerType, "probe", profile.DisplayName, profile.Kind.ToString(), true, endpoint, "probe", "", "probe"));
    return Results.Json(await LLMProbe.FetchModelsAsync(provider, limit ?? 200, ct));
});

// ─── Secrets raw & set/remove ───
app.MapGet("/api/secrets/raw", (ISecretsStore secrets, HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var all = secrets.GetAll();
    var nested = FlatToNested(all);
    return Results.Json(new { ok = true, json = JsonSerializer.Serialize(nested, new JsonSerializerOptions { WriteIndented = true }), keyCount = all.Count });
});
app.MapPut("/api/secrets/raw", async (HttpContext http, ISecretsStore secrets) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    RawSecretsRequest? req;
    try { req = await http.Request.ReadFromJsonAsync<RawSecretsRequest>(); }
    catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
    if (req?.Json == null) return Results.BadRequest(new { ok = false, error = "json field is required" });
    Dictionary<string, string> newFlat;
    try { using var doc = JsonDocument.Parse(req.Json); newFlat = NestedToFlat(doc.RootElement); }
    catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
    var oldAll = secrets.GetAll();
    foreach (var k in oldAll.Keys) { if (!newFlat.ContainsKey(k)) secrets.Remove(k); }
    foreach (var kv in newFlat) secrets.Set(kv.Key, kv.Value);
    EnsureDefaultProviderKeyBestEffort(secrets, null);
    return Results.Json(new { ok = true, keyCount = newFlat.Count });
});
app.MapPost("/api/secrets/set", (SetSecretRequest req, ISecretsStore secrets, HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var key = (req.Key ?? "").Trim();
    if (string.IsNullOrEmpty(key)) return Results.BadRequest(new { error = "key is required" });
    var value = (req.Value ?? "").Trim();
    if (string.IsNullOrEmpty(value)) return Results.BadRequest(new { error = "value is required" });
    secrets.Set(key, value);
    return Results.Json(new { ok = true, key });
});
app.MapPost("/api/secrets/remove", (RemoveSecretRequest req, ISecretsStore secrets, HttpContext http) =>
{
    if (!IsLocal(http)) return Results.Forbid();
    var key = (req.Key ?? "").Trim();
    if (string.IsNullOrEmpty(key)) return Results.BadRequest(new { error = "key is required" });
    var removed = secrets.Remove(key);
    return Results.Json(new { ok = true, key, removed });
});

app.MapFallbackToFile("index.html");

app.Run();

static bool IsProviderRunnable(ISecretsStore secrets, string providerName)
{
    var name = (providerName ?? "").Trim();
    if (name.Length == 0) return false;
    return secrets.TryGet($"LLMProviders:Providers:{name}:ApiKey", out var v) && !string.IsNullOrWhiteSpace(v);
}
static string ResolveEffectiveDefaultProviderName(ISecretsStore secrets)
{
    if (secrets.TryGet("LLMProviders:Default", out var raw) && !string.IsNullOrWhiteSpace(raw))
    {
        var v = raw.Trim();
        if (!string.Equals(v, "default", StringComparison.OrdinalIgnoreCase) || IsProviderRunnable(secrets, "default")) return v;
    }
    const string prefix = "LLMProviders:Providers:";
    const string suffix = ":ApiKey";
    var all = secrets.GetAll();
    var first = all.Keys.Where(k => k != null && k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        .Select(k => k!.Substring(prefix.Length, k.Length - prefix.Length - suffix.Length).Trim()).FirstOrDefault(n => !string.IsNullOrEmpty(n) && IsProviderRunnable(secrets, n));
    return first ?? "default";
}
static void EnsureDefaultProviderKeyBestEffort(ISecretsStore secrets, string? preferredProvider)
{
    var current = secrets.TryGet("LLMProviders:Default", out var raw) ? (raw ?? "").Trim() : "";
    var currentBad = string.IsNullOrWhiteSpace(current) || (string.Equals(current, "default", StringComparison.OrdinalIgnoreCase) && !IsProviderRunnable(secrets, "default"));
    if (!currentBad && IsProviderRunnable(secrets, current)) return;
    var preferred = (preferredProvider ?? "").Trim();
    if (!string.IsNullOrEmpty(preferred) && IsProviderRunnable(secrets, preferred)) { secrets.Set("LLMProviders:Default", preferred); return; }
    var next = ResolveEffectiveDefaultProviderName(secrets);
    if (!string.IsNullOrEmpty(next) && IsProviderRunnable(secrets, next)) { secrets.Set("LLMProviders:Default", next); return; }
    if (!string.IsNullOrWhiteSpace(current)) secrets.Remove("LLMProviders:Default");
}
static object FlatToNested(IReadOnlyDictionary<string, string> flat)
{
    var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    foreach (var kv in flat)
    {
        var parts = kv.Key.Split(':');
        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            if (!current.TryGetValue(part, out var next))
            {
                next = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                current[part] = next;
            }
            current = (Dictionary<string, object>)next;
        }
        current[parts[^1]] = kv.Value;
    }
    return root;
}
static Dictionary<string, string> NestedToFlat(JsonElement element, string prefix = "")
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    NestedToFlatRecursive(element, prefix, result);
    return result;
}
static void NestedToFlatRecursive(JsonElement element, string prefix, Dictionary<string, string> result)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var prop in element.EnumerateObject())
            {
                var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}:{prop.Name}";
                NestedToFlatRecursive(prop.Value, key, result);
            }
            break;
        case JsonValueKind.Array:
            var idx = 0;
            foreach (var item in element.EnumerateArray()) { NestedToFlatRecursive(item, $"{prefix}:{idx}", result); idx++; }
            break;
        default:
            if (!string.IsNullOrEmpty(prefix)) result[prefix] = element.ToString();
            break;
    }
}
static void OpenBrowser(string url)
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start("open", url);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Process.Start("xdg-open", url);
    }
    catch { }
}
