using System.Text.Json;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message });
}
