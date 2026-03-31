using System.CommandLine;
using System.Text.Json;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class AppBindingsCommand
{
    public static Command Create(Option<string?> urlOption)
    {
        var command = new Command("bindings", "View service bindings.");

        var serviceOption = new Option<string>("--service", "Service ID to inspect bindings for.") { IsRequired = true };
        var scopeOption = new Option<string?>("--scope", "Scope ID (tenant) filter.");

        command.AddOption(serviceOption);
        command.AddOption(scopeOption);
        command.AddOption(urlOption);

        command.SetHandler(async (string service, string? scope, string? url) =>
        {
            var baseUrl = CliAppConfigStore.ResolveApiBaseUrl(url, "http://localhost:5080", out _);
            using var client = new AppApiClient(baseUrl);

            try
            {
                var result = await client.GetBindingsAsync(service, scope, CancellationToken.None);
                PrintBindingsTable(service, result);
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
            }
        }, serviceOption, scopeOption, urlOption);

        return command;
    }

    private static void PrintBindingsTable(string serviceId, JsonElement root)
    {
        Console.WriteLine($"Bindings for service '{serviceId}':");
        Console.WriteLine();

        JsonElement bindings;
        if (root.ValueKind == JsonValueKind.Object &&
            (root.TryGetProperty("bindings", out bindings) || root.TryGetProperty("Bindings", out bindings)))
        {
            // OK
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            bindings = root;
        }
        else
        {
            Console.WriteLine("  (no bindings found)");
            return;
        }

        if (bindings.ValueKind != JsonValueKind.Array || bindings.GetArrayLength() == 0)
        {
            Console.WriteLine("  (no bindings found)");
            return;
        }

        Console.WriteLine($"  {"BINDING ID",-25} {"DISPLAY NAME",-25} {"KIND",-12} {"TARGET"}");
        Console.WriteLine($"  {new string('-', 25)} {new string('-', 25)} {new string('-', 12)} {new string('-', 40)}");

        foreach (var b in bindings.EnumerateArray())
        {
            var bindingId = GetStr(b, "bindingId");
            var displayName = GetStr(b, "displayName");
            var kind = GetStr(b, "bindingKind");
            var target = ResolveTarget(b, kind);

            Console.WriteLine($"  {Truncate(bindingId, 25),-25} {Truncate(displayName, 25),-25} {Truncate(kind, 12),-12} {target}");
        }
    }

    private static string ResolveTarget(JsonElement binding, string kind)
    {
        return kind switch
        {
            "service" => ResolveServiceRef(binding),
            "connector" => ResolveConnectorRef(binding),
            "secret" => ResolveSecretRef(binding),
            _ => ""
        };
    }

    private static string ResolveServiceRef(JsonElement binding)
    {
        if (!TryGetObject(binding, "serviceRef", out var sref))
            return "";
        var endpointId = GetStr(sref, "endpointId");
        if (TryGetObject(sref, "identity", out var identity))
        {
            var sid = GetStr(identity, "serviceId");
            return string.IsNullOrEmpty(endpointId) ? sid : $"{sid}/{endpointId}";
        }
        return endpointId;
    }

    private static string ResolveConnectorRef(JsonElement binding)
    {
        if (!TryGetObject(binding, "connectorRef", out var cref))
            return "";
        var connType = GetStr(cref, "connectorType");
        var connId = GetStr(cref, "connectorId");
        return string.IsNullOrEmpty(connType) ? connId : $"{connType}/{connId}";
    }

    private static string ResolveSecretRef(JsonElement binding)
    {
        if (!TryGetObject(binding, "secretRef", out var sref))
            return "";
        return GetStr(sref, "secretName");
    }

    private static bool TryGetObject(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object)
            return true;
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        if (parent.TryGetProperty(pascal, out value) && value.ValueKind == JsonValueKind.Object)
            return true;
        value = default;
        return false;
    }

    private static string GetStr(JsonElement el, string name)
    {
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
