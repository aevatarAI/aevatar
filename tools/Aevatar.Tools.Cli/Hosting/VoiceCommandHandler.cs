namespace Aevatar.Tools.Cli.Hosting;

internal static class VoiceCommandHandler
{
    private const string DefaultApiBaseUrl = "http://localhost:5080";
    private const int DefaultSampleRateHz = 24000;

    public static async Task RunAsync(
        string agentId,
        int port,
        string? urlOverride,
        string? providerOverride,
        string? voiceOverride,
        CancellationToken cancellationToken)
    {
        var normalizedAgentId = (agentId ?? string.Empty).Trim();
        if (normalizedAgentId.Length == 0)
        {
            Console.Error.WriteLine("Voice agent is required. Example: aevatar voice --agent robot-dog-1.");
            return;
        }

        var normalizedProvider = ResolveProvider(providerOverride);
        var normalizedVoice = ResolveVoice(voiceOverride);
        var sampleRateHz = ResolveSampleRateHz();

        string apiBaseUrl;
        try
        {
            apiBaseUrl = CliAppConfigStore.ResolveApiBaseUrl(urlOverride, DefaultApiBaseUrl, out var warning);
            if (!string.IsNullOrWhiteSpace(warning))
                Console.WriteLine($"[warn] {warning}");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return;
        }

        if (!string.IsNullOrWhiteSpace(voiceOverride))
        {
            Console.WriteLine(
                "[warn] Phase A browser voice UI does not override the host-side voice session yet. " +
                "The requested voice is displayed in the UI as a session hint only.");
        }

        string localBaseUrl;
        try
        {
            localBaseUrl = await AppUiHostLauncher.EnsureReadyAsync(port, apiBaseUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return;
        }

        var uiUrl = BuildUiUrl(localBaseUrl, normalizedAgentId, normalizedProvider, normalizedVoice, sampleRateHz);
        BrowserLauncher.Open(uiUrl);
        Console.WriteLine($"Opened aevatar voice UI: {uiUrl}");
    }

    internal static string BuildUiUrl(
        string localBaseUrl,
        string agentId,
        string? provider,
        string? voice,
        int sampleRateHz)
    {
        var query = new List<string>
        {
            $"agent={Uri.EscapeDataString(agentId)}",
            $"sampleRateHz={sampleRateHz}",
        };

        if (!string.IsNullOrWhiteSpace(provider))
            query.Add($"provider={Uri.EscapeDataString(provider)}");

        if (!string.IsNullOrWhiteSpace(voice))
            query.Add($"voice={Uri.EscapeDataString(voice)}");

        return $"{localBaseUrl.TrimEnd('/')}/voice?{string.Join("&", query)}";
    }

    internal static string? ResolveProvider(string? providerOverride)
    {
        var configuredProvider = CliAppConfigStore.TryGetConfigValue("Cli:Voice:Provider");
        return NormalizeProvider(providerOverride) ?? NormalizeProvider(configuredProvider);
    }

    internal static string? ResolveVoice(string? voiceOverride)
    {
        var configuredVoice = CliAppConfigStore.TryGetConfigValue("Cli:Voice:Voice");
        return NormalizeValue(voiceOverride) ?? NormalizeValue(configuredVoice);
    }

    internal static int ResolveSampleRateHz()
    {
        var configuredSampleRate = CliAppConfigStore.TryGetConfigValue("Cli:Voice:SampleRateHz");
        return int.TryParse(configuredSampleRate, out var parsed) && parsed > 0
            ? parsed
            : DefaultSampleRateHz;
    }

    private static string? NormalizeProvider(string? value)
    {
        var normalized = NormalizeValue(value)?.ToLowerInvariant();
        switch (normalized)
        {
            case null:
                return null;
            case "openai":
            case "voice_presence_openai":
                return "openai";
            case "minicpm":
            case "minicpm-o":
            case "voice_presence_minicpm":
            case "voice_presence_minicpm_o":
                return "minicpm";
            default:
                Console.WriteLine($"[warn] Ignoring unsupported voice provider '{value}'. Supported values: openai, minicpm.");
                return null;
        }
    }

    private static string? NormalizeValue(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
