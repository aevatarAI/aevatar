using System.CommandLine;
using System.Text.Json;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class AppServicesCommand
{
    public static Command Create(Option<string?> urlOption)
    {
        var command = new Command("services", "List services in a scope.");

        var scopeOption = new Option<string>("--scope", "Scope ID (tenant).") { IsRequired = true };
        var takeOption = new Option<int>("--take", () => 20, "Max number of services to return.");

        command.AddOption(scopeOption);
        command.AddOption(takeOption);
        command.AddOption(urlOption);

        command.SetHandler(async (string scope, int take, string? url) =>
        {
            var baseUrl = CliAppConfigStore.ResolveApiBaseUrl(url, "http://localhost:5080", out _);
            using var client = new AppApiClient(baseUrl);

            try
            {
                var result = await client.ListServicesAsync(scope, take, CancellationToken.None);
                PrintServicesTable(scope, result);
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
            }
        }, scopeOption, takeOption, urlOption);

        return command;
    }

    private static void PrintServicesTable(string scope, JsonElement array)
    {
        Console.WriteLine($"Services in scope '{scope}':");
        Console.WriteLine();

        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            Console.WriteLine("  (no services found)");
            return;
        }

        Console.WriteLine($"  {"SERVICE ID",-30} {"DISPLAY NAME",-25} {"STATUS",-15} {"ENDPOINTS"}");
        Console.WriteLine($"  {new string('-', 30)} {new string('-', 25)} {new string('-', 15)} {new string('-', 30)}");

        foreach (var svc in array.EnumerateArray())
        {
            var serviceId = GetStr(svc, "serviceId");
            var displayName = GetStr(svc, "displayName");
            var status = GetStr(svc, "deploymentStatus");

            var endpoints = new List<string>();
            if (svc.TryGetProperty("endpoints", out var eps) && eps.ValueKind == JsonValueKind.Array)
            {
                foreach (var ep in eps.EnumerateArray())
                {
                    var kind = GetStr(ep, "kind");
                    if (!string.IsNullOrEmpty(kind) && kind != "unspecified")
                        endpoints.Add(kind);
                    else
                        endpoints.Add(GetStr(ep, "endpointId"));
                }
            }

            Console.WriteLine($"  {Truncate(serviceId, 30),-30} {Truncate(displayName, 25),-25} {Truncate(status, 15),-15} {string.Join(", ", endpoints)}");
        }
    }

    private static string GetStr(JsonElement el, string name)
    {
        // Try camelCase then PascalCase.
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        if (el.TryGetProperty(pascal, out v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";
}
