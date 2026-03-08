using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aevatar.App.Application.Services;
using FluentAssertions;

namespace Aevatar.App.Host.Api.Tests;

public sealed class ReferralIntegrationTests : IClassFixture<AppTestFixture>
{
    private readonly AppTestFixture _fx;

    public ReferralIntegrationTests(AppTestFixture fx) => _fx = fx;

    [Fact]
    public async Task Click_MissingRef_Returns400()
    {
        var res = await _fx.Client.PostAsJsonAsync("/api/referral/click",
            new { Ref = (string?)null, Page = "https://app.test" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Click_EmptyRef_Returns400()
    {
        var res = await _fx.Client.PostAsJsonAsync("/api/referral/click",
            new { Ref = "  ", Page = "https://app.test" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Click_PartnerNotFound_Returns404()
    {
        _fx.ToltStub.NextClickResult = null;

        var res = await _fx.Client.PostAsJsonAsync("/api/referral/click",
            new { Ref = "unknown-ref", Page = "https://app.test" });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Click_PartnerFound_ReturnsPartnerId()
    {
        _fx.ToltStub.NextClickResult = new ToltClickResult("partner-xyz");

        var res = await _fx.Client.PostAsJsonAsync("/api/referral/click",
            new { Ref = "valid-ref", Page = "https://app.test", Device = "desktop" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("referralCode").GetString().Should().Be("partner-xyz");
    }

    [Fact]
    public async Task Bind_NoAuth_Returns401()
    {
        var res = await _fx.Client.PostAsJsonAsync("/api/referral/bind",
            new { ReferralCode = "some-code" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Bind_MissingReferralCode_Returns400()
    {
        using var client = _fx.CreateAuthenticatedClient("ref-user-1", "ref1@test.com");

        var res = await client.PostAsJsonAsync("/api/referral/bind",
            new { ReferralCode = (string?)null });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bind_ToltFailure_Returns422()
    {
        _fx.ToltStub.NextBindResult = new ToltBindResult(false, null, "invalid referral");
        using var client = _fx.CreateAuthenticatedClient("ref-user-2", "ref2@test.com");

        var res = await client.PostAsJsonAsync("/api/referral/bind",
            new { ReferralCode = "bad-code" });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Bind_Success_Returns200()
    {
        _fx.ToltStub.NextBindResult = new ToltBindResult(true, "cust-123", null);
        using var client = _fx.CreateAuthenticatedClient("ref-user-3", "ref3@test.com");

        var res = await client.PostAsJsonAsync("/api/referral/bind",
            new { ReferralCode = "valid-code" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
    }
}
