using System.Net;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.NyxidChat.ExternalCallbacks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdConnectLinkCapabilityPathTests
{
    [Fact]
    public async Task Verifier_RemintsThroughExistingBrokerTokenExchange_AndUsesThatTokenForBothExactReads()
    {
        var subject = new ExternalSubjectRef { Platform = "telegram", ExternalUserId = "sender-original" };
        var query = Substitute.For<IExternalIdentityBindingQueryPort>();
        query.ResolveAsync(Arg.Is<ExternalSubjectRef>(s => s.Equals(subject)), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(new BindingId { Value = "binding-original" }));
        var provider = Substitute.For<IAevatarOAuthClientProvider>();
        provider.GetAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new AevatarOAuthClientSnapshot(
            "existing-configured-client", DateTimeOffset.UnixEpoch, "key-version", new byte[32], DateTimeOffset.UnixEpoch,
            "https://nyx.example", true, DateTimeOffset.UnixEpoch)));
        var handler = new ContractHandler();
        var factory = new HttpFactory(handler);
        var broker = new NyxIdRemoteCapabilityBroker(factory, provider, Options.Create(new NyxIdBrokerOptions()),
            new StateTokenCodec(provider), query, TimeProvider.System, NullLogger<NyxIdRemoteCapabilityBroker>.Instance);
        var verifier = new NyxIdConnectLinkVerifier(new NyxIdConnectLinkVerifierTests.ClientFactory(handler), broker);

        var result = await verifier.VerifyAsync(new ExternalCallbackRegistration
        {
            Kind = ExternalCallbackKind.ConnectLink, ExternalRequestId = "link-exact", RequestedCatalogServiceSlug = "google-workspace",
            Authorization = new CallbackAuthorizationReference { ExternalSubject = subject, BindingId = "binding-original", OwnerScopeId = "user-original" },
        });

        result.Disposition.Should().Be(CallbackVerificationDisposition.Succeeded);
        handler.Paths.Should().Equal("/oauth/token", "/api/v1/connect-links/link-exact", "/api/v1/user-services");
        handler.TokenExchangeBody.Should().Contain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Atoken-exchange");
        handler.TokenExchangeBody.Should().Contain("subject_token=binding-original");
        handler.TokenExchangeBody.Should().Contain("client_id=existing-configured-client");
        handler.TokenExchangeBody.Should().Contain("scope=proxy");
        handler.ReadAuthorizations.Should().Equal("Bearer minted-binding-token", "Bearer minted-binding-token");
        await query.Received(1).ResolveAsync(Arg.Is<ExternalSubjectRef>(s => s.Equals(subject)), Arg.Any<CancellationToken>());
    }

    private sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ContractHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string?> ReadAuthorizations { get; } = [];
        public string? TokenExchangeBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            string body;
            if (path == "/oauth/token")
            {
                TokenExchangeBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                body = """{"access_token":"minted-binding-token","token_type":"Bearer","scope":"proxy","expires_in":300}""";
            }
            else
            {
                ReadAuthorizations.Add(request.Headers.Authorization?.ToString());
                body = path == "/api/v1/connect-links/link-exact"
                    ? """{"id":"link-exact","service_slug":"google-workspace","status":"completed","connected_service":{"id":"instance-exact","slug":"google-workspace-7"}}"""
                    : """{"services":[{"id":"instance-exact","slug":"google-workspace-7","is_active":true,"credential_source":{"type":"personal"}}]}""";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
