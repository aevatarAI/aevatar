using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aevatar.Configuration;
using Microsoft.AspNetCore.Http.Json;

namespace Aevatar.Tools.Config;

public sealed class ConfigToolHostOptions
{
    public int Port { get; init; } = 6677;
    public bool NoBrowser { get; init; }
    public string BannerTitle { get; init; } = "aevatar config";
    public string? DeprecationMessage { get; init; }
    public IReadOnlyList<string>? WebRootCandidates { get; init; }
}

public static class ConfigToolHost
{
    public static async Task RunAsync(
        ConfigToolHostOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ConfigToolHostOptions();

        var toolDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
            ?? Environment.CurrentDirectory;
        var webRootCandidates = BuildWebRootCandidates(options, toolDir);
        var webRootPath = webRootCandidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "index.html")))
            ?? webRootCandidates[0];

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            WebRootPath = webRootPath,
            ContentRootPath = toolDir,
        });

        var url = $"http://localhost:{options.Port}";
        builder.WebHost.UseUrls(url);
        builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        builder.Services.AddSingleton<AevatarSecretsStore>(_ => new AevatarSecretsStore(AevatarPaths.SecretsJson));
        builder.Services.AddSingleton<ISecretsStore>(sp => new SecretsStoreAdapter(sp.GetRequiredService<AevatarSecretsStore>()));
        builder.Services.AddSingleton<ConfigOperations>();

        var app = builder.Build();
        PrintBanner(options, url, webRootPath);

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (!options.NoBrowser)
                OpenBrowser(url);
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();

        // Health.
        app.MapGet("/api/health", () => Results.Text("ok"));

        // Stubs.
        app.MapGet("/api/config/source", (HttpContext http) => IsLocal(http)
            ? Results.Json(new { ok = true, mongoConfigured = false, fileConfigured = true })
            : Results.Forbid());
        app.MapGet("/api/crypto/secp256k1/status", (HttpContext http) => IsLocal(http)
            ? Results.Json(new
            {
                ok = true,
                configured = false,
                privateKey = new { configured = false, masked = "", keyPath = "", backupsPrefix = "", backupCount = 0 },
                publicKey = new { configured = false, hex = "" },
            })
            : Results.Forbid());
        app.MapGet("/api/embeddings", (HttpContext http) => IsLocal(http)
            ? Results.Json(new
            {
                ok = true,
                embeddings = new { enabled = (bool?)null, providerType = "", model = "", endpoint = "", configured = false, masked = "" },
            })
            : Results.Forbid());
        app.MapGet("/api/skillsmp/status", (HttpContext http) => IsLocal(http)
            ? Results.Json(new { ok = true, configured = false, masked = "", keyPath = "SkillsMP:ApiKey", baseUrl = "" })
            : Results.Forbid());
        app.MapGet("/api/websearch", (HttpContext http) => IsLocal(http)
            ? Results.Json(new
            {
                ok = true,
                webSearch = new
                {
                    enabled = (bool?)null,
                    effectiveEnabled = false,
                    provider = "",
                    endpoint = "",
                    timeoutMs = (int?)null,
                    searchDepth = "",
                    configured = false,
                    masked = "",
                    available = false,
                },
            })
            : Results.Forbid());

        app.MapGet("/api/workflows", (HttpContext http, ConfigOperations ops) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();

            var dir = AevatarPaths.Workflows;
            var items = ops.ListWorkflows(WorkflowSource.Home);
            return Results.Json(new
            {
                ok = true,
                workflows = items,
                exists = Directory.Exists(dir),
                directory = dir,
            });
        });

        app.MapGet("/api/workflows/{filename}", (string filename, HttpContext http, ConfigOperations ops) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();

            try
            {
                var item = ops.GetWorkflow(filename, WorkflowSource.Home);
                if (item == null)
                    return Results.NotFound(new { ok = false, error = "Workflow file not found", filename });

                return Results.Json(new
                {
                    ok = true,
                    filename = item.Filename,
                    content = item.Content,
                    sizeBytes = item.SizeBytes,
                    lastModified = item.LastModified,
                    source = item.Source,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });

        app.MapPut("/api/workflows/{filename}", async (string filename, WorkflowFileRequest? req, HttpContext http, ConfigOperations ops) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();

            if (req?.Content == null)
                return Results.BadRequest(new { ok = false, error = "content field is required" });

            try
            {
                var saved = ops.PutWorkflow(filename, req.Content, WorkflowSource.Home);
                return Results.Json(new
                {
                    ok = true,
                    filename = saved.Filename,
                    path = saved.Path,
                    source = saved.Source,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });

        app.MapDelete("/api/workflows/{filename}", (string filename, HttpContext http, ConfigOperations ops) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();

            try
            {
                var deleted = ops.DeleteWorkflow(filename, WorkflowSource.Home);
                if (!deleted)
                    return Results.NotFound(new { ok = false, error = "Workflow file not found", filename });
                return Results.Json(new { ok = true, filename, deleted = true, source = "home" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });

        // Compatibility aliases (deprecated): /api/agents -> /api/workflows.
        app.MapGet("/api/agents", (HttpContext http, ConfigOperations ops) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();

            var dir = AevatarPaths.Workflows;
            var items = ops.ListWorkflows(WorkflowSource.Home);
            return Results.Json(new
            {
                ok = true,
                agents = items,
                exists = Directory.Exists(dir),
                directory = dir,
                deprecated = true,
                migration = "/api/workflows",
            });
        });
        app.MapGet("/api/agents/{filename}", (string filename, HttpContext http, ConfigOperations ops) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            var item = ops.GetWorkflow(filename, WorkflowSource.Home);
            return item == null
                ? Results.NotFound(new { ok = false, error = "Workflow file not found", filename })
                : Results.Json(new
                {
                    ok = true,
                    filename = item.Filename,
                    content = item.Content,
                    sizeBytes = item.SizeBytes,
                    lastModified = item.LastModified,
                    deprecated = true,
                    migration = "/api/workflows/{filename}",
                });
        });
        app.MapPut("/api/agents/{filename}", (string filename, AgentFileRequest? req, HttpContext http, ConfigOperations ops) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            if (req?.Content == null)
                return Results.BadRequest(new { ok = false, error = "content field is required" });
            var saved = ops.PutWorkflow(filename, req.Content, WorkflowSource.Home);
            return Results.Json(new
            {
                ok = true,
                filename = saved.Filename,
                path = saved.Path,
                deprecated = true,
                migration = "/api/workflows/{filename}",
            });
        });
        app.MapDelete("/api/agents/{filename}", (string filename, HttpContext http, ConfigOperations ops) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            var deleted = ops.DeleteWorkflow(filename, WorkflowSource.Home);
            return deleted
                ? Results.Json(new { ok = true, filename, deleted = true, deprecated = true, migration = "/api/workflows/{filename}" })
                : Results.NotFound(new { ok = false, error = "Workflow file not found", filename });
        });

        app.MapGet("/api/trash/api-keys", (HttpContext http) => IsLocal(http)
            ? Results.Json(new { ok = true, items = Array.Empty<object>() })
            : Results.Forbid());

        app.MapGet("/api/config/raw", (HttpContext http, ConfigOperations ops) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();

            try
            {
                var json = ops.ExportConfigJson();
                var flat = ops.ListConfigJson();
                return Results.Json(new
                {
                    ok = true,
                    json,
                    keyCount = flat.Count,
                    exists = File.Exists(AevatarPaths.ConfigJson),
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });

        app.MapPut("/api/config/raw", async (HttpContext http, ConfigOperations ops) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();

            RawSecretsRequest? req;
            try
            {
                req = await http.Request.ReadFromJsonAsync<RawSecretsRequest>(cancellationToken);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }

            if (req?.Json == null)
                return Results.BadRequest(new { ok = false, error = "json field is required" });

            try
            {
                var keyCount = ops.ImportConfigJson(req.Json);
                return Results.Json(new { ok = true, path = AevatarPaths.ConfigJson, keyCount });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        });

        // LLM.
        app.MapGet("/api/llm/providers", (ISecretsStore secrets, HttpContext http) => IsLocal(http)
            ? Results.Json(new { ok = true, providers = ProviderCatalog.BuildProviderTypes(secrets) })
            : Results.Forbid());
        app.MapGet("/api/llm/instances", (ISecretsStore secrets, HttpContext http) => IsLocal(http)
            ? Results.Json(new { ok = true, instances = ProviderCatalog.BuildInstances(secrets) })
            : Results.Forbid());
        app.MapGet("/api/llm/default", (ISecretsStore secrets, HttpContext http) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            EnsureDefaultProviderKeyBestEffort(secrets, null);
            return Results.Json(new { ok = true, providerName = ResolveEffectiveDefaultProviderName(secrets) });
        });
        app.MapPost("/api/llm/default", (SetLLMDefaultRequest req, ISecretsStore secrets, HttpContext http) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            var name = (req.ProviderName ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Results.BadRequest(new { ok = false, error = "providerName is required" });
            if (!IsProviderRunnable(secrets, name))
                return Results.BadRequest(new { ok = false, error = "providerName has no configured apiKey" });
            secrets.Set("LLMProviders:Default", name);
            return Results.Json(new { ok = true, providerName = name });
        });
        app.MapGet("/api/llm/provider/{providerName}", (string providerName, ISecretsStore secrets, HttpContext http) => IsLocal(http)
            ? Results.Json(new { ok = true, provider = LLMProviderResolver.Resolve(secrets, providerName).Public })
            : Results.Forbid());
        app.MapGet("/api/llm/test/{providerName}", async (string providerName, ISecretsStore secrets, HttpContext http, CancellationToken ct) => IsLocal(http)
            ? Results.Json(await LLMProbe.TestAsync(LLMProviderResolver.Resolve(secrets, providerName), ct))
            : Results.Forbid());
        app.MapGet("/api/llm/models/{providerName}", async (string providerName, ISecretsStore secrets, HttpContext http, int? limit, CancellationToken ct) => IsLocal(http)
            ? Results.Json(await LLMProbe.FetchModelsAsync(LLMProviderResolver.Resolve(secrets, providerName), limit ?? 200, ct))
            : Results.Forbid());
        app.MapGet("/api/llm/api-key/{providerName}", (string providerName, bool? reveal, ISecretsStore secrets, HttpContext http) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            var name = (providerName ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Results.BadRequest(new { ok = false, error = "providerName is required" });
            var keyPath = $"LLMProviders:Providers:{name}:ApiKey";
            if (!secrets.TryGet(keyPath, out var value) || string.IsNullOrWhiteSpace(value))
                return Results.Json(new { ok = true, providerName = name, configured = false, masked = "" });
            var masked = SecretMask.MaskMiddle(value.Trim());
            if (reveal == true)
                return Results.Json(new { ok = true, providerName = name, configured = true, masked, value = value.Trim() });
            return Results.Json(new { ok = true, providerName = name, configured = true, masked });
        });
        app.MapPost("/api/llm/api-key", (SetLLMApiKeyRequest req, ISecretsStore secrets, HttpContext http) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            var providerName = (req.ProviderName ?? "").Trim();
            if (string.IsNullOrEmpty(providerName))
                return Results.BadRequest(new { error = "providerName is required" });
            var apiKey = (req.ApiKey ?? "").Trim();
            if (string.IsNullOrEmpty(apiKey))
                return Results.BadRequest(new { error = "apiKey is required" });
            var keyPath = $"LLMProviders:Providers:{providerName}:ApiKey";
            secrets.Set(keyPath, apiKey);
            EnsureDefaultProviderKeyBestEffort(secrets, providerName);
            return Results.Json(new { ok = true, providerName, keyPath });
        });
        app.MapPost("/api/llm/instance", (UpsertLLMInstanceRequest req, ISecretsStore secrets, HttpContext http) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            var name = (req.ProviderName ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Results.BadRequest(new { ok = false, error = "providerName is required" });
            var providerType = (req.ProviderType ?? "").Trim();
            if (string.IsNullOrEmpty(providerType))
                return Results.BadRequest(new { ok = false, error = "providerType is required" });
            var model = (req.Model ?? "").Trim();
            if (string.IsNullOrEmpty(model))
                return Results.BadRequest(new { ok = false, error = "model is required" });
            secrets.Set($"LLMProviders:Providers:{name}:ProviderType", providerType);
            secrets.Set($"LLMProviders:Providers:{name}:Model", model);
            var endpoint = (req.Endpoint ?? "").Trim();
            var endpointPath = $"LLMProviders:Providers:{name}:Endpoint";
            if (string.IsNullOrEmpty(endpoint))
                secrets.Remove(endpointPath);
            else
                secrets.Set(endpointPath, endpoint);
            var apiKeyPath = $"LLMProviders:Providers:{name}:ApiKey";
            var apiKey = (req.ApiKey ?? "").Trim();
            var copyFrom = (req.CopyApiKeyFrom ?? "").Trim();
            var forceCopyFrom = req.ForceCopyApiKeyFrom == true;
            var hasExistingApiKey =
                secrets.TryGet(apiKeyPath, out var existingApiKey) &&
                !string.IsNullOrWhiteSpace(existingApiKey);
            var apiKeyCopiedFrom = string.Empty;
            var apiKeyCopySkipped = false;

            if (!string.IsNullOrEmpty(apiKey))
            {
                secrets.Set(apiKeyPath, apiKey);
            }
            else if (!string.IsNullOrEmpty(copyFrom))
            {
                if (hasExistingApiKey && !forceCopyFrom)
                {
                    apiKeyCopySkipped = true;
                }
                else
                {
                    var fromPath = $"LLMProviders:Providers:{copyFrom}:ApiKey";
                    if (!secrets.TryGet(fromPath, out var fromKey) || string.IsNullOrWhiteSpace(fromKey))
                        return Results.BadRequest(new { ok = false, error = "copyApiKeyFrom has no configured apiKey" });

                    secrets.Set(apiKeyPath, fromKey!.Trim());
                    apiKeyCopiedFrom = copyFrom;
                }
            }

            EnsureDefaultProviderKeyBestEffort(secrets, name);
            var resolved = LLMProviderResolver.Resolve(secrets, name);
            return Results.Json(new
            {
                ok = true,
                providerName = name,
                providerType,
                keyPaths = new[] { $"LLMProviders:Providers:{name}:ProviderType", $"LLMProviders:Providers:{name}:Model", endpointPath, apiKeyPath },
                apiKeyCopiedFrom,
                apiKeyCopySkipped,
                provider = resolved.Public,
            });
        });
        app.MapDelete("/api/llm/api-key/{providerName}", (string providerName, ISecretsStore secrets, HttpContext http) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            var name = (providerName ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                return Results.BadRequest(new { error = "providerName is required" });
            var keyPath = $"LLMProviders:Providers:{name}:ApiKey";
            var removed = secrets.Remove(keyPath);
            return Results.Json(new { ok = true, providerName = name, keyPath, removed });
        });
        app.MapPost("/api/llm/probe/test", async (ProbeLLMRequest req, HttpContext http, CancellationToken ct) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            var providerType = (req.ProviderType ?? "").Trim();
            if (string.IsNullOrEmpty(providerType))
                return Results.BadRequest(new { ok = false, error = "providerType is required" });
            var apiKey = (req.ApiKey ?? "").Trim();
            if (string.IsNullOrEmpty(apiKey))
                return Results.BadRequest(new { ok = false, error = "apiKey is required" });
            var profile = ProviderProfiles.Get(providerType);
            var endpoint = string.IsNullOrWhiteSpace(req.Endpoint) ? (profile.DefaultEndpoint ?? "") : req.Endpoint.Trim();
            if (string.IsNullOrEmpty(endpoint))
                return Results.BadRequest(new { ok = false, error = "endpoint is required" });
            var provider = new ResolvedProvider(
                $"probe:{providerType}",
                providerType,
                "probe",
                profile.DisplayName,
                profile.Kind,
                endpoint,
                "probe",
                "",
                "probe",
                true,
                apiKey,
                new ResolvedProviderPublic($"probe:{providerType}", providerType, "probe", profile.DisplayName, profile.Kind.ToString(), true, endpoint, "probe", "", "probe"));
            return Results.Json(await LLMProbe.TestAsync(provider, ct));
        });
        app.MapPost("/api/llm/probe/models", async (ProbeLLMRequest req, int? limit, HttpContext http, CancellationToken ct) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            var providerType = (req.ProviderType ?? "").Trim();
            if (string.IsNullOrEmpty(providerType))
                return Results.BadRequest(new { ok = false, error = "providerType is required" });
            var apiKey = (req.ApiKey ?? "").Trim();
            if (string.IsNullOrEmpty(apiKey))
                return Results.BadRequest(new { ok = false, error = "apiKey is required" });
            var profile = ProviderProfiles.Get(providerType);
            var endpoint = string.IsNullOrWhiteSpace(req.Endpoint) ? (profile.DefaultEndpoint ?? "") : req.Endpoint.Trim();
            if (string.IsNullOrEmpty(endpoint))
                return Results.BadRequest(new { ok = false, error = "endpoint is required" });
            var provider = new ResolvedProvider(
                $"probe:{providerType}",
                providerType,
                "probe",
                profile.DisplayName,
                profile.Kind,
                endpoint,
                "probe",
                "",
                "probe",
                true,
                apiKey,
                new ResolvedProviderPublic($"probe:{providerType}", providerType, "probe", profile.DisplayName, profile.Kind.ToString(), true, endpoint, "probe", "", "probe"));
            return Results.Json(await LLMProbe.FetchModelsAsync(provider, limit ?? 200, ct));
        });

        // Secrets.
        app.MapGet("/api/secrets/raw", (ConfigOperations ops, HttpContext http) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            var all = ops.ListSecrets();
            var json = ops.ExportSecretsJson();
            return Results.Json(new
            {
                ok = true,
                json,
                keyCount = all.Count,
            });
        });
        app.MapPut("/api/secrets/raw", async (HttpContext http, ConfigOperations ops) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            RawSecretsRequest? req;
            try
            {
                req = await http.Request.ReadFromJsonAsync<RawSecretsRequest>(cancellationToken);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
            if (req?.Json == null)
                return Results.BadRequest(new { ok = false, error = "json field is required" });
            try
            {
                var keyCount = ops.ImportSecretsJson(req.Json);
                return Results.Json(new { ok = true, keyCount });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        });
        app.MapPost("/api/secrets/set", (SetSecretRequest req, ConfigOperations ops, HttpContext http) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            var key = (req.Key ?? "").Trim();
            if (string.IsNullOrEmpty(key))
                return Results.BadRequest(new { error = "key is required" });
            var value = (req.Value ?? "").Trim();
            if (string.IsNullOrEmpty(value))
                return Results.BadRequest(new { error = "value is required" });
            ops.SetSecret(key, value);
            return Results.Json(new { ok = true, key });
        });
        app.MapPost("/api/secrets/remove", (RemoveSecretRequest req, ConfigOperations ops, HttpContext http) =>
        {
            if (!IsLocal(http))
                return Results.Forbid();
            var key = (req.Key ?? "").Trim();
            if (string.IsNullOrEmpty(key))
                return Results.BadRequest(new { error = "key is required" });
            var removed = ops.RemoveSecret(key);
            return Results.Json(new { ok = true, key, removed });
        });

        app.MapFallbackToFile("index.html");
        await app.RunAsync(cancellationToken);
    }

    private static IReadOnlyList<string> BuildWebRootCandidates(ConfigToolHostOptions options, string toolDir)
    {
        if (options.WebRootCandidates is { Count: > 0 })
            return options.WebRootCandidates;

        return new[]
        {
            Path.Combine(toolDir, "wwwroot"),
            Path.Combine(toolDir, "wwwroot", "config"),
            Path.GetFullPath(Path.Combine(toolDir, "../../../wwwroot")),
            Path.GetFullPath(Path.Combine(toolDir, "../../../../tools/Aevatar.Tools.Config/wwwroot")),
            Path.GetFullPath(Path.Combine(toolDir, "../../../../tools/Aevatar.Tools.Cli/wwwroot/config")),
            Path.Combine(Environment.CurrentDirectory, "wwwroot"),
            Path.Combine(Environment.CurrentDirectory, "tools", "Aevatar.Tools.Config", "wwwroot"),
            Path.Combine(Environment.CurrentDirectory, "tools", "Aevatar.Tools.Cli", "wwwroot", "config"),
        };
    }

    private static void PrintBanner(ConfigToolHostOptions options, string url, string webRootPath)
    {
        Console.WriteLine();
        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║{options.BannerTitle,-59}║");
        Console.WriteLine("╠═══════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║  🌐 Web UI: {url,-45} ║");
        Console.WriteLine($"║  📦 WebRoot: {webRootPath,-42} ║");
        if (webRootPath.Length > 51)
            Console.WriteLine($"║     {webRootPath,-49} ║");
        Console.WriteLine($"║  📁 Secrets: {AevatarPaths.SecretsJson,-42} ║");
        if (AevatarPaths.SecretsJson.Length > 51)
            Console.WriteLine($"║     {AevatarPaths.SecretsJson,-49} ║");
        if (!string.IsNullOrWhiteSpace(options.DeprecationMessage))
            Console.WriteLine($"║  ⚠ {options.DeprecationMessage,-50}║");
        Console.WriteLine("║  Press Ctrl+C to stop                                      ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();
    }

    private static bool IsLocal(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress == null ||
        System.Net.IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress!);

    private static bool IsProviderRunnable(ISecretsStore secrets, string providerName)
    {
        var name = (providerName ?? "").Trim();
        if (name.Length == 0)
            return false;
        return secrets.TryGet($"LLMProviders:Providers:{name}:ApiKey", out var v) && !string.IsNullOrWhiteSpace(v);
    }

    private static string ResolveEffectiveDefaultProviderName(ISecretsStore secrets)
    {
        if (secrets.TryGet("LLMProviders:Default", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            var value = raw.Trim();
            if (!string.Equals(value, "default", StringComparison.OrdinalIgnoreCase) || IsProviderRunnable(secrets, "default"))
                return value;
        }

        const string prefix = "LLMProviders:Providers:";
        const string suffix = ":ApiKey";
        var all = secrets.GetAll();
        var first = all.Keys
            .Where(k => k != null &&
                        k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k!.Substring(prefix.Length, k.Length - prefix.Length - suffix.Length).Trim())
            .FirstOrDefault(n => !string.IsNullOrEmpty(n) && IsProviderRunnable(secrets, n));
        return first ?? "default";
    }

    private static void EnsureDefaultProviderKeyBestEffort(ISecretsStore secrets, string? preferredProvider)
    {
        var current = secrets.TryGet("LLMProviders:Default", out var raw) ? (raw ?? "").Trim() : "";
        var currentBad = string.IsNullOrWhiteSpace(current) ||
                         (string.Equals(current, "default", StringComparison.OrdinalIgnoreCase) && !IsProviderRunnable(secrets, "default"));
        if (!currentBad && IsProviderRunnable(secrets, current))
            return;

        var preferred = (preferredProvider ?? "").Trim();
        if (!string.IsNullOrEmpty(preferred) && IsProviderRunnable(secrets, preferred))
        {
            secrets.Set("LLMProviders:Default", preferred);
            return;
        }

        var next = ResolveEffectiveDefaultProviderName(secrets);
        if (!string.IsNullOrEmpty(next) && IsProviderRunnable(secrets, next))
        {
            secrets.Set("LLMProviders:Default", next);
            return;
        }

        if (!string.IsNullOrWhiteSpace(current))
            secrets.Remove("LLMProviders:Default");
    }

    private static object FlatToNested(IReadOnlyDictionary<string, string> flat)
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

    private static Dictionary<string, string> NestedToFlat(JsonElement element, string prefix = "")
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        NestedToFlatRecursive(element, prefix, result);
        return result;
    }

    private static void NestedToFlatRecursive(JsonElement element, string prefix, Dictionary<string, string> result)
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
                foreach (var item in element.EnumerateArray())
                {
                    NestedToFlatRecursive(item, $"{prefix}:{idx}", result);
                    idx++;
                }
                break;
            default:
                if (!string.IsNullOrEmpty(prefix))
                    result[prefix] = element.ToString();
                break;
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
        }
        catch
        {
        }
    }
}
