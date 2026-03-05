using System.Text.Json;

namespace Aevatar.App.Host.Api.Endpoints;

public static class ConfigEndpoints
{
    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/remote-config", (IConfiguration config) =>
        {
            var remoteConfigJson = config["REMOTE_CONFIG"];
            if (string.IsNullOrEmpty(remoteConfigJson))
                return Results.Ok(new { });

            try
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(remoteConfigJson);
                return Results.Ok(parsed);
            }
            catch (JsonException)
            {
                return Results.Ok(new { });
            }
        });
    }
}
