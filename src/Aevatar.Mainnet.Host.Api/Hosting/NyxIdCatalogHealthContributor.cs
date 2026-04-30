using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Mainnet.Host.Api.Hosting;

internal static class NyxIdCatalogHealthContributor
{
    public static AevatarHealthContributorRegistration Create() =>
        new()
        {
            Name = "nyxid-catalog",
            Category = "dependency",
            Critical = true,
            ProbeAsync = static (serviceProvider, _) =>
            {
                var catalog = serviceProvider.GetRequiredService<NyxIdSpecCatalog>();
                return ValueTask.FromResult(Evaluate(catalog.GetStatus()));
            },
        };

    internal static AevatarHealthContributorResult Evaluate(NyxIdSpecCatalogStatus status)
    {
        var details = BuildDetails(status);

        if (!status.BaseUrlConfigured)
        {
            return AevatarHealthContributorResult.Healthy(
                "NyxID catalog is not configured.",
                details);
        }

        if (!status.SpecFetchTokenConfigured)
        {
            return AevatarHealthContributorResult.Unhealthy(
                "NyxID spec fetch token is missing; generic capability discovery is unavailable.",
                details);
        }

        if (status.OperationCount > 0)
        {
            return AevatarHealthContributorResult.Healthy(
                $"NyxID spec catalog loaded {status.OperationCount} operations.",
                details);
        }

        if (!status.InitialRefreshAttempted)
        {
            return AevatarHealthContributorResult.Healthy(
                "NyxID spec catalog refresh is still loading.",
                details);
        }

        if (status.LastRefreshFailureKind is NyxIdSpecCatalogRefreshFailureKind.Unauthorized
            or NyxIdSpecCatalogRefreshFailureKind.Forbidden)
        {
            return AevatarHealthContributorResult.Unhealthy(
                "NyxID spec fetch token was rejected; generic capability discovery is unavailable.",
                details);
        }

        if (status.LastRefreshFailureKind == NyxIdSpecCatalogRefreshFailureKind.EmptySpec)
        {
            return AevatarHealthContributorResult.Unhealthy(
                "NyxID spec catalog is empty; generic capability discovery is unavailable.",
                details);
        }

        return AevatarHealthContributorResult.Healthy(
            "NyxID spec catalog is temporarily unavailable; generic capability discovery is waiting for refresh.",
            details);
    }

    private static IReadOnlyDictionary<string, string> BuildDetails(NyxIdSpecCatalogStatus status)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["baseUrlConfigured"] = FormatBool(status.BaseUrlConfigured),
            ["specFetchTokenConfigured"] = FormatBool(status.SpecFetchTokenConfigured),
            ["initialRefreshAttempted"] = FormatBool(status.InitialRefreshAttempted),
            ["refreshInProgress"] = FormatBool(status.RefreshInProgress),
            ["operationCount"] = status.OperationCount.ToString(),
        };

        if (status.LastSuccessfulRefreshUtc.HasValue)
            details["lastSuccessfulRefreshUtc"] = status.LastSuccessfulRefreshUtc.Value.ToString("O");

        if (!string.IsNullOrWhiteSpace(status.LastRefreshError))
            details["lastRefreshError"] = status.LastRefreshError;

        if (status.LastRefreshFailureKind.HasValue)
            details["lastRefreshFailureKind"] = status.LastRefreshFailureKind.Value.ToString();

        return details;
    }

    private static string FormatBool(bool value) => value ? "true" : "false";
}
