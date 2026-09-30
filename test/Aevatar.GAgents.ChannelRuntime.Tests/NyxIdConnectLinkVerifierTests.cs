using System.Net;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.NyxidChat.ExternalCallbacks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdConnectLinkVerifierTests
{
    private const string CompletedLink = """{"id":"link-exact","status":"completed","service_slug":"google-workspace","connected_service":{"id":"service-exact","slug":"google-workspace-7"}}""";
    private const string ActiveInventory = """{"services":[{"id":"service-exact","slug":"google-workspace-7","is_active":true,"credential_source":{"type":"personal"}}]}""";

    [Fact]
    public async Task CompletedLink_RequiresExactInstance_UsingOriginalBindingCapability()
    {
        var handler = new ScriptedHandler(CompletedLink, ActiveInventory);
        var issuer = new Issuer();
        var verifier = new NyxIdConnectLinkVerifier(new ClientFactory(handler), issuer);

        var result = await verifier.VerifyAsync(Registration());

        result.Disposition.Should().Be(CallbackVerificationDisposition.Succeeded);
        result.References.ConnectLinkId.Should().Be("link-exact");
        result.References.ConnectedServiceId.Should().Be("service-exact");
        result.References.ConnectedServiceSlug.Should().Be("google-workspace-7");
        result.References.CatalogServiceSlug.Should().Be("google-workspace");
        result.References.BindingId.Should().Be("binding-original");
        result.References.OwnerScopeId.Should().Be("user-original");
        issuer.BindingId.Should().Be("binding-original");
        issuer.Subject.Should().BeEquivalentTo(Registration().Authorization.ExternalSubject);
        handler.Paths.Should().Equal("/api/v1/connect-links/link-exact", "/api/v1/user-services");
        handler.Tokens.Should().OnlyContain(token => token == "Bearer freshly-minted-original-user");
    }

    [Theory]
    [InlineData("link-other", "google-workspace")]
    [InlineData("link-exact", "another-catalog")]
    public async Task LinkIdentityOrCatalogMismatch_Fails(string linkId, string catalogSlug)
    {
        var json = CompletedLink.Replace("link-exact", linkId, StringComparison.Ordinal)
            .Replace("\"service_slug\":\"google-workspace\"", $"\"service_slug\":\"{catalogSlug}\"", StringComparison.Ordinal);
        var handler = new ScriptedHandler(json);

        var result = await new NyxIdConnectLinkVerifier(new ClientFactory(handler), new Issuer()).VerifyAsync(Registration());

        result.Disposition.Should().Be(CallbackVerificationDisposition.Failed);
        result.References.Should().BeNull();
        handler.Paths.Should().ContainSingle();
    }

    [Theory]
    [InlineData("""{"services":[{"id":"another-id","slug":"google-workspace-7","is_active":true,"credential_source":{"type":"personal"}}]}""")]
    [InlineData("""{"services":[{"id":"service-exact","slug":"another-slug","is_active":true,"credential_source":{"type":"personal"}}]}""")]
    [InlineData("""{"services":[{"id":"service-exact","slug":"google-workspace-7","is_active":false,"credential_source":{"type":"personal"}}]}""")]
    [InlineData("""{"services":[{"id":"service-exact","slug":"google-workspace-7","is_active":true,"credential_source":{"type":"org","org_id":"owner-other","allowed":true}}]}""")]
    [InlineData("""{"services":[{"id":"service-exact","slug":"google-workspace-7","is_active":true}]}""")]
    public async Task CompletedLink_WithoutActiveExactPersonallyOwnedInstance_NeverSucceeds(string inventory)
    {
        var result = await new NyxIdConnectLinkVerifier(new ClientFactory(new ScriptedHandler(CompletedLink, inventory)), new Issuer())
            .VerifyAsync(Registration());

        result.Disposition.Should().NotBe(CallbackVerificationDisposition.Succeeded);
        result.References.Should().BeNull();
    }

    [Theory]
    [InlineData("pending", CallbackVerificationDisposition.Pending)]
    [InlineData("cancelled", CallbackVerificationDisposition.Cancelled)]
    [InlineData("expired", CallbackVerificationDisposition.Expired)]
    [InlineData("future-status", CallbackVerificationDisposition.Unavailable)]
    public async Task ExactLinkStatus_MapsOnlyAuthoritativeResponse(string status, CallbackVerificationDisposition disposition)
    {
        var handler = new ScriptedHandler($$"""{"id":"link-exact","status":"{{status}}","service_slug":"google-workspace"}""");
        var result = await new NyxIdConnectLinkVerifier(new ClientFactory(handler), new Issuer()).VerifyAsync(Registration());

        result.Disposition.Should().Be(disposition);
        handler.Paths.Should().ContainSingle();
    }

    [Fact]
    public async Task TemporaryProviderFailure_IsTypedUnavailableForActorRetry()
    {
        var handler = new ScriptedHandler("{\"error\":\"unavailable\"}") { StatusCode = HttpStatusCode.ServiceUnavailable };
        var result = await new NyxIdConnectLinkVerifier(new ClientFactory(handler), new Issuer()).VerifyAsync(Registration());

        result.Disposition.Should().Be(CallbackVerificationDisposition.Unavailable);
        result.References.Should().BeNull();
    }

    [Fact]
    public async Task ChangedOriginalBinding_FailsWithoutUsingAnyAlternativeCredential()
    {
        var handler = new ScriptedHandler(CompletedLink);
        var result = await new NyxIdConnectLinkVerifier(new ClientFactory(handler), new Issuer { Changed = true })
            .VerifyAsync(Registration());

        result.Disposition.Should().Be(CallbackVerificationDisposition.Failed);
        handler.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_PropagatesForRuntimeHandling()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var verifier = new NyxIdConnectLinkVerifier(new ClientFactory(new ScriptedHandler(CompletedLink)), new Issuer());

        await ((Func<Task>)(() => verifier.VerifyAsync(Registration(), cts.Token))).Should().ThrowAsync<OperationCanceledException>();
    }

    private static ExternalCallbackRegistration Registration() => new()
    {
        CallbackId = "callback-original", Kind = ExternalCallbackKind.ConnectLink,
        ExternalRequestId = "link-exact", RequestedCatalogServiceSlug = "google-workspace",
        Authorization = new CallbackAuthorizationReference
        {
            BindingId = "binding-original", OwnerScopeId = "user-original",
            ExternalSubject = new ExternalSubjectRef { Platform = "telegram", Tenant = "tenant-original", ExternalUserId = "sender-original" },
        },
    };

    internal sealed class Issuer : INyxIdConnectedServiceCapabilityIssuer
    {
        public bool Changed { get; init; }
        public string? BindingId { get; private set; }
        public ExternalSubjectRef? Subject { get; private set; }
        public Task<CapabilityHandle> IssueByBindingIdAsync(ExternalSubjectRef externalSubject, string bindingId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Changed) throw new BindingChangedException(externalSubject);
            BindingId = bindingId;
            Subject = externalSubject.Clone();
            return Task.FromResult(new CapabilityHandle { AccessToken = "freshly-minted-original-user" });
        }
    }

    internal sealed class ClientFactory(HttpMessageHandler handler) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => new(new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler, disposeHandler: false), NullLogger<NyxIdApiClient>.Instance);
    }

    internal sealed class ScriptedHandler(params string[] responses) : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public List<string> Paths { get; } = [];
        public List<string?> Tokens { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Tokens.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(StatusCode) { Content = new StringContent(responses[Paths.Count - 1]) });
        }
    }
}
