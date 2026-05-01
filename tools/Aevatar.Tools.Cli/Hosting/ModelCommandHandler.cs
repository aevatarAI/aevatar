using System.Text.Json;

namespace Aevatar.Tools.Cli.Hosting;

internal static class ModelCommandHandler
{
    private const string LocalFallbackUrl = "http://localhost:6688";

    public static async Task<int> ListAsync(string? urlOverride, CancellationToken cancellationToken)
    {
        if (!TryCreateClient(urlOverride, out var client, out var token, out var exitCode))
            return exitCode;

        using (client)
        {
            try
            {
                var options = ParseOptions(await client.GetUserLlmOptionsAsync(token, cancellationToken));
                PrintOptions(options);
                return 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine(ex.Message);
                return 6;
            }
        }
    }

    public static async Task<int> UseAsync(
        string value,
        string? urlOverride,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Console.Error.WriteLine("Usage: aevatar model use <service-number|service-name|model-name>");
            return 2;
        }

        if (!TryCreateClient(urlOverride, out var client, out var token, out var exitCode))
            return exitCode;

        using (client)
        {
            try
            {
                var options = ParseOptions(await client.GetUserLlmOptionsAsync(token, cancellationToken));
                var requested = value.Trim();
                var option = ResolveOption(requested, options.Available);
                if (option is not null)
                {
                    await client.SaveUserLlmPreferenceAsync(
                        token,
                        new { serviceId = option.ServiceId },
                        cancellationToken);
                    Console.WriteLine($"Selected LLM service: {option.DisplayName}");
                    return 0;
                }

                await client.SaveUserLlmPreferenceAsync(token, new { model = requested }, cancellationToken);
                Console.WriteLine($"Set model override: {requested}");
                return 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine(ex.Message);
                return 6;
            }
        }
    }

    public static async Task<int> PresetAsync(
        string presetId,
        string? urlOverride,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            Console.Error.WriteLine("Usage: aevatar model preset <preset-id>");
            return 2;
        }

        if (!TryCreateClient(urlOverride, out var client, out var token, out var exitCode))
            return exitCode;

        using (client)
        {
            try
            {
                await client.SaveUserLlmPreferenceAsync(
                    token,
                    new { presetId = presetId.Trim() },
                    cancellationToken);
                Console.WriteLine($"Applied LLM preset: {presetId.Trim()}");
                return 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine(ex.Message);
                return 6;
            }
        }
    }

    public static async Task<int> ResetAsync(string? urlOverride, CancellationToken cancellationToken)
    {
        if (!TryCreateClient(urlOverride, out var client, out var token, out var exitCode))
            return exitCode;

        using (client)
        {
            try
            {
                await client.SaveUserLlmPreferenceAsync(token, new { reset = true }, cancellationToken);
                Console.WriteLine("Cleared LLM service/model preference.");
                return 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine(ex.Message);
                return 6;
            }
        }
    }

    private static bool TryCreateClient(
        string? urlOverride,
        out AppApiClient client,
        out string token,
        out int exitCode)
    {
        client = null!;
        token = NyxIdTokenStore.LoadToken() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("NyxID login is required. Run `aevatar login` first.");
            exitCode = 2;
            return false;
        }

        try
        {
            var baseUrl = CliAppConfigStore.ResolveApiBaseUrl(urlOverride, LocalFallbackUrl, out var warning);
            if (!string.IsNullOrWhiteSpace(warning))
                Console.WriteLine($"[warn] {warning}");
            client = new AppApiClient(baseUrl);
            exitCode = 0;
            return true;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            exitCode = 2;
            return false;
        }
    }

    private static void PrintOptions(CliLlmOptions options)
    {
        Console.WriteLine("LLM options");
        Console.WriteLine(options.Current is null
            ? "Current: (none)"
            : $"Current: {options.Current.DisplayName}{FormatModelSuffix(options.Current.DefaultModel)}");

        if (options.Available.Count == 0)
        {
            Console.WriteLine("No routable LLM services.");
            if (options.SetupHint is not null)
            {
                if (options.SetupHint.Presets.Count > 0)
                {
                    Console.WriteLine("Presets:");
                    foreach (var preset in options.SetupHint.Presets)
                        Console.WriteLine($"  {preset.Id}: {preset.Title}");
                }

                if (!string.IsNullOrWhiteSpace(options.SetupHint.SetupUrl))
                    Console.WriteLine($"Setup: {options.SetupHint.SetupUrl}");
            }
            return;
        }

        Console.WriteLine("Services:");
        for (var i = 0; i < options.Available.Count; i++)
        {
            var option = options.Available[i];
            var marker = options.Current is not null &&
                string.Equals(option.ServiceId, options.Current.ServiceId, StringComparison.OrdinalIgnoreCase)
                    ? " *"
                    : string.Empty;
            Console.WriteLine(
                $"  {i + 1}. {option.DisplayName}{FormatModelSuffix(option.DefaultModel)} [{option.Source}, {option.Status}]{marker}");
        }
    }

    private static string FormatModelSuffix(string? model) =>
        string.IsNullOrWhiteSpace(model) ? string.Empty : $" / {model.Trim()}";

    private static CliLlmOption? ResolveOption(string requested, IReadOnlyList<CliLlmOption> available)
    {
        if (int.TryParse(requested, out var number))
        {
            if (number < 1 || number > available.Count)
                throw new InvalidOperationException($"No LLM service number {number}. Run `aevatar model list`.");
            return available[number - 1];
        }

        var exact = available.FirstOrDefault(option =>
            string.Equals(option.ServiceId, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.ServiceSlug, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.DisplayName, requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var fuzzy = available
            .Where(option =>
                option.ServiceSlug.Contains(requested, StringComparison.OrdinalIgnoreCase) ||
                option.DisplayName.Contains(requested, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return fuzzy.Length == 1 ? fuzzy[0] : null;
    }

    private static CliLlmOptions ParseOptions(JsonElement root)
    {
        var available = root.TryGetProperty("available", out var availableElement) &&
            availableElement.ValueKind == JsonValueKind.Array
                ? availableElement.EnumerateArray().Select(ParseOption).ToArray()
                : [];
        var current = root.TryGetProperty("current", out var currentElement) &&
            currentElement.ValueKind == JsonValueKind.Object
                ? ParseOption(currentElement)
                : null;
        var setupHint = root.TryGetProperty("setupHint", out var setupElement) &&
            setupElement.ValueKind == JsonValueKind.Object
                ? ParseSetupHint(setupElement)
                : null;
        return new CliLlmOptions(current, available, setupHint);
    }

    private static CliLlmOption ParseOption(JsonElement element) => new(
        ServiceId: ReadString(element, "serviceId"),
        ServiceSlug: ReadString(element, "serviceSlug"),
        DisplayName: ReadString(element, "displayName"),
        DefaultModel: ReadOptionalString(element, "defaultModel"),
        Status: ReadString(element, "status"),
        Source: ReadString(element, "source"));

    private static CliLlmSetupHint ParseSetupHint(JsonElement element)
    {
        var presets = element.TryGetProperty("presets", out var presetsElement) &&
            presetsElement.ValueKind == JsonValueKind.Array
                ? presetsElement.EnumerateArray()
                    .Select(preset => new CliLlmPreset(
                        ReadString(preset, "id"),
                        ReadOptionalString(preset, "title") ?? ReadString(preset, "id")))
                    .ToArray()
                : [];
        return new CliLlmSetupHint(ReadOptionalString(element, "setupUrl"), presets);
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        var value = ReadOptionalString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"LLM options response is missing '{propertyName}'.");
        return value.Trim();
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private sealed record CliLlmOptions(
        CliLlmOption? Current,
        IReadOnlyList<CliLlmOption> Available,
        CliLlmSetupHint? SetupHint);

    private sealed record CliLlmOption(
        string ServiceId,
        string ServiceSlug,
        string DisplayName,
        string? DefaultModel,
        string Status,
        string Source);

    private sealed record CliLlmSetupHint(string? SetupUrl, IReadOnlyList<CliLlmPreset> Presets);

    private sealed record CliLlmPreset(string Id, string Title);
}
