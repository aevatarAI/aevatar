using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Bootstrap.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.Bootstrap.Tests;

public sealed class EndpointAuditCaptureMiddlewareTests
{
    private const string RawToken = "eyJhbGciOiJVTklUIn0.eyJzdWIiOiJ1c2VyLTEyMyJ9.c2lnbmF0dXJlLXZhbHVl";
    private const string RawEmail = "admin@example.test";

    [Fact]
    public async Task AnnotatedMutation_WhenAccepted_ShouldAppendAttemptedAndAccepted()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateHostAsync(appender);

        using var request = AuthenticatedRequest(HttpMethod.Post, "/audited/widgets/widget-1");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        appender.Records.Should().HaveCount(2);
        appender.Records[0].OperationName.Should().Be("test.widget.create.attempted");
        appender.Records[0].Outcome.Should().Be(AuditOutcome.Accepted);
        appender.Records[0].LifecyclePhase.Should().Be(AuditLifecyclePhase.Accepted);
        appender.Records[0].TerminalOutcome.Should().Be(AuditTerminalOutcome.Unspecified);
        appender.Records[0].ResultSummary.Should().BeEmpty();
        appender.Records[1].OperationName.Should().Be("test.widget.create");
        appender.Records[1].Outcome.Should().Be(AuditOutcome.Accepted);
        appender.Records[1].LifecyclePhase.Should().Be(AuditLifecyclePhase.Accepted);
        appender.Records[1].TerminalOutcome.Should().Be(AuditTerminalOutcome.Unspecified);
        appender.Records[1].ResultSummary.Should().Be("status=202");
        appender.Records.Should().OnlyContain(record =>
            record.CapturePlane == AuditCapturePlane.BoundaryEndpoint &&
            record.Target.Kind == "widget" &&
            record.Target.Id == "widget-1" &&
            record.AuditActorId == "hashed-user-123" &&
            record.IdentityKeyId == "kid-test");
    }

    [Fact]
    public async Task AnnotatedEndpoint_WhenOk_ShouldAppendAcceptedNonterminalRecord()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateHostAsync(appender);

        using var request = AuthenticatedRequest(HttpMethod.Get, "/audited/widgets/widget-1");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        appender.Records.Should().HaveCount(2);
        appender.Records[1].OperationName.Should().Be("test.widget.read");
        appender.Records[1].Outcome.Should().Be(AuditOutcome.Accepted);
        appender.Records[1].LifecyclePhase.Should().Be(AuditLifecyclePhase.Accepted);
        appender.Records[1].TerminalOutcome.Should().Be(AuditTerminalOutcome.Unspecified);
    }

    [Fact]
    public async Task AnnotatedEndpoint_WhenAuthenticatedForbidden_ShouldAppendAttemptedAndDenied()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateHostAsync(appender);

        using var request = AuthenticatedRequest(HttpMethod.Post, "/admin/widgets/widget-1");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        appender.Records.Should().HaveCount(2);
        appender.Records[0].OperationName.Should().Be("test.widget.admin.attempted");
        appender.Records[1].OperationName.Should().Be("test.widget.admin");
        appender.Records[1].Outcome.Should().Be(AuditOutcome.Denied);
        appender.Records[1].LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        appender.Records[1].TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
        appender.Records[1].Failure.Category.Should().Be(AuditFailureCategory.Authorization);
        appender.Records[1].ErrorCode.Should().Be("authorization_denied");
    }

    [Fact]
    public async Task AnnotatedEndpoint_WhenHandlerThrows_ShouldAppendErrorTerminalRecord()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateHostAsync(appender);

        using var request = AuthenticatedRequest(HttpMethod.Post, "/audited/widgets/widget-1/throw");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        appender.Records.Should().HaveCount(2);
        appender.Records[0].OperationName.Should().Be("test.widget.throw.attempted");
        appender.Records[0].Outcome.Should().Be(AuditOutcome.Accepted);
        appender.Records[1].OperationName.Should().Be("test.widget.throw");
        appender.Records[1].Outcome.Should().Be(AuditOutcome.Error);
        appender.Records[1].LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        appender.Records[1].TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
        appender.Records[1].Failure.Code.Should().Be("endpoint_error");
        appender.Records[1].ErrorCode.Should().Be("endpoint_error");
        appender.Records[1].ErrorSummary.Should().Be(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task AnnotatedEndpoint_WhenStatusCodeIs500_ShouldAppendErrorTerminalRecord()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateHostAsync(appender);

        using var request = AuthenticatedRequest(HttpMethod.Post, "/audited/widgets/widget-1/fail");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        appender.Records.Should().HaveCount(2);
        appender.Records[1].OperationName.Should().Be("test.widget.fail");
        appender.Records[1].Outcome.Should().Be(AuditOutcome.Error);
        appender.Records[1].ErrorCode.Should().Be("endpoint_error");
        appender.Records[1].ErrorSummary.Should().Be("status=500");
    }

    [Fact]
    public async Task AnnotatedEndpoint_WhenStatusCodeIs504_ShouldRecordConsistentTimeoutFailure()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateHostAsync(appender);

        using var request = AuthenticatedRequest(HttpMethod.Post, "/audited/widgets/widget-1/timeout");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        appender.Records.Should().HaveCount(2);
        var record = appender.Records[1];
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.TimedOut);
        record.Failure.Code.Should().Be("endpoint_timeout");
        record.Failure.Category.Should().Be(AuditFailureCategory.Timeout);
        record.ErrorCode.Should().Be(record.Failure.Code);
        record.ErrorSummary.Should().Be(record.Failure.SanitizedMessage);
    }

    [Fact]
    public async Task AnnotatedEndpoint_WhenStatusCodeIs300_ShouldAppendSuccessTerminalRecord()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateHostAsync(appender);

        using var request = AuthenticatedRequest(HttpMethod.Post, "/audited/widgets/widget-1/redirect");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MultipleChoices);
        appender.Records.Should().HaveCount(2);
        appender.Records[1].OperationName.Should().Be("test.widget.redirect");
        appender.Records[1].Outcome.Should().Be(AuditOutcome.Success);
    }

    [Fact]
    public async Task FromRouteValues_ShouldJoinRouteValuesAndRedactSensitiveSegments()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateHostAsync(appender);

        using var request = AuthenticatedRequest(HttpMethod.Post, $"/audited/scopes/scope-a/members/{RawEmail}/runs/run-1");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        appender.Records.Should().HaveCount(2);
        appender.Records.Should().OnlyContain(record =>
            record.Target.Kind == "workflow-run" &&
            record.Target.Id == "scope-a/redacted/run-1" &&
            record.RequestSummary.Contains("/audited/scopes/{scopeId}/members/{memberId}/runs/{runId}", StringComparison.Ordinal) &&
            record.RequestSummary.Contains("scopeId=scope-a", StringComparison.Ordinal) &&
            record.RequestSummary.Contains("memberId=redacted", StringComparison.Ordinal) &&
            record.RequestSummary.Contains("runId=run-1", StringComparison.Ordinal));
        appender.Records.SelectMany(RecordStrings).Should().NotContain(value => value.Contains(RawEmail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnannotatedEndpoint_ShouldNotAppendAuditRecord()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateHostAsync(appender);

        using var request = AuthenticatedRequest(HttpMethod.Get, "/plain");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        appender.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task AnnotatedEndpoint_WhenUnauthenticatedChallenge_ShouldNotAppendAuditRecord()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateHostAsync(appender);

        var response = await app.GetTestClient().PostAsync("/audited/widgets/widget-1", new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        appender.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task AnnotatedAnonymousIngress_WhenUnauthenticatedAndOptedIn_ShouldAppendRecordsWithAnonymousActor()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateHostAsync(appender);

        // No Authorization header -> unauthenticated caller, but the ingress
        // endpoint opts into anonymous capture, so the attempt is still recorded.
        var response = await app.GetTestClient().PostAsync(
            "/audited/anon-ingress/ingress-1",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        appender.Records.Should().HaveCount(2);
        appender.Records.Should().OnlyContain(record =>
            record.AuditActorId == "hashed-anonymous" &&
            record.IdentityKeyId == "kid-test" &&
            record.ActorKind == AuditActorKind.System &&
            record.CredentialSource == AuditCredentialSource.System &&
            record.CapturePlane == AuditCapturePlane.BoundaryEndpoint &&
            record.OperationName.StartsWith("test.anon.ingress", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TokenShapedRequestValue_ShouldNotEnterAppendedRecords()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateHostAsync(appender);

        using var request = AuthenticatedRequest(HttpMethod.Post, $"/audited/widgets/{RawToken}");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        appender.Records.Should().NotBeEmpty();
        appender.Records.SelectMany(RecordStrings).Should().NotContain(value => value.Contains(RawToken, StringComparison.Ordinal));
        appender.Records.Should().OnlyContain(record =>
            record.RequestSummary == "redacted" ||
            !record.RequestSummary.Contains(RawToken, StringComparison.Ordinal));
        appender.Records.Should().OnlyContain(record =>
            record.Target.Id == "redacted" ||
            !record.Target.Id.Contains(RawToken, StringComparison.Ordinal));
        appender.Records.Should().OnlyContain(record =>
            record.RequestSummary == "redacted" ||
            record.RequestSummary.Contains("/audited/widgets/{widgetId}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmailShapedQueryValue_ShouldBeRedactedBeforeAppend()
    {
        var appender = new RecordingAuditTrailAppender();
        await using var app = await CreateHostAsync(appender);

        using var request = AuthenticatedRequest(HttpMethod.Get, $"/audited/search?email={Uri.EscapeDataString(RawEmail)}");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        appender.Records.Should().HaveCount(2);
        appender.Records.SelectMany(RecordStrings).Should().NotContain(value => value.Contains(RawEmail, StringComparison.Ordinal));
        appender.Records.Should().OnlyContain(record =>
            record.Target.Id == "redacted" &&
            record.RequestSummary == "redacted");
    }

    [Fact]
    public async Task AppenderFailure_ShouldNotAffectBusinessResponse_AndShouldLogError()
    {
        var appender = new RecordingAuditTrailAppender { ThrowOnAppend = true };
        var loggerProvider = new RecordingLoggerProvider();
        await using var app = await CreateHostAsync(appender, loggerProvider);

        using var request = AuthenticatedRequest(HttpMethod.Post, "/audited/widgets/widget-1");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        loggerProvider.Messages.Should().Contain(message =>
            message.LogLevel == LogLevel.Error &&
            message.Category == typeof(EndpointAuditCaptureMiddleware).FullName &&
            message.Text.Contains("Endpoint audit append failed.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingAuditPorts_ShouldNotAffectBusinessResponse()
    {
        await using var app = await CreateHostWithoutAuditPortsAsync();

        using var request = AuthenticatedRequest(HttpMethod.Post, "/audited/widgets/widget-1");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task RequestCancellation_ShouldAppendCancelledTerminalRecordWithHostOwnedToken()
    {
        var appender = new RecordingAuditTrailAppender();
        using var requestCancellation = new CancellationTokenSource();
        var context = CreateAuditedContext(
            "request-cancelled",
            "test.widget.cancel",
            requestCancellation.Token);
        RequestDelegate next = _ =>
        {
            requestCancellation.Cancel();
            return Task.FromException(new OperationCanceledException(requestCancellation.Token));
        };
        var middleware = new EndpointAuditCaptureMiddleware(
            next,
            [appender],
            [new FakeAuditActorIdentityHasher()],
            NullLogger<EndpointAuditCaptureMiddleware>.Instance);

        Func<Task> act = () => middleware.InvokeAsync(context);

        await act.Should().ThrowAsync<OperationCanceledException>();
        appender.Records.Should().HaveCount(2);
        appender.Records[0].OperationName.Should().Be("test.widget.cancel.attempted");
        appender.Records[1].OperationName.Should().Be("test.widget.cancel");
        appender.Records[1].Outcome.Should().Be(AuditOutcome.Cancelled);
        appender.Records[1].LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        appender.Records[1].TerminalOutcome.Should().Be(AuditTerminalOutcome.Cancelled);
        appender.Records[1].Failure.Should().BeNull();
        appender.CancellationStates.Should().Equal(false, false);
    }

    [Fact]
    public async Task TerminalAppend_WhenAppenderIgnoresCancellation_ShouldRespectDeadlineAndObserveLateFault()
    {
        var appender = new NonCooperativeAuditTrailAppender();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-14T10:00:00Z"));
        var loggerProvider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var middleware = new EndpointAuditCaptureMiddleware(
            _ => Task.CompletedTask,
            [appender],
            [new FakeAuditActorIdentityHasher()],
            loggerFactory.CreateLogger<EndpointAuditCaptureMiddleware>(),
            timeProvider);
        var context = CreateAuditedContext("request-timeout", "test.widget.timeout");

        var invocation = middleware.InvokeAsync(context);
        await appender.TerminalAppendStarted.Task;

        invocation.IsCompleted.Should().BeFalse();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        await invocation;

        appender.Records.Should().HaveCount(2);
        appender.Records[0].OperationName.Should().Be("test.widget.timeout.attempted");
        appender.Records[1].OperationName.Should().Be("test.widget.timeout");
        appender.TerminalCancellationToken.IsCancellationRequested.Should().BeTrue();
        loggerProvider.Messages.Should().Contain(message =>
            message.LogLevel == LogLevel.Error &&
            message.Text.Contains("timed out after 5s", StringComparison.Ordinal));

        appender.FailTerminal(new InvalidOperationException("late terminal failure"));

        loggerProvider.Messages.Should().Contain(message =>
            message.LogLevel == LogLevel.Error &&
            message.Text.Contains("failed after its deadline", StringComparison.Ordinal));
    }

    private static DefaultHttpContext CreateAuditedContext(
        string traceIdentifier,
        string operationName,
        CancellationToken requestAborted = default)
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = traceIdentifier,
            RequestAborted = requestAborted,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "user-123"),
                new Claim("scope_id", "scope-a"),
            ], "Test")),
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/audited/widgets/widget-1";
        var metadata = new EndpointAuditMetadata(
            operationName,
            AuditSensitivityLevel.Confidential,
            "widget",
            _ => ValueTask.FromResult<EndpointAuditTarget?>(new EndpointAuditTarget("widget", "widget-1")),
            _ => ValueTask.FromResult("request captured"),
            _ => ValueTask.FromResult("result captured"));
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(metadata),
            "audited endpoint"));
        return context;
    }

    private static async Task<WebApplication> CreateHostAsync(
        RecordingAuditTrailAppender appender,
        ILoggerProvider? loggerProvider = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (loggerProvider is not null)
        {
            builder.Logging.AddProvider(loggerProvider);
        }

        builder.AddAevatarDefaultHost(options =>
        {
            options.EnableCors = false;
            options.EnableConnectorBootstrap = false;
            options.AutoMapCapabilities = false;
            options.MapRootHealthEndpoint = false;
            options.EnableHealthEndpoints = false;
            options.EnableOpenApiDocument = false;
            options.AllowLocalFileSecretsStore = false;
        });
        builder.Services.AddSingleton<IAuditTrailAppender>(appender);
        builder.Services.AddSingleton<IAuditActorIdentityHasher>(new FakeAuditActorIdentityHasher());
        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("AdminOnly", policy => policy.RequireClaim("role", "admin"));
        });

        var app = builder.Build();
        app.UseAevatarDefaultHost();

        app.MapPost("/audited/widgets/{widgetId}", (string widgetId) => Results.Accepted($"/audited/widgets/{widgetId}"))
            .WithEndpointAudit(
                "test.widget.create",
                AuditSensitivityLevel.Confidential,
                "widget",
                EndpointAuditTargetResolvers.FromRouteValue("widget", "widgetId"),
                EndpointAuditSanitizers.WithRouteValues("widgetId"))
            .RequireAuthorization();
        app.MapGet("/audited/widgets/{widgetId}", (string widgetId) => Results.Ok(new { id = widgetId }))
            .WithEndpointAudit(
                "test.widget.read",
                AuditSensitivityLevel.Confidential,
                "widget",
                EndpointAuditTargetResolvers.FromRouteValue("widget", "widgetId"),
                EndpointAuditSanitizers.WithRouteValues("widgetId"))
            .RequireAuthorization();
        app.MapGet("/audited/search", () => Results.Ok())
            .WithEndpointAudit(
                "test.widget.search",
                AuditSensitivityLevel.Restricted,
                "email-lookup",
                EndpointAuditTargetResolvers.FromQuery("email-lookup", "email"),
                context => ValueTask.FromResult($"email={context.HttpContext.Request.Query["email"]}"))
            .RequireAuthorization();
        app.MapPost("/admin/widgets/{widgetId}", () => Results.Accepted())
            .WithEndpointAudit(
                "test.widget.admin",
                AuditSensitivityLevel.Confidential,
                "widget",
                EndpointAuditTargetResolvers.FromRouteValue("widget", "widgetId"),
                EndpointAuditSanitizers.WithRouteValues("widgetId"))
            .RequireAuthorization("AdminOnly");
        app.MapPost("/audited/widgets/{widgetId}/throw", () =>
            {
                throw new InvalidOperationException("handler failed");
            })
            .WithEndpointAudit(
                "test.widget.throw",
                AuditSensitivityLevel.Confidential,
                "widget",
                EndpointAuditTargetResolvers.FromRouteValue("widget", "widgetId"),
                EndpointAuditSanitizers.WithRouteValues("widgetId"))
            .RequireAuthorization();
        app.MapPost("/audited/widgets/{widgetId}/fail", () => Results.StatusCode(StatusCodes.Status500InternalServerError))
            .WithEndpointAudit(
                "test.widget.fail",
                AuditSensitivityLevel.Confidential,
                "widget",
                EndpointAuditTargetResolvers.FromRouteValue("widget", "widgetId"),
                EndpointAuditSanitizers.WithRouteValues("widgetId"))
            .RequireAuthorization();
        app.MapPost("/audited/widgets/{widgetId}/timeout", () => Results.StatusCode(StatusCodes.Status504GatewayTimeout))
            .WithEndpointAudit(
                "test.widget.timeout",
                AuditSensitivityLevel.Confidential,
                "widget",
                EndpointAuditTargetResolvers.FromRouteValue("widget", "widgetId"),
                EndpointAuditSanitizers.WithRouteValues("widgetId"))
            .RequireAuthorization();
        app.MapPost("/audited/widgets/{widgetId}/redirect", () => Results.StatusCode(StatusCodes.Status300MultipleChoices))
            .WithEndpointAudit(
                "test.widget.redirect",
                AuditSensitivityLevel.Confidential,
                "widget",
                EndpointAuditTargetResolvers.FromRouteValue("widget", "widgetId"),
                EndpointAuditSanitizers.WithRouteValues("widgetId"))
            .RequireAuthorization();
        app.MapPost("/audited/scopes/{scopeId}/members/{memberId}/runs/{runId}", () => Results.Accepted())
            .WithEndpointAudit(
                "test.workflow-run.resume",
                AuditSensitivityLevel.Confidential,
                "workflow-run",
                EndpointAuditTargetResolvers.FromRouteValues("workflow-run", "scopeId", "memberId", "runId"),
                EndpointAuditSanitizers.WithRouteValues("scopeId", "memberId", "runId"))
            .RequireAuthorization();
        app.MapPost("/audited/anon-ingress/{ingressId}", (string ingressId) => Results.Accepted($"/audited/anon-ingress/{ingressId}"))
            .WithEndpointAudit(
                "test.anon.ingress",
                AuditSensitivityLevel.Confidential,
                "anon-ingress",
                EndpointAuditTargetResolvers.FromRouteValue("anon-ingress", "ingressId"),
                EndpointAuditSanitizers.WithRouteValues("ingressId"),
                captureUnauthenticated: true)
            .AllowAnonymous();
        app.MapGet("/plain", () => Results.Ok()).RequireAuthorization();

        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> CreateHostWithoutAuditPortsAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.AddAevatarDefaultHost(options =>
        {
            options.EnableCors = false;
            options.EnableConnectorBootstrap = false;
            options.AutoMapCapabilities = false;
            options.MapRootHealthEndpoint = false;
            options.EnableHealthEndpoints = false;
            options.EnableOpenApiDocument = false;
            options.AllowLocalFileSecretsStore = false;
        });
        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });

        var app = builder.Build();
        app.UseAevatarDefaultHost();

        app.MapPost("/audited/widgets/{widgetId}", (string widgetId) => Results.Accepted($"/audited/widgets/{widgetId}"))
            .WithEndpointAudit(
                "test.widget.create",
                AuditSensitivityLevel.Confidential,
                "widget",
                EndpointAuditTargetResolvers.FromRouteValue("widget", "widgetId"),
                EndpointAuditSanitizers.WithRouteValues("widgetId"))
            .RequireAuthorization();

        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Test", "user");
        return request;
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

    private sealed class RecordingAuditTrailAppender : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public List<bool> CancellationStates { get; } = [];

        public bool ThrowOnAppend { get; init; }

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            CancellationStates.Add(cancellationToken.IsCancellationRequested);
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnAppend)
            {
                throw new InvalidOperationException("append failed");
            }

            Records.Add(record);
            return Task.FromResult(AuditTrailAppendResult.Appended(
                record.AuditId,
                record.AuditActorId,
                record.OccurredAt.ToDateTimeOffset()));
        }
    }

    private sealed class NonCooperativeAuditTrailAppender : IAuditTrailAppender
    {
        private readonly TaskCompletionSource<AuditTrailAppendResult> _terminalCompletion = new();
        private int _appendCount;

        public List<AuditRecord> Records { get; } = [];

        public TaskCompletionSource TerminalAppendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken TerminalCancellationToken { get; private set; }

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            if (Interlocked.Increment(ref _appendCount) == 1)
            {
                return Task.FromResult(AuditTrailAppendResult.Appended(
                    record.AuditId,
                    record.AuditActorId,
                    record.OccurredAt.ToDateTimeOffset()));
            }

            TerminalCancellationToken = cancellationToken;
            TerminalAppendStarted.TrySetResult();
            return _terminalCompletion.Task;
        }

        public void FailTerminal(Exception exception)
        {
            if (!_terminalCompletion.TrySetException(exception))
                throw new InvalidOperationException("Terminal append was already completed.");
        }
    }

    private sealed class FakeAuditActorIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey)
        {
            return canonicalActorKey switch
            {
                "nyxid:user-123" => new AuditActorIdentity("hashed-user-123", "kid-test"),
                "system:endpoint-audit-anonymous" => new AuditActorIdentity("hashed-anonymous", "kid-test"),
                _ => throw new InvalidOperationException($"Unexpected canonical actor key: {canonicalActorKey}"),
            };
        }

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId)
        {
            return canonicalActorKey == "nyxid:user-123" &&
                   auditActorId == "hashed-user-123" &&
                   identityKeyId == "kid-test";
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var authorization) ||
                !authorization.ToString().StartsWith("Test ", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim("sub", "user-123"),
                new Claim("scope_id", "scope-a"),
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<LogMessage> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            return new RecordingLogger(categoryName, Messages);
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(string category, List<LogMessage> messages) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Add(new LogMessage(category, logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogMessage(string Category, LogLevel LogLevel, string Text);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
