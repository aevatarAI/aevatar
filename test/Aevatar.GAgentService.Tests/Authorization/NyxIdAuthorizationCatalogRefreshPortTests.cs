using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgentService.Tests.Authorization;

public sealed class NyxIdAuthorizationCatalogRefreshPortTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-21T09:00:00Z");
    private static readonly DateTimeOffset EvaluatedAt = DateTimeOffset.Parse("2026-07-21T08:59:59Z");

    [Fact]
    public async Task RefreshPersonalAsync_ShouldNotTreatDispatchAdmissionAsCommittedBegin()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJson()));
        var observation = new RecordingObservationRuntime();
        using var cancellation = new CancellationTokenSource();

        var refresh = Create(
                commands,
                handler,
                publishCommittedOutcomes: false,
                observation)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret", cancellation.Token);

        commands.Beginnings.Should().ContainSingle();
        refresh.IsCompleted.Should().BeFalse();
        handler.Requests.Should().BeEmpty();

        cancellation.Cancel();
        var act = () => refresh;
        await act.Should().ThrowAsync<OperationCanceledException>();
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_ShouldNotTreatTerminalDispatchAdmissionAsCompletion()
    {
        var commands = new RecordingCommandPort { PublishTerminalOutcomes = false };
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJson()));
        using var cancellation = new CancellationTokenSource();

        var refresh = Create(commands, handler)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret", cancellation.Token);

        commands.Observations.Should().ContainSingle();
        refresh.IsCompleted.Should().BeFalse();
        handler.Requests.Should().HaveCount(2);

        cancellation.Cancel();
        var act = () => refresh;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenCommittedBeginIsNotObserved_ShouldReturnObservationTimedOut()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJson()));
        var observation = new RecordingObservationRuntime();
        var clock = new FakeTimeProvider(Now);

        var refresh = Create(
                commands,
                handler,
                publishCommittedOutcomes: false,
                observation,
                clock)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        clock.Advance(NyxIdAuthorizationCatalogRefreshPort.CatalogObservationTimeout);
        var result = await refresh;

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.ObservationTimedOut);
        result.FailureCode.Should().Be("nyxid_catalog_refresh_observation_timed_out");
        handler.Requests.Should().BeEmpty();
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenCommittedBeginIsSuperseded_ShouldSkipProviderCalls()
    {
        var commands = new RecordingCommandPort
        {
            BeginOutcomeStatus = NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded,
        };
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJson()));

        var result = await Create(commands, handler)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Superseded);
        result.FailureCode.Should().Be("nyxid_catalog_refresh_superseded");
        handler.Requests.Should().BeEmpty();
        commands.Observations.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenDetachFails_ShouldStillReleaseBothScopes()
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime
        {
            DetachFailure = new InvalidOperationException("detach-failure"),
        };

        var act = () => Create(
                commands,
                new RoutingJsonHandler(Ok(UserServicesJson()), Ok(ScopePlanJson())),
                observation: observation)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("detach-failure");
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenProjectionReleaseFails_ShouldStillReleasePreparation()
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime
        {
            ProjectionReleaseFailure = new InvalidOperationException("projection-release-failure"),
        };

        var act = () => Create(
                commands,
                new RoutingJsonHandler(Ok(UserServicesJson()), Ok(ScopePlanJson())),
                observation: observation)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("projection-release-failure");
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_ShouldObservePublishedScopePlanForActiveAllowedServices()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJson()));

        var result = await Create(commands, handler)
            .RefreshPersonalAsync(" owner-alpha ", "bearer-secret");

        result.Should().Be(NyxIdAuthorizationCatalogRefreshResult.Observed);
        handler.Requests.Select(static request => (request.Method, request.Path))
            .Should().Equal(
                (HttpMethod.Get, "/api/v1/user-services"),
                (HttpMethod.Post, "/api/v1/api-keys/scope-plan"));
        handler.AuthorizationHeaders.Should().OnlyContain(static value => value == "Bearer bearer-secret");
        using (var request = JsonDocument.Parse(handler.RequestBodies.Single()))
        {
            request.RootElement.GetProperty("selected_service_ids")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .Should().Equal("service-a", "service-b");
            request.RootElement.TryGetProperty("target_org_id", out _).Should().BeFalse();
        }

        commands.Activations.Should().ContainSingle();
        commands.Beginnings.Should().ContainSingle();
        commands.Beginnings[0].Owner.Should().BeEquivalentTo(Owner());
        commands.Beginnings[0].At.Should().Be(Now);
        commands.Beginnings[0].RefreshId.Should().NotBeNullOrWhiteSpace();
        var observation = commands.Observations.Should().ContainSingle().Subject;
        observation.Owner.Should().BeEquivalentTo(Owner());
        observation.RefreshId.Should().Be(commands.Beginnings[0].RefreshId);
        observation.ObservedAtUtc.Should().Be(Now);
        observation.FreshUntilUtc.Should().Be(Now.AddMinutes(15));
        observation.ContractVersion.Should().Be("1");
        observation.PolicyVersion.Should().Be("api-key-scope-v1");
        observation.EvaluatedAtUtc.Should().Be(EvaluatedAt);
        observation.ContentDigest.Should().Be(
            NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(observation.Owner, observation.Services));
        observation.Services.Select(static service => service.UserServiceId)
            .Should().Equal("service-a", "service-b");

        var personal = observation.Services[0];
        personal.ServiceSlug.Should().Be("api-alpha");
        personal.DisplayName.Should().Be("Alpha");
        personal.Access.Should().Be(NyxIdAuthorizationAccess.Permitted);
        personal.ResourceOwner.Should().BeEquivalentTo(Owner());
        personal.NodeGrantRequirement.Should().Be(AuthorizationGrantRequirement.NotRequired);
        personal.NodeIds.Should().BeEmpty();

        var organization = observation.Services[1];
        organization.ServiceSlug.Should().Be("api-beta");
        organization.DisplayName.Should().Be("Beta Catalog");
        organization.ResourceOwner.Should().BeEquivalentTo(new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Organization,
            OwnerSubject = "org-alpha",
        });
        organization.NodeGrantRequirement.Should().Be(AuthorizationGrantRequirement.Required);
        organization.NodeIds.Should().Equal("node-a", "node-b");
        commands.Invalidations.Should().BeEmpty();
        commands.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenScopePlanIsForbidden_ShouldInvalidateCatalog()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Error(HttpStatusCode.Forbidden, "api_key_scope_plan_denied", 9004));

        var result = await Create(commands, handler)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.AccessDenied);
        result.FailureCode.Should().Be("api_key_scope_plan_denied");
        commands.Beginnings.Should().ContainSingle();
        commands.Invalidations.Should().ContainSingle();
        commands.Invalidations[0].RefreshId.Should().Be(commands.Beginnings[0].RefreshId);
        commands.Invalidations[0].Reason.Should().Be("api_key_scope_plan_denied");
        commands.Invalidations[0].OutcomeStatus.Should()
            .Be(NyxIdAuthorizationCatalogRefreshOutcomeStatus.AccessDenied);
        commands.Observations.Should().BeEmpty();
        commands.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenScopePlanProviderIsUnavailable_ShouldRecordRefreshFailure()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Error(HttpStatusCode.ServiceUnavailable, "internal_error", 1006));

        var result = await Create(commands, handler)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Failed);
        result.FailureCode.Should().Be("internal_error");
        commands.Failures.Should().ContainSingle();
        commands.Failures[0].RefreshId.Should().Be(commands.Beginnings[0].RefreshId);
        commands.Failures[0].Code.Should().Be("internal_error");
        commands.Invalidations.Should().BeEmpty();
        commands.Observations.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenScopePlanResponseIsMalformed_ShouldInvalidateAsUnstable()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok("{}"));

        var result = await Create(commands, handler)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable);
        result.FailureCode.Should().Be("nyxid_scope_plan_response_malformed");
        commands.Invalidations.Should().ContainSingle();
        commands.Invalidations[0].RefreshId.Should().Be(commands.Beginnings[0].RefreshId);
        commands.Invalidations[0].Reason.Should().Be("nyxid_scope_plan_response_malformed");
        commands.Invalidations[0].OutcomeStatus.Should()
            .Be(NyxIdAuthorizationCatalogRefreshOutcomeStatus.CatalogUnstable);
        commands.Observations.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_WhenOrganizationOwnerIsNotActorScoped_ShouldFailWithoutLifecycleMutation()
    {
        var commands = new RecordingCommandPort();
        var owner = Owner();
        owner.OwnerKind = AuthorizationOwnerKind.Organization;
        owner.OwnerSubject = "org-alpha";

        var result = await Create(commands, new RoutingJsonHandler(Ok("{}")))
            .RefreshAsync(owner, "bearer-secret");

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

        var act = () => Create(commands, new RoutingJsonHandler(Ok("{}")))
            .RefreshAsync(owner, "bearer-secret");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*owner authority is not supported*");
        commands.AllCalls.Should().Be(0);
    }

    private static NyxIdAuthorizationCatalogRefreshPort Create(
        RecordingCommandPort commands,
        RoutingJsonHandler handler,
        bool publishCommittedOutcomes = true,
        RecordingObservationRuntime? observation = null,
        TimeProvider? timeProvider = null)
    {
        observation ??= new RecordingObservationRuntime();
        commands.Observation = publishCommittedOutcomes ? observation : null;
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example") });
        return new NyxIdAuthorizationCatalogRefreshPort(
            commands,
            new TestNyxIdApiClientFactory(client),
            observation,
            observation,
            timeProvider ?? new FakeTimeProvider(Now),
            NullLogger<NyxIdAuthorizationCatalogRefreshPort>.Instance);
    }

    private static AuthorizationOwnerIdentity Owner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = "owner-alpha",
    };

    private static string UserServicesJson() => """
        {
          "services": [
            {"id":"service-b","slug":"api-beta","catalog_service_name":"Beta Catalog","is_active":true,
             "credential_source":{"type":"org","org_id":"org-alpha","org_name":"Alpha","role":"admin","allowed":true}},
            {"id":"service-inactive","slug":"api-inactive","label":"Inactive","is_active":false,
             "credential_source":{"type":"personal"}},
            {"id":"service-a","slug":"api-alpha","label":"Alpha","is_active":true,
             "credential_source":{"type":"personal"}},
            {"id":"service-denied","slug":"api-denied","label":"Denied","is_active":true,
             "credential_source":{"type":"org","org_id":"org-beta","org_name":"Beta","role":"viewer","allowed":false}}
          ]
        }
        """;

    private static string ScopePlanJson() => $$$"""
        {
          "authority":"nyxid",
          "contract_version":"1",
          "policy_version":"api-key-scope-v1",
          "authenticated_actor":{"id":"owner-alpha","type":"personal"},
          "intended_key_owner":{"id":"owner-alpha","type":"personal"},
          "services":[
            {"user_service_id":"service-a","resource_owner":{"id":"owner-alpha","type":"personal"},"node_grant":{"type":"not_required"}},
            {"user_service_id":"service-b","resource_owner":{"id":"org-alpha","type":"organization"},"node_grant":{"type":"required","node_ids":["node-a","node-b"]}}
          ],
          "allowed_service_ids":["service-a","service-b"],
          "allowed_node_ids":["node-a","node-b"],
          "evaluated_at":"{{{EvaluatedAt:O}}}",
          "normalized_grant_digest":"sha256:{{{new string('a', 64)}}}",
          "freshness":{"mode":"mutation_revalidated_snapshot","precondition_field":"scope_plan_digest","post_creation_drift":"fail_closed"},
          "completeness":{"list_complete":true,"no_duplicates":true,"route_candidate_basis":"active_configured_routes","transient_node_state_excluded":true}
        }
        """;

    private static QueuedResponse Ok(string body) => new(HttpStatusCode.OK, body);

    private static QueuedResponse Error(HttpStatusCode status, string code, int errorCode) =>
        new(status, JsonSerializer.Serialize(new
        {
            error = code,
            error_code = errorCode,
            message = "sensitive provider detail",
        }));

    private sealed class TestNyxIdApiClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    private sealed class RoutingJsonHandler(params QueuedResponse[] responses) : HttpMessageHandler
    {
        private readonly Queue<QueuedResponse> _responses = new(responses);

        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        public List<string> AuthorizationHeaders { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri?.PathAndQuery ?? string.Empty));
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            if (request.Content != null)
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            if (!_responses.TryDequeue(out var response))
                throw new InvalidOperationException("No queued response remains.");
            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record QueuedResponse(HttpStatusCode Status, string Body);

    private sealed class RecordingCommandPort : INyxIdAuthorizationCatalogCommandPort
    {
        public List<(AuthorizationOwnerIdentity Owner, DateTimeOffset At)> Activations { get; } = [];
        public List<(AuthorizationOwnerIdentity Owner, string RefreshId, DateTimeOffset At)> Beginnings { get; } = [];
        public List<NyxIdAuthorizationCatalogObservation> Observations { get; } = [];
        public List<(AuthorizationOwnerIdentity Owner, string RefreshId, DateTimeOffset At, string Code)> Failures { get; } = [];
        public List<(
            AuthorizationOwnerIdentity Owner,
            string RefreshId,
            DateTimeOffset At,
            string Reason,
            NyxIdAuthorizationCatalogRefreshOutcomeStatus OutcomeStatus)> Invalidations { get; } = [];
        public List<(AuthorizationOwnerIdentity Owner, DateTimeOffset At, string Reason)> Cleanups { get; } = [];

        public RecordingObservationRuntime? Observation { get; set; }

        public bool PublishTerminalOutcomes { get; init; } = true;

        public NyxIdAuthorizationCatalogRefreshOutcomeStatus BeginOutcomeStatus { get; init; } =
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.Started;

        public int AllCalls => Activations.Count + Beginnings.Count + Observations.Count + Failures.Count +
                               Invalidations.Count + Cleanups.Count;

        public Task ActivateAsync(
            AuthorizationOwnerIdentity owner,
            DateTimeOffset activatedAtUtc,
            CancellationToken ct = default)
        {
            Activations.Add((owner.Clone(), activatedAtUtc));
            return Task.CompletedTask;
        }

        public Task BeginRefreshAsync(
            AuthorizationOwnerIdentity owner,
            string refreshId,
            DateTimeOffset startedAtUtc,
            CancellationToken ct = default)
        {
            Beginnings.Add((owner.Clone(), refreshId, startedAtUtc));
            Observation?.Publish(
                refreshId,
                BeginOutcomeStatus,
                BeginOutcomeStatus == NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded
                    ? "nyxid_catalog_refresh_superseded"
                    : string.Empty,
                startedAtUtc: startedAtUtc);
            return Task.CompletedTask;
        }

        public Task ObserveAsync(
            NyxIdAuthorizationCatalogObservation observation,
            CancellationToken ct = default)
        {
            Observations.Add(observation);
            if (PublishTerminalOutcomes)
            {
                Observation?.Publish(
                    observation.RefreshId,
                    NyxIdAuthorizationCatalogRefreshOutcomeStatus.Observed,
                    startedAtUtc: observation.ObservedAtUtc);
            }
            return Task.CompletedTask;
        }

        public Task RecordRefreshFailureAsync(
            AuthorizationOwnerIdentity owner,
            string refreshId,
            DateTimeOffset failedAtUtc,
            string failureCode,
            CancellationToken ct = default)
        {
            Failures.Add((owner.Clone(), refreshId, failedAtUtc, failureCode));
            if (PublishTerminalOutcomes)
            {
                Observation?.Publish(
                    refreshId,
                    NyxIdAuthorizationCatalogRefreshOutcomeStatus.Failed,
                    failureCode,
                    failedAtUtc);
            }
            return Task.CompletedTask;
        }

        public Task InvalidateAsync(
            AuthorizationOwnerIdentity owner,
            DateTimeOffset invalidatedAtUtc,
            string reason,
            CancellationToken ct = default)
        {
            Invalidations.Add((
                owner.Clone(),
                string.Empty,
                invalidatedAtUtc,
                reason,
                default));
            return Task.CompletedTask;
        }

        public Task InvalidateRefreshAsync(
            AuthorizationOwnerIdentity owner,
            string refreshId,
            DateTimeOffset invalidatedAtUtc,
            string reason,
            NyxIdAuthorizationCatalogRefreshOutcomeStatus outcomeStatus,
            CancellationToken ct = default)
        {
            Invalidations.Add((owner.Clone(), refreshId, invalidatedAtUtc, reason, outcomeStatus));
            if (PublishTerminalOutcomes)
                Observation?.Publish(refreshId, outcomeStatus, reason, invalidatedAtUtc);
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

    private sealed class RecordingObservationRuntime
        : INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort,
          INyxIdAuthorizationCatalogRefreshObservationProjectionPort
    {
        private IEventSink<NyxIdAuthorizationCatalogRefreshCommittedOutcome>? _sink;

        public bool ProjectionEnabled => true;

        public int Detached { get; private set; }

        public int ProjectionReleases { get; private set; }

        public int PreparationReleases { get; private set; }

        public Exception? DetachFailure { get; init; }

        public Exception? ProjectionReleaseFailure { get; init; }

        public Task<NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation?> PrepareAsync(
            string actorId,
            string refreshId,
            CancellationToken ct = default) =>
            Task.FromResult<NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation?>(
                new NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation(
                    actorId,
                    refreshId));

        public Task ReleaseAsync(
            NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation preparation,
            CancellationToken ct = default)
        {
            PreparationReleases++;
            return Task.CompletedTask;
        }

        public Task<
            EventSinkProjectionAttachment<INyxIdAuthorizationCatalogRefreshObservationProjectionLease>?>
            AttachExistingRefreshProjectionAsync(
                string actorId,
                string refreshId,
                IEventSink<NyxIdAuthorizationCatalogRefreshCommittedOutcome> sink,
                CancellationToken ct = default)
        {
            _sink = sink;
            var lease = new ObservationLease(actorId, refreshId);
            return Task.FromResult<
                EventSinkProjectionAttachment<
                    INyxIdAuthorizationCatalogRefreshObservationProjectionLease>?>(
                new EventSinkProjectionAttachment<
                    INyxIdAuthorizationCatalogRefreshObservationProjectionLease>(
                    lease,
                    new NoopAsyncDisposable()));
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            INyxIdAuthorizationCatalogRefreshObservationProjectionLease lease,
            IEventSink<NyxIdAuthorizationCatalogRefreshCommittedOutcome> sink,
            CancellationToken ct = default)
        {
            _sink = sink;
            return Task.FromResult<IAsyncDisposable?>(new NoopAsyncDisposable());
        }

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default)
        {
            Detached++;
            _sink = null;
            return DetachFailure == null
                ? Task.CompletedTask
                : Task.FromException(DetachFailure);
        }

        public Task ReleaseActorProjectionAsync(
            INyxIdAuthorizationCatalogRefreshObservationProjectionLease lease,
            CancellationToken ct = default)
        {
            ProjectionReleases++;
            return ProjectionReleaseFailure == null
                ? Task.CompletedTask
                : Task.FromException(ProjectionReleaseFailure);
        }

        public void Publish(
            string refreshId,
            NyxIdAuthorizationCatalogRefreshOutcomeStatus status,
            string failureCode = "",
            DateTimeOffset? startedAtUtc = null) =>
            _sink?.Push(new NyxIdAuthorizationCatalogRefreshCommittedOutcome(
                refreshId,
                status,
                1,
                failureCode,
                startedAtUtc ?? Now));

        private sealed record ObservationLease(string ActorId, string RefreshId)
            : INyxIdAuthorizationCatalogRefreshObservationProjectionLease;

        private sealed class NoopAsyncDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
