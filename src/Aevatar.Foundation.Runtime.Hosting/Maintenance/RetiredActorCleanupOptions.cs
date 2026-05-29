using Microsoft.Extensions.Configuration;

namespace Aevatar.Foundation.Runtime.Hosting.Maintenance;

/// <summary>
/// Tunables for the spec-driven retired-actor cleanup hosted service.
/// </summary>
// Refactor (issue1287-first):
//   Old pattern: options exposed marker lease timeout and polling controls.
//   New principle: options only gate idempotent cleanup actions; no lease timing remains.
public sealed class RetiredActorCleanupOptions
{
    public const string SectionName = "Aevatar:RetiredActorCleanup";

    public bool Enabled { get; init; } = true;

    public bool ResetEventStreams { get; init; } = true;

    public bool CleanupReadModels { get; init; } = true;

    public static RetiredActorCleanupOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        return new RetiredActorCleanupOptions
        {
            Enabled = ResolveBool(section, nameof(Enabled), fallback: true),
            ResetEventStreams = ResolveBool(section, nameof(ResetEventStreams), fallback: true),
            CleanupReadModels = ResolveBool(section, nameof(CleanupReadModels), fallback: true),
        };
    }

    private static bool ResolveBool(IConfiguration section, string key, bool fallback)
    {
        var raw = section[key];
        return bool.TryParse(raw, out var value) ? value : fallback;
    }
}
