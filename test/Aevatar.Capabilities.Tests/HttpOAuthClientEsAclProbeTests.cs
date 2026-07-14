using System.Net;
using System.Text;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.Mainnet.Host.Api.Hosting;
using FluentAssertions;

namespace Aevatar.Capabilities.Tests;

public sealed class HttpOAuthClientEsAclProbeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProbeAsync_WhenHasPrivilegesSucceeds_ShouldRemainUnverifiable(bool hasRead)
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(
                $"{{\"index\":{{\"aevatar-oauth-clients\":{{\"read\":{hasRead.ToString().ToLowerInvariant()}}}}}}}"),
        });
        using var probe = CreateProbe(handler);

        var result = await probe.ProbeAsync();

        result.Status.Should().Be(EsAclProbeStatus.Unverifiable);
        result.ObservedState.Should().Contain("does not prove");
        handler.Requests.Should().ContainSingle()
            .Which.RequestUri!.AbsolutePath.Should().Be("/_security/user/_has_privileges");
    }

    [Fact]
    public async Task ProbeAsync_WhenSecurityIsDisabled_ShouldReportUnrestricted()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = Json("{\"error\":\"security is not enabled\"}"),
        });
        using var probe = CreateProbe(handler);

        var result = await probe.ProbeAsync();

        result.Status.Should().Be(EsAclProbeStatus.Unrestricted);
    }

    private static HttpOAuthClientEsAclProbe CreateProbe(HttpMessageHandler handler) => new(
        new ElasticsearchProjectionDocumentStoreOptions
        {
            Endpoints = ["https://elasticsearch.example.com"],
            RequestTimeoutMs = 1_000,
        },
        httpMessageHandler: handler);

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(response);
        }
    }
}
