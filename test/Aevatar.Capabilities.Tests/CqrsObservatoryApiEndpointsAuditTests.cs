using System.Net;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
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

    private static async Task<WebApplication> CreateAppAsync(
        RecordingAuditTrailAppender appender,
        IProjectionScopeStatusListQueryPort scopeStatuses)
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
        builder.Services.AddSingleton<IProjectionReadModelInventoryQueryPort>(new FakeProjectionReadModelInventoryQueryPort());
        builder.Services.AddSingleton<IPlatformAdminAuthorizer>(new FakePlatformAdminAuthorizer());

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<EndpointAuditCaptureMiddleware>();
        app.UseAuthorization();
        app.MapCqrsObservatoryApiEndpoints();
        await app.StartAsync();
        return app;
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
                    LastObservedVersion: 8,
                    LastSuccessfulVersion: 7,
                    FailureCount: 0,
                    Lag: 1,
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

    private sealed class FakePlatformAdminAuthorizer : IPlatformAdminAuthorizer
    {
        public Task<PlatformCaller> ResolveCallerAsync(string bearerToken, CancellationToken ct = default)
        {
            bearerToken.Should().Be(RawToken);
            return Task.FromResult(new PlatformCaller(true, "admin", "admin@example.com", "user-123"));
        }
    }

    private sealed class RecordingAuditTrailAppender : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public Task<AuditTrailAppendReceipt> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.FromResult(new AuditTrailAppendReceipt(
                record.AuditId,
                record.AuditActorId,
                record.OccurredAt.ToDateTimeOffset()));
        }

        public async Task<IReadOnlyList<AuditTrailAppendReceipt>> AppendManyAsync(
            IReadOnlyList<AuditRecord> records,
            CancellationToken cancellationToken = default)
        {
            var receipts = new List<AuditTrailAppendReceipt>(records.Count);
            foreach (var record in records)
            {
                receipts.Add(await AppendAsync(record, cancellationToken));
            }

            return receipts;
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
