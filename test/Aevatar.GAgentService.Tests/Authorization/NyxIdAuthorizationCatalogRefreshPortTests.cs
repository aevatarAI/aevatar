using System.Net;
using System.Text;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgentService.Tests.Authorization;

public sealed class NyxIdAuthorizationCatalogRefreshPortTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");

    [Fact]
    public async Task RefreshPersonalAsync_ShouldObserveExactOwnerServiceAndNodeTopology()
    {
        var handler = new RouteHandler(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/api/v1/user-services"] = (HttpStatusCode.OK, """
                {"services":[
                  {"id":"usr-svc-alpha","slug":"calendar","label":"Calendar","catalog_service_id":"catalog-alpha","node_id":"node-primary","is_active":true,"credential_source":{"type":"personal"}},
                  {"id":"usr-svc-org","slug":"org-only","is_active":true,"credential_source":{"type":"org","org_id":"org-alpha","allowed":true}}
                ]}
                """),
            ["/api/v1/nodes"] = (HttpStatusCode.OK, """
                {"nodes":[
                  {"id":"node-primary","name":"Primary","owner":{"kind":"user","id":"owner-alpha"}},
                  {"id":"node-fallback","name":"Fallback","owner":{"kind":"user","id":"owner-alpha"}},
                  {"id":"node-foreign","name":"Foreign","owner":{"kind":"user","id":"other-owner"}}
                ]}
                """),
            ["/api/v1/nodes/node-primary/bindings"] = (HttpStatusCode.OK, """
                {"bindings":[{"id":"binding-primary","service_id":"catalog-alpha","priority":0,"is_active":true}]}
                """),
            ["/api/v1/nodes/node-fallback/bindings"] = (HttpStatusCode.OK, """
                {"bindings":[
                  {"id":"binding-fallback-a","service_id":"catalog-alpha","priority":1,"is_active":true},
                  {"id":"binding-fallback-b","service_id":"catalog-alpha","priority":1,"is_active":true}
                ]}
                """),
        });
        var commands = new RecordingCommandPort();

        var result = await Create(handler, commands).RefreshPersonalAsync(" owner-alpha ", "bearer-secret");

        result.Should().Be(NyxIdAuthorizationCatalogRefreshResult.Observed);
        commands.Observation.Should().NotBeNull();
        var observation = commands.Observation!;
        observation.Owner.Authority.Should().Be(NyxIdAuthorizationAuthorities.NyxId);
        observation.Owner.OwnerKind.Should().Be(AuthorizationOwnerKind.Personal);
        observation.Owner.OwnerSubject.Should().Be("owner-alpha");
        observation.ObservedAtUtc.Should().Be(Now);
        observation.FreshUntilUtc.Should().Be(Now.AddMinutes(15));
        observation.ContentDigest.Should().NotBeNullOrWhiteSpace();
        var service = observation.Services.Should().ContainSingle().Subject;
        service.UserServiceId.Should().Be("usr-svc-alpha");
        service.Access.Should().Be(NyxIdAuthorizationAccess.Permitted);
        service.NodeGrantRequirement.Should().Be(AuthorizationGrantRequirement.Required);
        service.Nodes.Select(static node => (
                node.NodeId,
                node.Role,
                node.EdgeKind,
                node.BindingId,
                node.RoutePriority))
            .Should().Equal(
                ("node-primary", NyxIdNodeRole.Primary,
                    NyxIdNodeEdgeKind.UserServicePrimary, string.Empty, 0),
                ("node-primary", NyxIdNodeRole.Primary,
                    NyxIdNodeEdgeKind.NodeBinding, "binding-primary", 0),
                ("node-fallback", NyxIdNodeRole.Fallback,
                    NyxIdNodeEdgeKind.NodeBinding, "binding-fallback-a", 1),
                ("node-fallback", NyxIdNodeRole.Fallback,
                    NyxIdNodeEdgeKind.NodeBinding, "binding-fallback-b", 1));
        handler.AuthorizationHeaders.Should().OnlyContain(static value => value == "Bearer bearer-secret");
        handler.Requests.Should().NotContain("/api/v1/nodes/node-foreign/bindings");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RefreshPersonalAsync_WhenAccessIsDenied_ShouldInvalidate(HttpStatusCode statusCode)
    {
        var commands = new RecordingCommandPort();
        var handler = new RouteHandler(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/api/v1/user-services"] = (statusCode, "{}"),
        });

        var result = await Create(handler, commands).RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.AccessDenied);
        commands.Observation.Should().BeNull();
        commands.Invalidation.Should().NotBeNull();
        commands.Invalidation!.Value.Owner.OwnerSubject.Should().Be("owner-alpha");
        commands.Invalidation.Value.Reason.Should().Be("nyxid_catalog_access_denied");
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenPublishedShapeIsIncomplete_ShouldRecordFailure()
    {
        var commands = new RecordingCommandPort();
        var handler = new RouteHandler(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/api/v1/user-services"] = (HttpStatusCode.OK, "{}"),
            ["/api/v1/nodes"] = (HttpStatusCode.OK, """{"nodes":[]}"""),
        });

        var result = await Create(handler, commands).RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Failed);
        commands.Observation.Should().BeNull();
        commands.Failure.Should().NotBeNull();
        commands.Failure!.Value.Code.Should().Be("nyxid_catalog_refresh_failed");
    }

    [Fact]
    public async Task RefreshAsync_WhenOrganizationWriteAuthorityIsNotPublished_ShouldFailClosed()
    {
        var handler = new RouteHandler(new Dictionary<string, (HttpStatusCode, string)>());
        var commands = new RecordingCommandPort();
        var owner = new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Organization,
            OwnerSubject = "org-alpha",
        };

        var result = await Create(handler, commands).RefreshAsync(owner, "org-bearer");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.OwnerNotSupported);
        result.FailureCode.Should().Be("nyxid_catalog_organization_owner_not_supported");
        commands.Observation.Should().BeNull();
        commands.Failure.Should().BeNull();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenReplicaIsNotObserved_ShouldReturnTypedTimeout()
    {
        var handler = new RouteHandler(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/api/v1/user-services"] = (HttpStatusCode.OK, """{"services":[]}"""),
            ["/api/v1/nodes"] = (HttpStatusCode.OK, """{"nodes":[]}"""),
        });
        var commands = new RecordingCommandPort();

        var result = await Create(handler, commands, observeReplica: false)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.ObservationTimedOut);
        result.FailureCode.Should().Be("nyxid_catalog_observation_timeout");
        commands.Observation.Should().NotBeNull();
        commands.Failure!.Value.Code.Should().Be("nyxid_catalog_observation_timeout");
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenTopologyChangesBetweenReads_ShouldNotPublishObservation()
    {
        var commands = new RecordingCommandPort();
        var handler = new ChangingCatalogHandler();

        var result = await Create(handler, commands)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable);
        result.FailureCode.Should().Be("nyxid_catalog_unstable");
        commands.Observation.Should().BeNull();
        commands.Failure!.Value.Code.Should().Be("nyxid_catalog_unstable");
        handler.UserServiceReadCount.Should().Be(2);
    }

    [Fact]
    public async Task RefreshPersonalAsync_ShouldNormalizeEndpointOrderByPublishedPriority()
    {
        var handler = new RouteHandler(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/api/v1/user-services"] = (HttpStatusCode.OK, """
                {"services":[{"id":"usr-svc-alpha","slug":"calendar","catalog_service_id":"catalog-alpha","is_active":true,"credential_source":{"type":"personal"}}]}
                """),
            ["/api/v1/nodes"] = (HttpStatusCode.OK, """
                {"nodes":[
                  {"id":"node-fallback","name":"Fallback","owner":{"kind":"user","id":"owner-alpha"}},
                  {"id":"node-primary","name":"Primary","owner":{"kind":"user","id":"owner-alpha"}}
                ]}
                """),
            ["/api/v1/nodes/node-primary/bindings"] = (HttpStatusCode.OK, """
                {"bindings":[{"id":"binding-primary","service_id":"catalog-alpha","priority":-5,"is_active":true}]}
                """),
            ["/api/v1/nodes/node-fallback/bindings"] = (HttpStatusCode.OK, """
                {"bindings":[{"id":"binding-fallback","service_id":"catalog-alpha","priority":5,"is_active":true}]}
                """),
        });
        var commands = new RecordingCommandPort();

        var result = await Create(handler, commands).RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Should().Be(NyxIdAuthorizationCatalogRefreshResult.Observed);
        commands.Observation!.Services.Single().Nodes
            .Select(static node => (node.NodeId, node.Role, node.RoutePriority))
            .Should().Equal(
                ("node-primary", NyxIdNodeRole.Primary, -5),
                ("node-fallback", NyxIdNodeRole.Fallback, 5));
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenDifferentNodesSharePriority_ShouldFailClosed()
    {
        var handler = new RouteHandler(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/api/v1/user-services"] = (HttpStatusCode.OK, """
                {"services":[{"id":"usr-svc-alpha","slug":"calendar","catalog_service_id":"catalog-alpha","is_active":true,"credential_source":{"type":"personal"}}]}
                """),
            ["/api/v1/nodes"] = (HttpStatusCode.OK, """
                {"nodes":[
                  {"id":"node-a","owner":{"kind":"user","id":"owner-alpha"}},
                  {"id":"node-b","owner":{"kind":"user","id":"owner-alpha"}}
                ]}
                """),
            ["/api/v1/nodes/node-a/bindings"] = (HttpStatusCode.OK, """
                {"bindings":[{"id":"binding-a","service_id":"catalog-alpha","priority":0,"is_active":true}]}
                """),
            ["/api/v1/nodes/node-b/bindings"] = (HttpStatusCode.OK, """
                {"bindings":[{"id":"binding-b","service_id":"catalog-alpha","priority":0,"is_active":true}]}
                """),
        });
        var commands = new RecordingCommandPort();

        var result = await Create(handler, commands).RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Failed);
        commands.Observation.Should().BeNull();
        commands.Failure!.Value.Code.Should().Be("nyxid_catalog_refresh_failed");
    }

    private static NyxIdAuthorizationCatalogRefreshPort Create(
        HttpMessageHandler handler,
        RecordingCommandPort commands,
        bool observeReplica = true) =>
        new(
            new SingleClientFactory(new HttpClient(handler)),
            commands,
            new RecordingCatalogQueryPort(commands, observeReplica),
            Options.Create(new NyxIdAuthorizationCatalogRefreshOptions
            {
                EndpointBaseUrl = "https://nyx.example/",
                Freshness = TimeSpan.FromMinutes(15),
                ObservationTimeout = observeReplica ? TimeSpan.FromSeconds(5) : TimeSpan.Zero,
            }),
            new FakeTimeProvider(Now),
            NullLogger<NyxIdAuthorizationCatalogRefreshPort>.Instance);

    private sealed class RecordingCatalogQueryPort(
        RecordingCommandPort commands,
        bool observeReplica) : INyxIdAuthorizationCatalogQueryPort
    {
        public Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default)
        {
            var observation = observeReplica ? commands.Observation : null;
            return Task.FromResult(observation == null
                ? null
                : new NyxIdAuthorizationCatalogSnapshot(
                    observation.Owner.Clone(),
                    1,
                    observation.ObservedAtUtc,
                    observation.FreshUntilUtc,
                    observation.ExternalRevision,
                    observation.ContentDigest,
                    observation.Services.Select(static service => service.Clone()).ToArray()));
        }
    }

    private sealed class RecordingCommandPort : INyxIdAuthorizationCatalogCommandPort
    {
        public NyxIdAuthorizationCatalogObservation? Observation { get; private set; }
        public (AuthorizationOwnerIdentity Owner, DateTimeOffset At, string Reason)? Invalidation { get; private set; }
        public (AuthorizationOwnerIdentity Owner, DateTimeOffset At, string Code)? Failure { get; private set; }

        public Task ObserveAsync(
            NyxIdAuthorizationCatalogObservation observation,
            CancellationToken ct = default)
        {
            Observation = observation;
            return Task.CompletedTask;
        }

        public Task RecordRefreshFailureAsync(
            AuthorizationOwnerIdentity owner,
            DateTimeOffset failedAtUtc,
            string failureCode,
            CancellationToken ct = default)
        {
            Failure = (owner, failedAtUtc, failureCode);
            return Task.CompletedTask;
        }

        public Task InvalidateAsync(
            AuthorizationOwnerIdentity owner,
            DateTimeOffset invalidatedAtUtc,
            string reason,
            CancellationToken ct = default)
        {
            Invalidation = (owner, invalidatedAtUtc, reason);
            return Task.CompletedTask;
        }
    }

    private sealed class RouteHandler(
        IReadOnlyDictionary<string, (HttpStatusCode Status, string Body)> routes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string> AuthorizationHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            var route = routes[path];
            return Task.FromResult(new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ChangingCatalogHandler : HttpMessageHandler
    {
        public int UserServiceReadCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/api/v1/user-services" => BuildServicesBody(++UserServiceReadCount),
                "/api/v1/nodes" => "{\"nodes\":[]}",
                _ => throw new InvalidOperationException($"Unexpected path '{path}'."),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        private static string BuildServicesBody(int read) => System.Text.Json.JsonSerializer.Serialize(new
        {
            services = new[]
            {
                new
                {
                    id = $"service-{read}",
                    slug = "calendar",
                    is_active = true,
                    credential_source = new { type = "personal" },
                },
            },
        });
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
