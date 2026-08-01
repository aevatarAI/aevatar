using System.Net;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Authentication.Abstractions;
using Aevatar.Bootstrap.Hosting;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Mainnet.Host.Api.Cqrs;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Capabilities.Tests;

public sealed class CqrsObservatoryApiEndpointsAuditTests
{
    private const string RawToken = "eyJhbGciOiJVTklUIn0.eyJzdWIiOiJ1c2VyLTEyMyJ9.c2lnbmF0dXJlLXZhbHVl";

    [Fact]
    public async Task CqrsObservatoryRoute_ShouldAppendEndpointAuditRecords()
    {
        var appender = new RecordingAuditTrailAppender();
        var scopeStatuses = new FakeProjectionScopeStatusListQueryPort();
        await using var app = await CreateAppAsync(appender, scopeStatuses);
        var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/cqrs/scopes?take=5&access_token={RawToken}&email=alice@example.com");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RawToken);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        scopeStatuses.Queries.Should().ContainSingle().Which.Take.Should().Be(5);
        appender.Records.Should().HaveCount(2);
        appender.Records[0].OperationName.Should().Be("cqrs.observatory.list-scopes.attempted");
        appender.Records[0].Outcome.Should().Be(AuditOutcome.Accepted);
        appender.Records[0].ResultSummary.Should().BeEmpty();
        appender.Records[1].OperationName.Should().Be("cqrs.observatory.list-scopes");
        appender.Records[1].Outcome.Should().Be(AuditOutcome.Accepted);
        appender.Records.Should().OnlyContain(record =>
            record.Target.Kind == "cqrs-projection-scopes" &&
            record.Target.Id == "platform" &&
            record.RequestSummary == "GET /api/cqrs/scopes" &&
            record.CapturePlane == AuditCapturePlane.BoundaryEndpoint);
        appender.Records.SelectMany(RecordStrings).Should().NotContain(value =>
            value.Contains(RawToken, StringComparison.Ordinal) ||
            value.Contains("alice@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectionScopeIntrospectionRoutes_ShouldReturnDetailAndPayloadFreeRecentMetadata()
    {
        var appender = new RecordingAuditTrailAppender();
        var introspection = new FakeProjectionScopeIntrospectionQueryPort
        {
            Snapshot = new ProjectionScopeIntrospectionSnapshot(
                "projection-scope-alpha",
                "actor-alpha",
                "workflow-execution",
                "session-alpha",
                ProjectionRuntimeMode.DurableMaterialization,
                Active: true,
                ObservationAttached: true,
                Released: false,
                StateVersion: 12,
                ReceivedEnvelopeTotal: 12,
                AttemptedEnvelopeTotal: 11,
                SuccessfulMaterializationTotal: 10,
                FailedAttemptTotal: 1,
                RetryExhaustedTotal: 7,
                RetryExhaustedFailureCount: 0,
                UnresolvedFailureCount: 1,
                OldestUnresolvedFailureAt: DateTimeOffset.Parse("2026-07-30T07:55:00Z"),
                FailureDiagnosticDroppedTotal: 0,
                SourceVersions: [new ProjectionSourceVersionSnapshot("actor-alpha", 11, 10, 1)],
                UpdatedAt: DateTimeOffset.Parse("2026-07-30T08:00:00Z")),
            Envelopes =
            [
                new ProjectionObservedEnvelopeSnapshot(
                    "event-new",
                    "type.googleapis.com/aevatar.WorkflowRunUpdated",
                    41,
                    DateTimeOffset.Parse("2026-07-30T07:59:00Z")),
                new ProjectionObservedEnvelopeSnapshot(
                    "event-old",
                    "type.googleapis.com/aevatar.WorkflowRunStarted",
                    40,
                    null),
            ],
        };
        await using var app = await CreateAppAsync(
            appender,
            new FakeProjectionScopeStatusListQueryPort(),
            introspection);
        var client = app.GetTestClient();

        var detailResponse = await SendAuthorizedAsync(
            client,
            HttpMethod.Get,
            "/api/cqrs/scopes/projection-scope-alpha");
        var recentResponse = await SendAuthorizedAsync(
            client,
            HttpMethod.Get,
            $"/api/cqrs/scopes/projection-scope-alpha/recent-envelopes?take=7&access_token={RawToken}&email=alice@example.com");

        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        recentResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        detail.RootElement.GetProperty("scopeActorId").GetString().Should().Be("projection-scope-alpha");
        detail.RootElement.GetProperty("stateVersion").GetInt64().Should().Be(12);
        detail.RootElement.GetProperty("attemptedEnvelopeTotal").GetInt64().Should().Be(11);
        detail.RootElement.GetProperty("successfulMaterializationTotal").GetInt64().Should().Be(10);
        detail.RootElement.GetProperty("retryExhaustedTotal").GetInt64().Should().Be(7);
        detail.RootElement.GetProperty("retryExhaustedFailureCount").GetInt32().Should().Be(0);
        detail.RootElement.GetProperty("unresolvedFailureCount").GetInt32().Should().Be(1);
        var sourceVersion = detail.RootElement.GetProperty("sourceVersions")[0];
        sourceVersion.GetProperty("sourceActorId").GetString().Should().Be("actor-alpha");
        sourceVersion.GetProperty("versionGap").GetInt64().Should().Be(1);
        detail.RootElement.GetProperty("updatedAt").GetDateTimeOffset()
            .Should().Be(DateTimeOffset.Parse("2026-07-30T08:00:00Z"));

        var recentJson = await recentResponse.Content.ReadAsStringAsync();
        using var recent = JsonDocument.Parse(recentJson);
        var envelopes = recent.RootElement.GetProperty("envelopes");
        envelopes.GetArrayLength().Should().Be(2);
        envelopes[0].GetProperty("eventId").GetString().Should().Be("event-new");
        envelopes[0].GetProperty("typeUrl").GetString().Should().Be("type.googleapis.com/aevatar.WorkflowRunUpdated");
        envelopes[0].GetProperty("stateVersion").GetInt64().Should().Be(41);
        envelopes[1].GetProperty("timestampUtc").ValueKind.Should().Be(JsonValueKind.Null);
        recentJson.Contains("payload", StringComparison.OrdinalIgnoreCase).Should().BeFalse();

        introspection.GetRequests.Should().Equal("projection-scope-alpha");
        introspection.RecentRequests.Should().ContainSingle().Which.Should().Be(("projection-scope-alpha", 7));
        appender.Records.Select(static record => record.OperationName).Should().Equal(
            "cqrs.observatory.get-scope.attempted",
            "cqrs.observatory.get-scope",
            "cqrs.observatory.list-recent-envelopes.attempted",
            "cqrs.observatory.list-recent-envelopes");
        appender.Records.Should().OnlyContain(record =>
            record.Target.Kind == "cqrs-projection-scope" &&
            record.Target.Id == "projection-scope-alpha" &&
            record.CapturePlane == AuditCapturePlane.BoundaryEndpoint);
        appender.Records.SelectMany(RecordStrings).Should().NotContain(value =>
            value.Contains(RawToken, StringComparison.Ordinal) ||
            value.Contains("alice@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectionScopeDetail_ShouldReturnNotFoundWhenReadModelIsMissing()
    {
        var introspection = new FakeProjectionScopeIntrospectionQueryPort();
        await using var app = await CreateAppAsync(
            new RecordingAuditTrailAppender(),
            new FakeProjectionScopeStatusListQueryPort(),
            introspection);

        var response = await SendAuthorizedAsync(
            app.GetTestClient(),
            HttpMethod.Get,
            "/api/cqrs/scopes/missing-scope");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        introspection.GetRequests.Should().Equal("missing-scope");
    }

    [Fact]
    public async Task ProjectionScopeIntrospection_ShouldDenyNonAdminBeforeQueryingReadModel()
    {
        var introspection = new FakeProjectionScopeIntrospectionQueryPort();
        var authorizer = new FakePlatformAdminAuthorizer(isElevated: false);
        await using var app = await CreateAppAsync(
            new RecordingAuditTrailAppender(),
            new FakeProjectionScopeStatusListQueryPort(),
            introspection,
            authorizer);
        var client = app.GetTestClient();

        var detailResponse = await SendAuthorizedAsync(
            client,
            HttpMethod.Get,
            "/api/cqrs/scopes/projection-scope-alpha");
        var recentResponse = await SendAuthorizedAsync(
            client,
            HttpMethod.Get,
            "/api/cqrs/scopes/projection-scope-alpha/recent-envelopes");

        detailResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        recentResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        introspection.GetRequests.Should().BeEmpty();
        introspection.RecentRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectionScopeIntrospection_ShouldRejectMissingOrEmptyBearerBeforeQueryingReadModel()
    {
        var introspection = new FakeProjectionScopeIntrospectionQueryPort();
        var authorizer = new FakePlatformAdminAuthorizer();
        await using var app = await CreateAppAsync(
            new RecordingAuditTrailAppender(),
            new FakeProjectionScopeStatusListQueryPort(),
            introspection,
            authorizer);
        var client = app.GetTestClient();

        var missingResponse = await client.GetAsync("/api/cqrs/scopes/projection-scope-alpha");
        using var emptyBearer = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/cqrs/scopes/projection-scope-alpha/recent-envelopes");
        emptyBearer.Headers.TryAddWithoutValidation("Authorization", "Bearer " );
        var emptyResponse = await client.SendAsync(emptyBearer);

        missingResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        emptyResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        authorizer.Tokens.Should().BeEmpty();
        introspection.GetRequests.Should().BeEmpty();
        introspection.RecentRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/cqrs/scopes/projection-scope-alpha")]
    [InlineData("/api/cqrs/scopes/projection-scope-alpha/recent-envelopes")]
    public async Task ProjectionScopeIntrospection_ShouldNotExposeMutationRoutes(string path)
    {
        await using var app = await CreateAppAsync(
            new RecordingAuditTrailAppender(),
            new FakeProjectionScopeStatusListQueryPort(),
            new FakeProjectionScopeIntrospectionQueryPort());

        var response = await SendAuthorizedAsync(app.GetTestClient(), HttpMethod.Post, path);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    private static async Task<WebApplication> CreateAppAsync(
        RecordingAuditTrailAppender appender,
        IProjectionScopeStatusListQueryPort scopeStatuses,
        IProjectionScopeIntrospectionQueryPort? introspection = null,
        FakePlatformAdminAuthorizer? authorizer = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, RouteAuditAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IAuditTrailAppender>(appender);
        builder.Services.AddSingleton<IAuditActorIdentityHasher>(new StableAuditActorIdentityHasher());
        builder.Services.AddSingleton(scopeStatuses);
        builder.Services.AddSingleton<IProjectionScopeIntrospectionQueryPort>(
            introspection ?? new FakeProjectionScopeIntrospectionQueryPort());
        builder.Services.AddSingleton<IProjectionReadModelInventoryQueryPort>(new FakeProjectionReadModelInventoryQueryPort());
        builder.Services.AddSingleton<IPlatformAdminAuthorizer>(authorizer ?? new FakePlatformAdminAuthorizer());

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<EndpointAuditCaptureMiddleware>();
        app.UseAuthorization();
        app.MapCqrsObservatoryApiEndpoints();
        await app.StartAsync();
        return app;
    }

    private static Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpClient client,
        HttpMethod method,
        string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RawToken);
        return client.SendAsync(request);
    }

    private static IEnumerable<string> RecordStrings(AuditRecord record)
    {
        yield return record.AuditId;
        yield return record.ScopeId;
        yield return record.AuditActorId;
        yield return record.IdentityKeyId;
        yield return record.OperationName;
        yield return record.Target.Kind;
        yield return record.Target.Id;
        yield return record.Target.DisplayName;
        yield return record.Correlation.TraceId;
        yield return record.Correlation.RequestId;
        yield return record.Correlation.CommandId;
        yield return record.Correlation.CallId;
        yield return record.Correlation.SessionId;
        yield return record.Correlation.WorkflowRunId;
        yield return record.Correlation.ApprovalId;
        yield return record.RequestSummary;
        yield return record.ResultSummary;
        yield return record.ErrorCode;
        yield return record.ErrorSummary;
        foreach (var annotation in record.Annotations)
        {
            yield return annotation.Key;
            yield return annotation.Value;
        }
    }

    private sealed class FakeProjectionScopeStatusListQueryPort : IProjectionScopeStatusListQueryPort
    {
        public List<ProjectionScopeStatusListQuery> Queries { get; } = [];

        public Task<IReadOnlyList<ProjectionScopeStatusSnapshot>> ListAsync(
            ProjectionScopeStatusListQuery query,
            CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult<IReadOnlyList<ProjectionScopeStatusSnapshot>>(
            [
                new ProjectionScopeStatusSnapshot(
                    "projection-scope-a",
                    Active: true,
                    ReceivedEnvelopeTotal: 8,
                    AttemptedEnvelopeTotal: 8,
                    SuccessfulMaterializationTotal: 7,
                    FailedAttemptTotal: 1,
                    RetryExhaustedTotal: 7,
                    RetryExhaustedFailureCount: 0,
                    UnresolvedFailureCount: 0,
                    OldestUnresolvedFailureAt: null,
                    FailureDiagnosticDroppedTotal: 0,
                    SourceActorCount: 1,
                    SingleSourceVersionGap: 1,
                    UpdatedAt: DateTimeOffset.UnixEpoch),
            ]);
        }
    }

    private sealed class FakeProjectionReadModelInventoryQueryPort : IProjectionReadModelInventoryQueryPort
    {
        public Task<ProjectionReadModelInventory> GetInventoryAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new ProjectionReadModelInventory(
            [
                new ProjectionReadModelGroup(
                    ProjectionReadModelSinkShape.Document,
                    "in-memory",
                    []),
            ]));
        }
    }

    private sealed class FakeProjectionScopeIntrospectionQueryPort : IProjectionScopeIntrospectionQueryPort
    {
        public ProjectionScopeIntrospectionSnapshot? Snapshot { get; init; }
        public IReadOnlyList<ProjectionObservedEnvelopeSnapshot> Envelopes { get; init; } = [];
        public List<string> GetRequests { get; } = [];
        public List<(string ScopeActorId, int Take)> RecentRequests { get; } = [];

        public Task<ProjectionScopeIntrospectionSnapshot?> GetAsync(
            string scopeActorId,
            CancellationToken ct = default)
        {
            GetRequests.Add(scopeActorId);
            return Task.FromResult(Snapshot);
        }

        public Task<IReadOnlyList<ProjectionObservedEnvelopeSnapshot>> ListRecentEnvelopesAsync(
            string scopeActorId,
            int take,
            CancellationToken ct = default)
        {
            RecentRequests.Add((scopeActorId, take));
            return Task.FromResult(Envelopes);
        }
    }

    private sealed class FakePlatformAdminAuthorizer(bool isElevated = true) : IPlatformAdminAuthorizer
    {
        public List<string> Tokens { get; } = [];

        public Task<PlatformCaller> ResolveCallerAsync(string bearerToken, CancellationToken ct = default)
        {
            bearerToken.Should().Be(RawToken);
            Tokens.Add(bearerToken);
            return Task.FromResult(new PlatformCaller(isElevated, "admin", "admin@example.com", "user-123"));
        }
    }

    private sealed class RecordingAuditTrailAppender : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.FromResult(AuditTrailAppendResult.Appended(
                record.AuditId,
                record.AuditActorId,
                record.OccurredAt.ToDateTimeOffset()));
        }
    }

    private sealed class StableAuditActorIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new($"hashed:{canonicalActorKey}", "kid-test");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) =>
            auditActorId == $"hashed:{canonicalActorKey}" &&
            identityKeyId == "kid-test";
    }

    private sealed class RouteAuditAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var authorization) ||
                !authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim("sub", "user-123"),
                new System.Security.Claims.Claim("scope_id", "scope-a"),
            ], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
