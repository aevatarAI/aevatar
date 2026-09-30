using System.Net;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.NyxidChat.ExternalCallbacks;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdConnectLinkCreationAdapterTests
{
    [Fact]
    public async Task Create_UsesSavedOriginalBindingAndFixedIndependentCallback_AndReturnsTypedExactLink()
    {
        var handler = new Handler();
        var issuer = new NyxIdConnectLinkVerifierTests.Issuer();
        var adapter = new NyxIdConnectLinkCreationAdapter(new NyxIdConnectLinkVerifierTests.ClientFactory(handler), issuer,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T00:00:00Z")));

        var result = await adapter.CreateAsync(Registration());

        result.FailureCode.Should().BeEmpty();
        result.ExternalRequestId.Should().Be("link-created");
        result.ConnectUrl.Should().Be("https://nyx.example/connect/secret");
        result.ExpiresAtUnixMs.Should().Be(DateTimeOffset.Parse("2026-09-30T00:15:00Z").ToUnixTimeMilliseconds());
        issuer.BindingId.Should().Be("binding-original");
        handler.Authorization.Should().Be("Bearer freshly-minted-original-user");
        handler.Path.Should().Be("/api/v1/connect-links");
        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        root.GetProperty("service_slug").GetString().Should().Be("google-workspace");
        root.GetProperty("label").GetString().Should().Be("Personal Google");
        root.GetProperty("requested_by").GetString().Should().Be("assistant");
        root.GetProperty("expires_in").GetInt32().Should().Be(900);
        var callback = new Uri(root.GetProperty("callback_url").GetString()!);
        callback.AbsolutePath.Should().Be(ConnectLinkCallbackEndpoints.CallbackPath);
        callback.Query.Should().BeEmpty();
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("service_slug", "label", "requested_by", "callback_url", "expires_in");
    }

    [Fact]
    public async Task ChangedSavedBinding_FailsWithoutCreatingUnderRegistrationOwner()
    {
        var handler = new Handler();
        var adapter = new NyxIdConnectLinkCreationAdapter(new NyxIdConnectLinkVerifierTests.ClientFactory(handler),
            new NyxIdConnectLinkVerifierTests.Issuer { Changed = true }, TimeProvider.System);

        var result = await adapter.CreateAsync(Registration());

        result.FailureCode.Should().Be("original_binding_changed");
        handler.Body.Should().BeNull();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("""{"id":"link-created","connect_url":"javascript:alert(1)","expires_at":"2026-09-30T00:15:00Z"}""")]
    [InlineData("""{"id":"link-created","connect_url":"https://nyx.example/connect/secret","expires_at":"invalid"}""")]
    [InlineData("""{"id":"link-created","connect_url":"https://nyx.example/connect/secret","expires_at":"2026-09-29T00:15:00Z"}""")]
    public async Task InvalidProviderResult_NeverExposesAUsableLink(string body)
    {
        var adapter = new NyxIdConnectLinkCreationAdapter(new NyxIdConnectLinkVerifierTests.ClientFactory(new Handler { Response = body }),
            new NyxIdConnectLinkVerifierTests.Issuer(), new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T00:00:00Z")));

        var result = await adapter.CreateAsync(Registration());

        result.FailureCode.Should().Be("invalid_nyxid_connect_link_response");
        result.ConnectUrl.Should().BeEmpty();
        result.ExternalRequestId.Should().BeEmpty();
    }

    private static ExternalCallbackRegistration Registration() => new()
    {
        CallbackId = "callback-created", OperationActorId = "operation-created", Kind = ExternalCallbackKind.ConnectLink,
        RequestedCatalogServiceSlug = "google-workspace",
        ExpiresAtUnixMs = DateTimeOffset.Parse("2026-09-30T00:15:00Z").ToUnixTimeMilliseconds(),
        ConnectLinkRequest = new ConnectLinkCreationRequest { Label = "Personal Google", RequestedBy = "assistant", ExpiresInSeconds = 900 },
        Authorization = new CallbackAuthorizationReference
        {
            BindingId = "binding-original", OwnerScopeId = "user-original",
            ExternalSubject = new ExternalSubjectRef { Platform = "telegram", ExternalUserId = "sender-original" },
        },
    };

    private sealed class Handler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        public string? Path { get; private set; }
        public string Response { get; init; } = """{"id":"link-created","connect_url":"https://nyx.example/connect/secret","expires_at":"2026-09-30T00:15:00Z"}""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            Path = request.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response) };
        }
    }
}
