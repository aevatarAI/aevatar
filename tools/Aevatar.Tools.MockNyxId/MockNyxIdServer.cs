using Aevatar.Tools.MockNyxId.Endpoints;

namespace Aevatar.Tools.MockNyxId;

/// <summary>
/// Factory for building the mock NyxID server.
/// Supports both standalone mode (dotnet run) and TestServer mode (integration tests).
/// </summary>
public static class MockNyxIdServer
{
    /// <summary>
    /// Create a builder that can be further configured (e.g., UseTestServer for tests).
    /// </summary>
    public static WebApplicationBuilder CreateBuilder(
        string[]? args = null,
        Action<MockNyxIdOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);

        var options = new MockNyxIdOptions();
        builder.Configuration.GetSection("MockNyxId").Bind(options);
        configure?.Invoke(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new MockStore(options));
        builder.Services.AddSingleton(new MockJwtHelper(options));

        return builder;
    }

    /// <summary>
    /// Build a complete app ready to run (standalone mode).
    /// </summary>
    public static WebApplication Build(string[]? args = null, Action<MockNyxIdOptions>? configure = null)
    {
        var builder = CreateBuilder(args, configure);
        var options = builder.Services.BuildServiceProvider().GetRequiredService<MockNyxIdOptions>();

        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            builder.WebHost.UseUrls($"http://localhost:{options.Port}");

        var app = builder.Build();
        MapAllEndpoints(app);
        return app;
    }

    /// <summary>
    /// Build from an existing builder (for TestServer usage).
    /// </summary>
    public static WebApplication BuildFromBuilder(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        MapAllEndpoints(app);
        return app;
    }

    private static void MapAllEndpoints(WebApplication app)
    {
        // Health check
        app.MapGet("/", () => Results.Json(new
        {
            service = "MockNyxId",
            status = "ok",
            timestamp = DateTimeOffset.UtcNow,
        }));

        app.MapGet("/health", () => Results.Ok("ok"));

        // API endpoints
        app.MapAuthEndpoints();
        app.MapProxyEndpoints();
        app.MapLlmGatewayEndpoints();
    }
}
