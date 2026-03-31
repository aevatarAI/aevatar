using System.CommandLine;
using System.Text.Json;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class AppLogsCommand
{
    public static Command Create(Option<string?> urlOption)
    {
        var command = new Command("logs", "View actor timeline (run logs).");

        var actorOption = new Option<string>("--actor", "Actor ID to view timeline for.") { IsRequired = true };
        var takeOption = new Option<int>("--take", () => 50, "Max number of timeline entries.");

        command.AddOption(actorOption);
        command.AddOption(takeOption);
        command.AddOption(urlOption);

        command.SetHandler(async (string actor, int take, string? url) =>
        {
            var baseUrl = CliAppConfigStore.ResolveApiBaseUrl(url, "http://localhost:5080", out _);
            using var client = new AppApiClient(baseUrl);

            try
            {
                var result = await client.GetActorTimelineAsync(actor, take, CancellationToken.None);
                PrintTimeline(actor, take, result);
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
            }
        }, actorOption, takeOption, urlOption);

        return command;
    }

    private static void PrintTimeline(string actorId, int take, JsonElement data)
    {
        Console.WriteLine($"Timeline for actor '{actorId}' (last {take}):");
        Console.WriteLine();

        JsonElement items;
        if (data.ValueKind == JsonValueKind.Array)
        {
            items = data;
        }
        else if (data.ValueKind == JsonValueKind.Object &&
                 (data.TryGetProperty("items", out items) || data.TryGetProperty("Items", out items)))
        {
            // OK
        }
        else
        {
            Console.WriteLine("  (no timeline entries)");
            return;
        }

        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            Console.WriteLine("  (no timeline entries)");
            return;
        }

        Console.WriteLine($"  {"TIMESTAMP",-28} {"TYPE",-25} {"SUMMARY"}");
        Console.WriteLine($"  {new string('-', 28)} {new string('-', 25)} {new string('-', 50)}");

        foreach (var item in items.EnumerateArray())
        {
            var timestamp = GetStr(item, "timestamp", "occurredAt", "OccurredAt", "Timestamp");
            var type = GetStr(item, "eventType", "EventType", "type", "Type");
            var summary = BuildSummary(item);

            Console.WriteLine($"  {Truncate(timestamp, 28),-28} {Truncate(type, 25),-25} {summary}");
        }
    }

    private static string BuildSummary(JsonElement item)
    {
        // Try common summary fields.
        var summary = GetStr(item, "summary", "Summary", "description", "Description");
        if (!string.IsNullOrEmpty(summary))
            return summary;

        // Try to extract from payload/data.
        if (item.TryGetProperty("payload", out var payload) || item.TryGetProperty("Payload", out payload))
        {
            if (payload.ValueKind == JsonValueKind.String)
                return Truncate(payload.GetString() ?? "", 60);
            if (payload.ValueKind == JsonValueKind.Object)
                return Truncate(payload.ToString(), 60);
        }

        if (item.TryGetProperty("data", out var data) || item.TryGetProperty("Data", out data))
        {
            if (data.ValueKind == JsonValueKind.String)
                return Truncate(data.GetString() ?? "", 60);
            if (data.ValueKind == JsonValueKind.Object)
                return Truncate(data.ToString(), 60);
        }

        return "";
    }

    private static string GetStr(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }
        return "";
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";
}
