using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Hosting;
using Aevatar.Mainnet.Host.Api.Hosting;
using FluentAssertions;

namespace Aevatar.Hosting.Tests;

public sealed class MainnetNyxIdCatalogHealthTests
{
    [Fact]
    public void Evaluate_WhenSpecFetchTokenMissing_ShouldBeUnhealthy()
    {
        var result = NyxIdCatalogHealthContributor.Evaluate(new NyxIdSpecCatalogStatus(
            BaseUrlConfigured: true,
            SpecFetchTokenConfigured: false,
            InitialRefreshAttempted: false,
            RefreshInProgress: false,
            OperationCount: 0,
            LastSuccessfulRefreshUtc: null,
            LastRefreshError: null,
            LastRefreshFailureKind: null));

        result.Status.Should().Be(AevatarHealthStatuses.Unhealthy);
        result.Message.Should().Contain("spec fetch token is missing");
        result.Details.Should().Contain("baseUrlConfigured", "true");
        result.Details.Should().Contain("specFetchTokenConfigured", "false");
        result.Details.Should().Contain("operationCount", "0");
    }

    [Fact]
    public void Evaluate_WhenCatalogLoaded_ShouldBeHealthy()
    {
        var refreshedAt = DateTimeOffset.Parse("2026-04-30T08:00:00Z");

        var result = NyxIdCatalogHealthContributor.Evaluate(new NyxIdSpecCatalogStatus(
            BaseUrlConfigured: true,
            SpecFetchTokenConfigured: true,
            InitialRefreshAttempted: true,
            RefreshInProgress: false,
            OperationCount: 12,
            LastSuccessfulRefreshUtc: refreshedAt,
            LastRefreshError: null,
            LastRefreshFailureKind: null));

        result.Status.Should().Be(AevatarHealthStatuses.Healthy);
        result.Message.Should().Contain("loaded 12 operations");
        result.Details.Should().Contain("lastSuccessfulRefreshUtc", refreshedAt.ToString("O"));
    }

    [Fact]
    public void Evaluate_WhenCatalogLoadedButLastRefreshWasEmpty_ShouldStayHealthyWithFailureDetails()
    {
        var result = NyxIdCatalogHealthContributor.Evaluate(new NyxIdSpecCatalogStatus(
            BaseUrlConfigured: true,
            SpecFetchTokenConfigured: true,
            InitialRefreshAttempted: true,
            RefreshInProgress: false,
            OperationCount: 12,
            LastSuccessfulRefreshUtc: DateTimeOffset.Parse("2026-04-30T08:00:00Z"),
            LastRefreshError: "Spec yielded no operations.",
            LastRefreshFailureKind: NyxIdSpecCatalogRefreshFailureKind.EmptySpec));

        result.Status.Should().Be(AevatarHealthStatuses.Healthy);
        result.Message.Should().Contain("loaded 12 operations");
        result.Details.Should().Contain("lastRefreshFailureKind", nameof(NyxIdSpecCatalogRefreshFailureKind.EmptySpec));
    }

    [Fact]
    public void Evaluate_WhenInitialRefreshStillLoading_ShouldBeHealthy()
    {
        var result = NyxIdCatalogHealthContributor.Evaluate(new NyxIdSpecCatalogStatus(
            BaseUrlConfigured: true,
            SpecFetchTokenConfigured: true,
            InitialRefreshAttempted: false,
            RefreshInProgress: true,
            OperationCount: 0,
            LastSuccessfulRefreshUtc: null,
            LastRefreshError: null,
            LastRefreshFailureKind: null));

        result.Status.Should().Be(AevatarHealthStatuses.Healthy);
        result.Message.Should().Contain("still loading");
        result.Details.Should().Contain("refreshInProgress", "true");
    }

    [Fact]
    public void Evaluate_WhenInitialRefreshFailedTransiently_ShouldStayReady()
    {
        var result = NyxIdCatalogHealthContributor.Evaluate(new NyxIdSpecCatalogStatus(
            BaseUrlConfigured: true,
            SpecFetchTokenConfigured: true,
            InitialRefreshAttempted: true,
            RefreshInProgress: false,
            OperationCount: 0,
            LastSuccessfulRefreshUtc: null,
            LastRefreshError: "The request timed out.",
            LastRefreshFailureKind: NyxIdSpecCatalogRefreshFailureKind.NetworkError));

        result.Status.Should().Be(AevatarHealthStatuses.Healthy);
        result.Message.Should().Contain("temporarily unavailable");
        result.Details.Should().Contain("lastRefreshFailureKind", nameof(NyxIdSpecCatalogRefreshFailureKind.NetworkError));
    }

    [Theory]
    [InlineData(NyxIdSpecCatalogRefreshFailureKind.Unauthorized)]
    [InlineData(NyxIdSpecCatalogRefreshFailureKind.Forbidden)]
    public void Evaluate_WhenTokenRejected_ShouldBeUnhealthy(NyxIdSpecCatalogRefreshFailureKind failureKind)
    {
        var result = NyxIdCatalogHealthContributor.Evaluate(new NyxIdSpecCatalogStatus(
            BaseUrlConfigured: true,
            SpecFetchTokenConfigured: true,
            InitialRefreshAttempted: true,
            RefreshInProgress: false,
            OperationCount: 0,
            LastSuccessfulRefreshUtc: null,
            LastRefreshError: "Response status code does not indicate success.",
            LastRefreshFailureKind: failureKind));

        result.Status.Should().Be(AevatarHealthStatuses.Unhealthy);
        result.Message.Should().Contain("token was rejected");
    }

    [Fact]
    public void Evaluate_WhenSpecHasNoOperations_ShouldBeUnhealthy()
    {
        var result = NyxIdCatalogHealthContributor.Evaluate(new NyxIdSpecCatalogStatus(
            BaseUrlConfigured: true,
            SpecFetchTokenConfigured: true,
            InitialRefreshAttempted: true,
            RefreshInProgress: false,
            OperationCount: 0,
            LastSuccessfulRefreshUtc: null,
            LastRefreshError: "Spec yielded no operations.",
            LastRefreshFailureKind: NyxIdSpecCatalogRefreshFailureKind.EmptySpec));

        result.Status.Should().Be(AevatarHealthStatuses.Unhealthy);
        result.Message.Should().Contain("catalog is empty");
    }
}
