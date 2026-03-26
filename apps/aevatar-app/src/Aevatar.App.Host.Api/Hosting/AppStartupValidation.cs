using Microsoft.Extensions.Logging;

namespace Aevatar.App.Host.Api.Hosting;

public static class AppStartupValidation
{
    private static readonly string[] StorageDegradedKeys =
    [
        "App:Storage:PlatformApiUrl",
        "App:Storage:PipelineId",
        "App:Storage:PlatformApiToken"
    ];

    public static void ValidateRequiredConfiguration(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger? logger = null)
    {
        var missingKeys = new List<string>();

        Require(configuration, "App:Id", missingKeys);
        Require(configuration, "App:Storage:BucketName", missingKeys);
        Require(configuration, "Orleans:ClusterId", missingKeys);
        Require(configuration, "Orleans:ServiceId", missingKeys);
        if (!environment.IsDevelopment())
        {
            Require(configuration, "Firebase:ProjectId", missingKeys);
        }

        if (missingKeys.Count > 0)
            throw new InvalidOperationException(
                $"Missing required configuration keys: {string.Join(", ", missingKeys)}");

        var degradedKeys = new List<string>();
        foreach (var key in StorageDegradedKeys)
        {
            if (string.IsNullOrWhiteSpace(configuration[key]))
                degradedKeys.Add(key);
        }

        if (degradedKeys.Count > 0)
            logger?.LogWarning(
                "Storage configuration incomplete — missing: {Keys}. " +
                "/health storage will report degraded; upload/delete endpoints will be unavailable",
                string.Join(", ", degradedKeys));
    }

    private static void Require(
        IConfiguration configuration,
        string key,
        ICollection<string> missingKeys)
    {
        if (string.IsNullOrWhiteSpace(configuration[key]))
            missingKeys.Add(key);
    }
}
