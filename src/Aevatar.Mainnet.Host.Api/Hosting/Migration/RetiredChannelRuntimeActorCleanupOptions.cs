using Microsoft.Extensions.Configuration;

namespace Aevatar.Mainnet.Host.Api.Hosting.Migration;

public sealed class RetiredChannelRuntimeActorCleanupOptions
{
    private const string SectionName = "Aevatar:RetiredChannelRuntimeActorCleanup";

    public bool Enabled { get; init; } = true;

    public bool ResetEventStreams { get; init; } = true;

    public bool CleanupReadModels { get; init; } = true;

    public int InProgressTimeoutSeconds { get; init; } = 300;

    public int WaitPollMilliseconds { get; init; } = 1000;

    public int ReadModelCleanupPageSize { get; init; } = 500;

    public static RetiredChannelRuntimeActorCleanupOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        return new RetiredChannelRuntimeActorCleanupOptions
        {
            Enabled = ResolveBool(section, nameof(Enabled), fallback: true),
            ResetEventStreams = ResolveBool(section, nameof(ResetEventStreams), fallback: true),
            CleanupReadModels = ResolveBool(section, nameof(CleanupReadModels), fallback: true),
            InProgressTimeoutSeconds = ResolvePositiveInt(
                section,
                nameof(InProgressTimeoutSeconds),
                fallback: 300),
            WaitPollMilliseconds = ResolvePositiveInt(
                section,
                nameof(WaitPollMilliseconds),
                fallback: 1000),
            ReadModelCleanupPageSize = ResolvePositiveInt(
                section,
                nameof(ReadModelCleanupPageSize),
                fallback: 500),
        };
    }

    private static bool ResolveBool(IConfiguration section, string key, bool fallback)
    {
        var raw = section[key];
        return bool.TryParse(raw, out var value) ? value : fallback;
    }

    private static int ResolvePositiveInt(IConfiguration section, string key, int fallback)
    {
        var raw = section[key];
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
