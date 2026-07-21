using System.Net;
using System.Net.Http;
using System.Text;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgentService.Tests.Authorization;

public sealed class NyxIdAuthorizationCatalogRefreshPortTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");

    [Fact]
    public async Task RefreshPersonalAsync_WhenNyxIdKeysContainServices_ShouldObserveCatalog()
    {
        var commands = new RecordingCommandPort();
        var handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            """
            {
              "keys": [
                {
                  "id": "svc-alpha",
                  "slug": "llm-openai",
                  "catalog_service_slug": "llm-openai",
                  "catalog_service_name": "OpenAI",
                  "status": "active",
                  "is_active": true
                },
                {
                  "id": "svc-node",
                  "slug": "routeros",
                  "catalog_service_slug": "routeros",
                  "catalog_service_name": "RouterOS",
                  "status": "ready",
                  "is_active": true,
                  "node_id": "node-alpha",
                  "node_priority": 2
                }
              ]
            }
            """);
        var port = Create(
            commands,
            handler,
            new FixedCatalogQueryPort(Snapshot(lifecycleFence: 42)));

        var result = await port.RefreshPersonalAsync(" owner-alpha ", " bearer-secret ");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Observed);
        result.FailureCode.Should().BeEmpty();
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri.Should().Be(new Uri("https://nyx.example/api/v1/keys"));
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("bearer-secret");
        commands.Activations.Should().ContainSingle();
        commands.Activations[0].Owner.Should().BeEquivalentTo(Owner());
        commands.Activations[0].At.Should().Be(Now);
        commands.Observations.Should().ContainSingle();
        commands.Failures.Should().BeEmpty();
        commands.Invalidations.Should().BeEmpty();

        var observation = commands.Observations[0];
        observation.Owner.Should().BeEquivalentTo(Owner());
        observation.ObservedAtUtc.Should().Be(Now);
        observation.FreshUntilUtc.Should().Be(Now.AddMinutes(15));
        observation.ExpectedLifecycleFence.Should().Be(42);
        observation.Services.Should().HaveCount(2);
        observation.ContentDigest.Should().Be(
            NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(observation.Owner, observation.Services));
        observation.ExternalRevision.Should().Be(observation.ContentDigest);

        var direct = observation.Services.Single(service => service.UserServiceId == "svc-alpha");
        direct.ServiceSlug.Should().Be("llm-openai");
        direct.DisplayName.Should().Be("OpenAI");
        direct.Access.Should().Be(NyxIdAuthorizationAccess.Permitted);
        direct.NodeGrantRequirement.Should().Be(AuthorizationGrantRequirement.NotRequired);
        direct.Nodes.Should().BeEmpty();

        var nodeBacked = observation.Services.Single(service => service.UserServiceId == "svc-node");
        nodeBacked.ServiceSlug.Should().Be("routeros");
        nodeBacked.DisplayName.Should().Be("RouterOS");
        nodeBacked.Access.Should().Be(NyxIdAuthorizationAccess.Permitted);
        nodeBacked.NodeGrantRequirement.Should().Be(AuthorizationGrantRequirement.Required);
        nodeBacked.Nodes.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new NyxIdAuthorizationNodeEvidence
            {
                NodeId = "node-alpha",
                DisplayName = "node-alpha",
                Role = NyxIdNodeRole.Primary,
                EdgeKind = NyxIdNodeEdgeKind.UserServicePrimary,
                RoutePriority = 2,
            });
    }

    [Fact]
    public async Task RefreshAsync_WhenNyxIdReturnsUnauthorized_ShouldRecordAccessDenied()
    {
        var commands = new RecordingCommandPort();
        var handler = new RecordingHttpMessageHandler(HttpStatusCode.Unauthorized, "{\"error\":\"expired\"}");
        var result = await Create(commands, handler).RefreshAsync(Owner(), "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.AccessDenied);
        result.FailureCode.Should().Be(NyxIdAuthorizationCatalogRefreshPort.AccessDeniedFailureCode);
        commands.Activations.Should().ContainSingle();
        commands.Failures.Should().ContainSingle();
        commands.Failures[0].Owner.Should().BeEquivalentTo(Owner());
        commands.Failures[0].At.Should().Be(Now);
        commands.Failures[0].Code.Should().Be(NyxIdAuthorizationCatalogRefreshPort.AccessDeniedFailureCode);
        commands.Observations.Should().BeEmpty();
        commands.Invalidations.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_WhenOrganizationOwnerContractIsNotPublished_ShouldFailWithoutLifecycleMutation()
    {
        var commands = new RecordingCommandPort();
        var owner = Owner();
        owner.OwnerKind = AuthorizationOwnerKind.Organization;
        owner.OwnerSubject = "org-alpha";

        var result = await Create(commands).RefreshAsync(owner, "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.OwnerNotSupported);
        result.FailureCode.Should().Be("nyxid_catalog_organization_owner_not_supported");
        commands.AllCalls.Should().Be(0);
    }

    [Fact]
    public async Task RefreshAsync_WhenAuthorityIsNotNyxId_ShouldFailWithoutLifecycleMutation()
    {
        var commands = new RecordingCommandPort();
        var owner = Owner();
        owner.Authority = "other-authority";

        var act = () => Create(commands).RefreshAsync(owner, "bearer-secret");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*owner authority is not supported*");
        commands.AllCalls.Should().Be(0);
    }

    private static NyxIdAuthorizationCatalogRefreshPort Create(
        RecordingCommandPort commands,
        RecordingHttpMessageHandler? handler = null,
        INyxIdAuthorizationCatalogQueryPort? queryPort = null) => new(
        commands,
        new RecordingHttpClientFactory(handler ?? new RecordingHttpMessageHandler(HttpStatusCode.OK, "{\"keys\":[]}")),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:Authority"] = "https://nyx.example",
            })
            .Build(),
        new FakeTimeProvider(Now),
        NullLogger<NyxIdAuthorizationCatalogRefreshPort>.Instance,
        queryPort);

    private static AuthorizationOwnerIdentity Owner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = "owner-alpha",
    };

    private static NyxIdAuthorizationCatalogSnapshot Snapshot(long lifecycleFence) => new(
        Owner(),
        StateVersion: 7,
        ObservedAtUtc: Now.AddMinutes(-5),
        FreshUntilUtc: Now.AddMinutes(10),
        ExternalRevision: "revision-alpha",
        ContentDigest: "digest-alpha",
        Services: [],
        LifecycleFence: lifecycleFence);

    private sealed class RecordingHttpMessageHandler(
        HttpStatusCode statusCode,
        string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RecordingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedCatalogQueryPort(NyxIdAuthorizationCatalogSnapshot? snapshot)
        : INyxIdAuthorizationCatalogQueryPort
    {
        public Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class RecordingCommandPort : INyxIdAuthorizationCatalogCommandPort
    {
        public List<(AuthorizationOwnerIdentity Owner, DateTimeOffset At)> Activations { get; } = [];
        public List<NyxIdAuthorizationCatalogObservation> Observations { get; } = [];
        public List<(AuthorizationOwnerIdentity Owner, DateTimeOffset At, string Code)> Failures { get; } = [];
        public List<(AuthorizationOwnerIdentity Owner, DateTimeOffset At, string Reason)> Invalidations { get; } = [];
        public List<(AuthorizationOwnerIdentity Owner, DateTimeOffset At, string Reason)> Cleanups { get; } = [];

        public int AllCalls => Activations.Count + Observations.Count + Failures.Count +
                               Invalidations.Count + Cleanups.Count;

        public Task ActivateAsync(
            AuthorizationOwnerIdentity owner,
            DateTimeOffset activatedAtUtc,
            CancellationToken ct = default)
        {
            Activations.Add((owner.Clone(), activatedAtUtc));
            return Task.CompletedTask;
        }

        public Task ObserveAsync(
            NyxIdAuthorizationCatalogObservation observation,
            CancellationToken ct = default)
        {
            Observations.Add(observation);
            return Task.CompletedTask;
        }

        public Task RecordRefreshFailureAsync(
            AuthorizationOwnerIdentity owner,
            DateTimeOffset failedAtUtc,
            string failureCode,
            CancellationToken ct = default)
        {
            Failures.Add((owner.Clone(), failedAtUtc, failureCode));
            return Task.CompletedTask;
        }

        public Task InvalidateAsync(
            AuthorizationOwnerIdentity owner,
            DateTimeOffset invalidatedAtUtc,
            string reason,
            CancellationToken ct = default)
        {
            Invalidations.Add((owner.Clone(), invalidatedAtUtc, reason));
            return Task.CompletedTask;
        }

        public Task CleanupAsync(
            AuthorizationOwnerIdentity owner,
            DateTimeOffset cleanedAtUtc,
            string reason,
            CancellationToken ct = default)
        {
            Cleanups.Add((owner.Clone(), cleanedAtUtc, reason));
            return Task.CompletedTask;
        }
    }
}
