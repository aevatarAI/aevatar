using System.Net;
using System.Text;
using Aevatar.Studio.Application.Authorization;
using Aevatar.Studio.Hosting.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class NyxIdCatalogRefreshLifecycleTests
{
    [Fact]
    public async Task AccessInvalidation_ShouldForwardOnlyNyxIdNativeOwner()
    {
        var commands = new RecordingCommandPort();
        var configuration = Configuration();
        var port = new NyxIdCatalogAccessLifecyclePort(
            commands, configuration, new FixedTimeProvider(DateTimeOffset.Parse("2026-07-15T00:00:00Z")));

        await port.InvalidateAsync(new ExternalSubjectRef
        {
            Platform = "lark",
            ExternalUserId = "sender-alpha",
        }, "unbind");
        commands.InvalidatedOwner.Should().BeNull();

        await port.InvalidateAsync(new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            ExternalUserId = "nyx-owner-alpha",
        }, "access_lost");

        commands.InvalidatedOwner!.OwnerSubject.Should().Be("nyx-owner-alpha");
        commands.InvalidationReason.Should().Be("access_lost");
    }

    [Fact]
    public async Task RefreshPersonalAsync_ShouldObserveExactServiceAndPrimaryFallbackNodes()
    {
        var handler = new RouteHandler(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/api/v1/user-services"] = (HttpStatusCode.OK, """
                {"services":[
                  {"id":"usr-svc-alpha","slug":"calendar","label":"Calendar","catalog_service_id":"catalog-alpha","node_id":"node-primary","is_active":true,"credential_source":{"type":"personal"}},
                  {"id":"usr-svc-org","slug":"org-only","is_active":true,"credential_source":{"type":"org","org_id":"org-alpha"}}
                ]}
                """),
            ["/api/v1/nodes"] = (HttpStatusCode.OK, """{"nodes":[{"id":"node-primary"},{"id":"node-fallback"}]}"""),
            ["/api/v1/nodes/node-primary/bindings"] = (HttpStatusCode.OK, """{"bindings":[{"service_id":"catalog-alpha","priority":0,"is_active":true}]}"""),
            ["/api/v1/nodes/node-fallback/bindings"] = (HttpStatusCode.OK, """{"bindings":[{"service_id":"catalog-alpha","priority":1,"is_active":true}]}"""),
        });
        var commands = new RecordingCommandPort();
        var lifecycle = Create(handler, commands);

        await lifecycle.RefreshPersonalAsync("nyx-owner-alpha", "bearer-secret");

        commands.Observation.Should().NotBeNull();
        commands.Observation!.Owner.OwnerSubject.Should().Be("nyx-owner-alpha");
        commands.Observation.Owner.Authority.Should().Be("https://nyx.example");
        var service = commands.Observation.Services.Should().ContainSingle().Subject;
        service.UserServiceId.Should().Be("usr-svc-alpha");
        service.NodeGrants.Select(static node => node.NodeId).Should().Equal("node-primary", "node-fallback");
        service.NodeGrants[0].Primary.Should().BeTrue();
        handler.AuthorizationHeaders.Should().OnlyContain(static value => value == "Bearer bearer-secret");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RefreshPersonalAsync_WhenAccessDenied_ShouldInvalidate(HttpStatusCode statusCode)
    {
        var commands = new RecordingCommandPort();
        var lifecycle = Create(new RouteHandler(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/api/v1/user-services"] = (statusCode, "{}"),
        }), commands);

        await lifecycle.RefreshPersonalAsync("nyx-owner-alpha", "bearer-secret");

        commands.InvalidatedOwner!.OwnerSubject.Should().Be("nyx-owner-alpha");
        commands.InvalidationReason.Should().Be("nyxid_catalog_access_denied");
        commands.Observation.Should().BeNull();
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenProviderResponseIsInvalid_ShouldRecordFailure()
    {
        var commands = new RecordingCommandPort();
        var lifecycle = Create(new RouteHandler(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/api/v1/user-services"] = (HttpStatusCode.OK, "{}"),
            ["/api/v1/nodes"] = (HttpStatusCode.OK, "{\"nodes\":[]}"),
        }), commands);

        await lifecycle.RefreshPersonalAsync("nyx-owner-alpha", "bearer-secret");

        commands.FailureCode.Should().Be("nyxid_catalog_refresh_failed");
        commands.Observation.Should().BeNull();
    }

    private static NyxIdCatalogRefreshLifecycle Create(RouteHandler handler, RecordingCommandPort commands) =>
        new(
            new SingleClientFactory(new HttpClient(handler)),
            commands,
            Configuration(),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-15T00:00:00Z")),
            NullLogger<NyxIdCatalogRefreshLifecycle>.Instance);

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aevatar:NyxId:Authority"] = "https://nyx.example/",
        }).Build();

    private sealed class RecordingCommandPort : INyxIdCatalogSnapshotCommandPort
    {
        public NyxIdCatalogObservation? Observation { get; private set; }
        public NyxIdCatalogOwnerIdentity? InvalidatedOwner { get; private set; }
        public string? InvalidationReason { get; private set; }
        public string? FailureCode { get; private set; }
        public Task ObserveAsync(NyxIdCatalogObservation observation, CancellationToken ct = default)
        {
            Observation = observation;
            return Task.CompletedTask;
        }
        public Task RecordRefreshFailureAsync(NyxIdCatalogOwnerIdentity owner, DateTimeOffset failedAtUtc, string failureCode, CancellationToken ct = default)
        {
            FailureCode = failureCode;
            return Task.CompletedTask;
        }
        public Task InvalidateAsync(NyxIdCatalogOwnerIdentity owner, DateTimeOffset invalidatedAtUtc, string reason, CancellationToken ct = default)
        {
            InvalidatedOwner = owner;
            InvalidationReason = reason;
            return Task.CompletedTask;
        }
    }

    private sealed class RouteHandler(IReadOnlyDictionary<string, (HttpStatusCode Status, string Body)> routes) : HttpMessageHandler
    {
        public List<string> AuthorizationHeaders { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            var route = routes[request.RequestUri!.AbsolutePath];
            return Task.FromResult(new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
