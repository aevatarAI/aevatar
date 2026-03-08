using System.Net;
using System.Text.Json;
using Aevatar.App.Application.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.App.Application.Tests;

public sealed class ToltAppServiceTests
{
    private static ToltAppService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new FakeHttpHandler(handler));
        var options = Options.Create(new ToltOptions
        {
            BaseUrl = "https://api.tolt.test",
            ApiKey = "test-key"
        });
        return new ToltAppService(httpClient, options, NullLogger<ToltAppService>.Instance);
    }

    [Fact]
    public async Task TrackClickAsync_ReturnsPartnerId_OnSuccess()
    {
        var svc = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("{\"success\":true,\"data\":[{\"partner_id\":\"partner-abc\"}]}")
        });

        var result = await svc.TrackClickAsync("ref123", "https://app.test/pricing", "desktop");

        result.Should().NotBeNull();
        result!.PartnerId.Should().Be("partner-abc");
    }

    [Fact]
    public async Task TrackClickAsync_ReturnsNull_OnNon200()
    {
        var svc = CreateService(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        var result = await svc.TrackClickAsync("ref123", "https://app.test", null);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TrackClickAsync_ReturnsNull_WhenPartnerIdEmpty()
    {
        var svc = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("{\"success\":true,\"data\":[{\"partner_id\":\"\"}]}")
        });

        var result = await svc.TrackClickAsync("ref123", "https://app.test", null);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TrackClickAsync_ReturnsNull_OnException()
    {
        var svc = CreateService(_ => throw new HttpRequestException("network error"));

        var result = await svc.TrackClickAsync("ref123", "https://app.test", null);

        result.Should().BeNull();
    }

    [Fact]
    public async Task BindReferralAsync_ReturnsSuccess_WithCustomerId()
    {
        var svc = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("{\"success\":true,\"data\":[{\"id\":\"cust-xyz\",\"customer_id\":\"user-1\"}]}")
        });

        var result = await svc.BindReferralAsync("user@test.com", "ref-code", "user-1");

        result.Success.Should().BeTrue();
        result.CustomerId.Should().Be("cust-xyz");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task BindReferralAsync_ReturnsFailure_OnNon200()
    {
        var svc = CreateService(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("invalid referral")
        });

        var result = await svc.BindReferralAsync("user@test.com", "bad-ref", "user-1");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("UnprocessableEntity");
    }

    [Fact]
    public async Task BindReferralAsync_ReturnsFailure_OnException()
    {
        var svc = CreateService(_ => throw new HttpRequestException("fail"));

        var result = await svc.BindReferralAsync("user@test.com", "ref", "user-1");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Internal error");
    }

    [Fact]
    public async Task TrackPaymentAsync_ReturnsSuccess_WithTxId()
    {
        var svc = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("{\"success\":true,\"data\":[{\"id\":\"tolt-tx-1\",\"status\":\"paid\"}]}")
        });

        var result = await svc.TrackPaymentAsync("cust-1", 999, "subscription", "tx-1", "pro", "app_store");

        result.Success.Should().BeTrue();
        result.ToltTransactionId.Should().Be("tolt-tx-1");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task TrackPaymentAsync_ReturnsFailure_OnNon200()
    {
        var svc = CreateService(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("error")
        });

        var result = await svc.TrackPaymentAsync("cust-1", 999, "subscription", "tx-1", "pro", "app_store");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("InternalServerError");
    }

    [Fact]
    public async Task TrackPaymentAsync_ReturnsFailure_OnException()
    {
        var svc = CreateService(_ => throw new HttpRequestException("fail"));

        var result = await svc.TrackPaymentAsync("cust-1", 999, "subscription", "tx-1", "pro", "app_store");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Internal error");
    }

    [Fact]
    public async Task TrackRefundAsync_ReturnsTrue_OnSuccess()
    {
        var svc = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("{\"success\":true,\"data\":[{\"id\":\"tolt-tx-1\",\"status\":\"refunded\"}]}")
        });

        var result = await svc.TrackRefundAsync("tolt-tx-1");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task TrackRefundAsync_ReturnsFalse_OnNon200()
    {
        var svc = CreateService(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not found")
        });

        var result = await svc.TrackRefundAsync("tolt-tx-1");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TrackRefundAsync_ReturnsFalse_OnException()
    {
        var svc = CreateService(_ => throw new HttpRequestException("fail"));

        var result = await svc.TrackRefundAsync("tolt-tx-1");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TrackClickAsync_PostsCorrectRequestBody()
    {
        HttpRequestMessage? captured = null;
        var svc = CreateService(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("{\"success\":true,\"data\":[{\"partner_id\":\"p1\"}]}")
            };
        });

        await svc.TrackClickAsync("myref", "https://page.test", "mobile");

        captured.Should().NotBeNull();
        captured!.RequestUri!.ToString().Should().Be("https://api.tolt.test/v1/clicks");
        captured.Method.Should().Be(HttpMethod.Post);

        var body = JsonSerializer.Deserialize<JsonElement>(
            await captured.Content!.ReadAsStringAsync());
        body.GetProperty("param").GetString().Should().Be("ref");
        body.GetProperty("value").GetString().Should().Be("myref");
        body.GetProperty("page").GetString().Should().Be("https://page.test");
        body.GetProperty("device").GetString().Should().Be("mobile");
    }

    [Fact]
    public async Task TrackPaymentAsync_PostsCorrectRequestBody()
    {
        HttpRequestMessage? captured = null;
        var svc = CreateService(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("{\"success\":true,\"data\":[{\"id\":\"tolt-tx\",\"status\":\"paid\"}]}")
            };
        });

        await svc.TrackPaymentAsync("cust-1", 1999, "subscription", "tx-1", "pro_annual", "app_store");

        captured.Should().NotBeNull();
        captured!.RequestUri!.ToString().Should().Be("https://api.tolt.test/v1/transactions");

        var body = JsonSerializer.Deserialize<JsonElement>(
            await captured.Content!.ReadAsStringAsync());
        body.GetProperty("customer_id").GetString().Should().Be("cust-1");
        body.GetProperty("amount").GetInt32().Should().Be(1999);
        body.GetProperty("billing_type").GetString().Should().Be("subscription");
        body.GetProperty("charge_id").GetString().Should().Be("tx-1");
        body.GetProperty("product_id").GetString().Should().Be("pro_annual");
        body.GetProperty("source").GetString().Should().Be("app_store");
    }

    private static StringContent JsonContent(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");

    private sealed class FakeHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
