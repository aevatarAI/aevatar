using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;

namespace Aevatar.App.Host.Api.Tests;

public sealed class WebhookIntegrationTests : IClassFixture<AppTestFixture>
{
    private readonly AppTestFixture _fx;

    public WebhookIntegrationTests(AppTestFixture fx) => _fx = fx;

    [Fact]
    public async Task Webhook_NoAuth_Returns401()
    {
        var res = await _fx.Client.PostAsJsonAsync("/api/webhooks/revenuecat",
            new { @event = new { type = "INITIAL_PURCHASE", transaction_id = "tx1", app_user_id = "u1" } });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_WrongToken_Returns401()
    {
        using var client = new HttpClient { BaseAddress = _fx.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "wrong-secret");

        var res = await client.PostAsJsonAsync("/api/webhooks/revenuecat",
            new { @event = new { type = "INITIAL_PURCHASE", transaction_id = "tx1", app_user_id = "u1" } });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_CorrectToken_Returns200()
    {
        using var client = new HttpClient { BaseAddress = _fx.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AppTestFixture.WebhookSecret);

        var beforeCount = _fx.WebhookStub.HandleCount;
        var res = await client.PostAsJsonAsync("/api/webhooks/revenuecat",
            new { @event = new { type = "INITIAL_PURCHASE", transaction_id = "tx1", app_user_id = "u1" } });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        _fx.WebhookStub.HandleCount.Should().BeGreaterThan(beforeCount);
    }
}
