using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour tests for <see cref="IdentityOAuthEndpoints.HandleAevatarOAuthClientStatusAsync"/>.
/// </summary>
[Collection(NyxIdRedirectUriEnvCollection.Name)]
public sealed class IdentityOAuthClientStatusEndpointTests : IDisposable
{
    private readonly string? _savedOverride;

    public IdentityOAuthClientStatusEndpointTests()
    {
        _savedOverride = Environment.GetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar);
        Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, null);
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, _savedOverride);

    [Fact]
    public async Task PublishesTheRuntimeFloorSoOperatorsCanReconcileConsentDefaults()
    {
        // /oauth/authorize sends no RFC 8707 `resource`, so NyxID's consent page
        // no longer marks these services as required — the app's
        // `default_service_catalog_slugs` is what preselects them. Ops needs the
        // deployment's own resolved floor to diff against those defaults, which
        // is what tools/ops/check_nyxid_consent_defaults.sh consumes.
        var result = await IdentityOAuthEndpoints.HandleAevatarOAuthClientStatusAsync(
            new FakeOAuthClientProvider(NewSnapshot()),
            Options.Create(new NyxIdBrokerOptions
            {
                ResourceServerBaseUrl = "https://api.example.test",
                RequiredLlmServiceSlug = " chrono-llm-public ",
                AdditionalRequiredServiceSlugs = ["ornn-api", "chrono-sandbox"],
            }),
            CancellationToken.None);

        var (document, statusCode) = await ReadJsonWithStatusAsync(result);
        using var _ = document;
        statusCode.Should().Be(StatusCodes.Status200OK);
        document.RootElement.GetProperty("required_service_slugs")
            .EnumerateArray()
            .Select(static slug => slug.GetString())
            .Should()
            .Equal("aevatar", "chrono-llm-public", "ornn-api", "chrono-sandbox");
        document.RootElement.GetProperty("consent_defaults_handoff").GetString()
            .Should().Contain("default_service_catalog_slugs");
    }

    [Fact]
    public async Task DoesNotPromoteAnOptionalServiceIntoTheRuntimeFloor()
    {
        // A Lark-only service must never become a required slug: it would block
        // every user who has not connected Lark from binding at all.
        var result = await IdentityOAuthEndpoints.HandleAevatarOAuthClientStatusAsync(
            new FakeOAuthClientProvider(NewSnapshot()),
            Options.Create(new NyxIdBrokerOptions
            {
                ResourceServerBaseUrl = "https://api.example.test",
                RequiredLlmServiceSlug = "chrono-llm-public",
                AdditionalRequiredServiceSlugs = ["ornn-api", "chrono-sandbox"],
            }),
            CancellationToken.None);

        var (document, _) = await ReadJsonWithStatusAsync(result);
        using var __ = document;
        document.RootElement.GetProperty("required_service_slugs")
            .EnumerateArray()
            .Select(static slug => slug.GetString())
            .Should()
            .NotContain("api-lark-bot");
    }

    private static AevatarOAuthClientSnapshot NewSnapshot() => new(
        ClientId: "client-1",
        ClientIdIssuedAt: DateTimeOffset.Parse("2026-08-05T09:00:00Z"),
        HmacKid: "v1",
        HmacKey: Convert.FromHexString(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
        HmacKeyRotatedAt: DateTimeOffset.Parse("2026-08-05T09:00:00Z"),
        NyxIdAuthority: "https://id.example.test",
        BrokerCapabilityObserved: true,
        BrokerCapabilityObservedAt: DateTimeOffset.Parse("2026-08-05T09:00:00Z"),
        RedirectUri: NyxIdRedirectUriResolver.Resolve(),
        OauthScope: AevatarOAuthClientScopes.AuthorizationScope)
    {
        RedirectUris = NyxIdRedirectUriResolver.ResolveRegisteredRedirectUris(),
    };

    private static async Task<(JsonDocument Document, int StatusCode)> ReadJsonWithStatusAsync(IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (JsonDocument.Parse(text), context.Response.StatusCode);
    }

    private sealed class FakeOAuthClientProvider(AevatarOAuthClientSnapshot snapshot)
        : IAevatarOAuthClientProvider
    {
        public Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }
}
