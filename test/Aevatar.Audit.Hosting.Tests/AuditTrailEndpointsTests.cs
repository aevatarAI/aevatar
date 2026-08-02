using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Hosting;
using Aevatar.Authentication.Abstractions;
using Aevatar.Capabilities;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.Audit.Hosting.Tests;

public sealed class AuditTrailEndpointsTests
{
    private const string CallerScope = "scope-alice";
    private const string OtherScope = "scope-bob";
    private const string CallerSubject = "user-audit-alpha";

    [Fact]
    public async Task QueryChatActivity_DefaultsToPersonalScopeAndAllRetainedActorKeys()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var hasher = new RecordingHasher
        {
            Identities =
            [
                new AuditActorIdentity("actor-key-2", "key-2"),
                new AuditActorIdentity("actor-key-1", "key-1"),
            ],
        };
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, "token", queryPort, authorizer, subject: CallerSubject);

        var result = await AuditTrailEndpoints.QueryChatActivity(
            http,
            BuildEndpointDependencies(queryPort, hasher, authorizer),
            NullLoggerFactory.Instance,
            take: 500);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        var query = queryPort.Queries.Should().ContainSingle().Which;
        query.ScopeId.Should().Be(CallerScope);
        query.AuditActorId.Should().BeNull();
        query.AuditActorIds.Should().Equal("actor-key-2", "actor-key-1");
        query.RequireChatProvenance.Should().BeTrue();
        query.Take.Should().Be(200);
        hasher.CanonicalActorKeys.Should().Equal("nyxid:user-audit-alpha");
        authorizer.Calls.Should().Be(0);
        body.Should().Contain("conversation-alpha").And.Contain("continuationCursor");
        body.Should().NotContain(CallerSubject)
            .And.NotContain("prompt-secret")
            .And.NotContain("argument-secret")
            .And.NotContain("result-secret")
            .And.NotContain("action-params-secret");
    }

    [Fact]
    public async Task QueryChatActivity_MapsTypedFiltersAndUsesChatDefaultTake()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var hasher = new RecordingHasher();
        var http = BuildHttpContext(CallerScope, "token", queryPort, subject: CallerSubject);

        var result = await AuditTrailEndpoints.QueryChatActivity(
            http,
            BuildEndpointDependencies(queryPort, hasher),
            NullLoggerFactory.Instance,
            cursor: " cursor-alpha ",
            from: DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            to: DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
            surface: " workflow_chat ",
            conversationId: " conversation-alpha ",
            outcome: " failed ");
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        var query = queryPort.Queries.Should().ContainSingle().Which;
        query.Cursor.Should().Be("cursor-alpha");
        query.ChatSurface.Should().Be(AuditChatSurface.WorkflowChat);
        query.ChatConversationId.Should().Be("conversation-alpha");
        query.TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
        query.Take.Should().Be(50);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("auditActorId")]
    [InlineData("identityKeyId")]
    [InlineData("surface")]
    [InlineData("outcome")]
    public async Task QueryChatActivity_InvalidOrPersonalOnlyFiltersFailBeforeQuery(string filter)
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var hasher = new RecordingHasher();
        var http = BuildHttpContext(CallerScope, "token", queryPort, subject: CallerSubject);

        var result = await AuditTrailEndpoints.QueryChatActivity(
            http,
            BuildEndpointDependencies(queryPort, hasher),
            NullLoggerFactory.Instance,
            scope: filter == "scope" ? CallerScope : null,
            auditActorId: filter == "auditActorId" ? "actor-other" : null,
            identityKeyId: filter == "identityKeyId" ? "key-1" : null,
            surface: filter == "surface" ? "unknown" : null,
            outcome: filter == "outcome" ? "unknown" : null);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status400BadRequest);
        queryPort.Queries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing_scope")]
    [InlineData("missing_subject")]
    [InlineData("conflicting_subject")]
    public async Task QueryChatActivity_MissingOrAmbiguousIdentityFailsBeforeQuery(string failure)
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var claims = new List<Claim>();
        if (failure != "missing_scope")
            claims.Add(new Claim("scope_id", CallerScope));
        if (failure != "missing_subject")
            claims.Add(new Claim("uid", CallerSubject));
        if (failure == "conflicting_subject")
            claims.Add(new Claim("sub", "user-audit-beta"));
        var http = BuildHttpContext(null, "token", queryPort, scopeClaims: claims);

        var result = await AuditTrailEndpoints.QueryChatActivity(
            http,
            BuildEndpointDependencies(queryPort, new RecordingHasher()),
            NullLoggerFactory.Instance);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        queryPort.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryChatActivity_AdminAllModeRequiresExplicitSelection()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, "token", queryPort, authorizer, subject: CallerSubject);

        var result = await AuditTrailEndpoints.QueryChatActivity(
            http,
            BuildEndpointDependencies(queryPort, authorizer: authorizer),
            NullLoggerFactory.Instance,
            scope: "__all__",
            auditActorId: " actor-key-exact ");
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        var query = queryPort.Queries.Should().ContainSingle().Which;
        query.ScopeId.Should().BeNull();
        query.AuditActorIds.Should().BeNull();
        query.AuditActorId.Should().Be("actor-key-exact");
        query.RequireChatProvenance.Should().BeTrue();
        authorizer.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false, true, StatusCodes.Status403Forbidden, "SCOPE_ACCESS_DENIED")]
    [InlineData(true, false, StatusCodes.Status503ServiceUnavailable, "AUDIT_ADMIN_AUTH_UNAVAILABLE")]
    public async Task QueryChatActivity_AdminAllModeFailsClosed(
        bool elevated,
        bool includeAuthorizer,
        int expectedStatus,
        string expectedCode)
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var authorizer = new FakeAuthorizer(elevated);
        var http = BuildHttpContext(CallerScope, "token", queryPort, authorizer, subject: CallerSubject);

        var result = await AuditTrailEndpoints.QueryChatActivity(
            http,
            BuildEndpointDependencies(queryPort, authorizer: includeAuthorizer ? authorizer : null),
            NullLoggerFactory.Instance,
            scope: "__all__");
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(expectedStatus);
        body.Should().Contain(expectedCode);
        queryPort.Queries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("hasher", "AUDIT_ACTOR_HASHER_UNAVAILABLE")]
    [InlineData("query", "AUDIT_QUERY_UNAVAILABLE")]
    [InlineData("throw", "AUDIT_QUERY_UNAVAILABLE")]
    public async Task QueryChatActivity_UnavailableDependencyReturnsSanitizedServiceUnavailable(
        string dependency,
        string expectedCode)
    {
        IAuditTrailQueryPort? queryPort = dependency switch
        {
            "query" => null,
            "throw" => new ThrowingAuditTrailQueryPort("https://elastic-secret.example bearer secret"),
            _ => new RecordingAuditTrailQueryPort(),
        };
        var hasher = dependency == "hasher" ? null : new RecordingHasher();
        var http = BuildHttpContext(CallerScope, "token", queryPort, subject: CallerSubject);

        var result = await AuditTrailEndpoints.QueryChatActivity(
            http,
            BuildEndpointDependencies(queryPort, hasher),
            NullLoggerFactory.Instance);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain(expectedCode).And.NotContain("elastic-secret");
    }

    [Theory]
    [InlineData(AuditToolExecutionPhase.Running, "running")]
    [InlineData(AuditToolExecutionPhase.WaitingApproval, "waiting_approval")]
    [InlineData(AuditToolExecutionPhase.Terminal, "terminal")]
    [InlineData(AuditToolExecutionPhase.Unspecified, "unspecified")]
    public void ToRecordResponse_ToolExecutionPhase_ShouldExposeTypedSafeDetails(
        AuditToolExecutionPhase executionPhase,
        string expectedPhase)
    {
        var response = AuditTrailResponseMapper.ToRecordResponse(new AuditRecord
        {
            AuditId = "audit-tool",
            EventKind = "tool.execute",
            Subject = "tool/test_tool",
            Source = "urn:aevatar:audit:tool-execution",
            SchemaVersion = "1.0",
            OperationName = "test_tool",
            ToolExecution = new AuditToolExecution
            {
                ArgumentsSha256 = new string('a', 64),
                ExecutionPhase = executionPhase,
                IsMutation = true,
            },
        });

        response.ToolExecution.Should().BeEquivalentTo(new AuditToolExecutionResponse(
            new string('a', 64),
            expectedPhase,
            IsMutation: true));
    }

    [Fact]
    public async Task QueryAuditTrail_WhenScopeOmitted_UsesCallerScopeWithoutAdminAuthorization()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort, authorizer);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort, authorizer: authorizer),
            NullLoggerFactory.Instance,
            scope: null,
            auditActorId: " audit_actor:abc ",
            take: 999);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        var query = queryPort.Queries.Should().ContainSingle().Which;
        query.ScopeId.Should().Be(CallerScope);
        query.AuditActorId.Should().Be("audit_actor:abc");
        query.IdentityKeyId.Should().BeNull();
        query.OccurredFrom.Should().BeNull();
        query.OccurredTo.Should().BeNull();
        query.Take.Should().Be(500);
        authorizer.Calls.Should().Be(0);
        body.Should().Contain("coverage").And.Contain("ingestionWatermark").And.Contain("identityKeyId");
        body.Should().Contain("continuationCursor").And.Contain("lifecyclePhase").And.Contain("terminalOutcome");
        using var json = JsonDocument.Parse(body);
        var chat = json.RootElement.GetProperty("records")[0]
            .GetProperty("provenance").GetProperty("chat");
        chat.GetProperty("surface").GetString().Should().Be("nyxid_assistant");
        chat.GetProperty("conversationId").GetString().Should().Be("conversation-alpha");
        chat.GetProperty("turnId").GetString().Should().Be("turn-alpha");
        chat.GetProperty("taskId").GetString().Should().Be("task-alpha");
        chat.GetProperty("stepId").GetString().Should().Be("step-alpha");
        chat.GetProperty("actionRequestId").GetString().Should().Be("action-alpha");
        body.Should().NotContain("ownerSubject")
            .And.NotContain("prompt-secret")
            .And.NotContain("argument-secret")
            .And.NotContain("result-secret")
            .And.NotContain("action-params-secret");
    }

    [Fact]
    public async Task QueryAuditTrail_WhenNoRecordsMatch_ReturnsOkWithEmptyRecords()
    {
        var queryPort = new EmptyAuditTrailQueryPort();
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort),
            NullLoggerFactory.Instance,
            from: DateTimeOffset.Parse("2100-01-01T00:00:00Z"),
            to: DateTimeOffset.Parse("2100-01-02T00:00:00Z"),
            take: 1);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("records").GetArrayLength().Should().Be(0);
        queryPort.Queries.Should().ContainSingle().Which.ScopeId.Should().Be(CallerScope);
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCallerScopeMissing_ReturnsUnauthorizedBeforeQuery()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var http = BuildHttpContext(scopeClaim: null, bearer: "token", queryPort);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort),
            NullLoggerFactory.Instance);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        queryPort.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCallerScopeAmbiguous_ReturnsUnauthorizedBeforeQuery()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var http = BuildHttpContext(
            scopeClaim: null,
            bearer: "token",
            queryPort,
            scopeClaims:
            [
                new Claim("scope_id", CallerScope),
                new Claim("workflow.scope_id", OtherScope),
            ]);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort),
            NullLoggerFactory.Instance);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        queryPort.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCrossScopeAndNonAdmin_DeniesBeforeQuery()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort, authorizer);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort, authorizer: authorizer),
            NullLoggerFactory.Instance,
            scope: OtherScope);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status403Forbidden);
        queryPort.Queries.Should().BeEmpty();
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCrossScopeAndNonAdmin_DeniesBeforeQueryPortAvailability()
    {
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null, authorizer);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort: null, authorizer: authorizer),
            NullLoggerFactory.Instance,
            scope: OtherScope);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status403Forbidden);
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCrossScopeAndMissingBearer_ReturnsUnauthorizedBeforeAuthorizer()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: null, queryPort, authorizer);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort, authorizer: authorizer),
            NullLoggerFactory.Instance,
            scope: OtherScope);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        queryPort.Queries.Should().BeEmpty();
        authorizer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCrossScopeAndAdminAuthorizerMissing_ReturnsUnavailableBeforeQuery()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort),
            NullLoggerFactory.Instance,
            scope: OtherScope);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("AUDIT_ADMIN_AUTH_UNAVAILABLE");
        queryPort.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCrossScopeAndAdmin_ReadsTargetScope()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort, authorizer);
        using var loggerProvider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort, authorizer: authorizer),
            loggerFactory,
            scope: OtherScope,
            take: 10);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        var query = queryPort.Queries.Should().ContainSingle().Which;
        query.ScopeId.Should().Be(OtherScope);
        query.AuditActorId.Should().BeNull();
        query.Take.Should().Be(10);
        authorizer.Calls.Should().Be(1);
        loggerProvider.Messages.Should().Contain(message => message.Contains("admin-1", StringComparison.Ordinal));
        loggerProvider.Messages.Should().NotContain(message =>
            message.Contains("admin@example.test", StringComparison.Ordinal) ||
            message.Contains("adminEmail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryAuditTrail_WhenAllScopesAndAdmin_FiltersByNullScope()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort, authorizer);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort, authorizer: authorizer),
            NullLoggerFactory.Instance,
            scope: "__all__",
            take: 10);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        // The store matches ScopeId == null as "any scope"; the wildcard must not leak the literal
        // "__all__" token into the query.
        var query = queryPort.Queries.Should().ContainSingle().Which;
        query.ScopeId.Should().BeNull();
        query.Take.Should().Be(10);
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task QueryAuditTrail_WhenAllScopesAndNonAdmin_DeniesBeforeQuery()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort, authorizer);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort, authorizer: authorizer),
            NullLoggerFactory.Instance,
            scope: "__all__");
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status403Forbidden);
        queryPort.Queries.Should().BeEmpty();
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task QueryAuditTrail_WhenFiltersProvided_PreservesQueryFilters()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort);
        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var to = DateTimeOffset.Parse("2026-01-31T23:59:59Z");

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort),
            NullLoggerFactory.Instance,
            auditActorId: " audit_actor:abc ",
            identityKeyId: " key-1 ",
            cursor: " cursor-1 ",
            from: from,
            to: to,
            take: 25,
            commandId: " command-1 ",
            workflowRunId: " run-1 ",
            lifecyclePhase: AuditLifecyclePhase.Terminal,
            terminalOutcome: AuditTerminalOutcome.Succeeded,
            correlationId: " correlation-1 ");
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        var query = queryPort.Queries.Should().ContainSingle().Which;
        query.ScopeId.Should().Be(CallerScope);
        query.AuditActorId.Should().Be("audit_actor:abc");
        query.IdentityKeyId.Should().Be("key-1");
        query.Cursor.Should().Be("cursor-1");
        query.OccurredFrom.Should().Be(from);
        query.OccurredTo.Should().Be(to);
        query.CommandId.Should().Be("command-1");
        query.WorkflowRunId.Should().Be("run-1");
        query.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        query.TerminalOutcome.Should().Be(AuditTerminalOutcome.Succeeded);
        query.CorrelationId.Should().Be("correlation-1");
        query.Take.Should().Be(25);
    }

    [Fact]
    public async Task ExportAuditTrailCloudEvents_ShouldReturnCloudEvents10BatchWithStableAuditData()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort);

        var result = await AuditTrailEndpoints.ExportAuditTrailCloudEvents(
            http,
            BuildEndpointDependencies(queryPort),
            NullLoggerFactory.Instance,
            commandId: "command-1",
            lifecyclePhase: AuditLifecyclePhase.Terminal);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        http.Response.ContentType.Should().StartWith("application/cloudevents-batch+json");
        http.Response.Headers["Aevatar-Audit-Truncated"].ToString().Should().Be("true");
        http.Response.Headers["Aevatar-Audit-Window-Completeness"].ToString().Should().Be("unbounded");
        http.Response.Headers["Aevatar-Audit-Schema-Compatibility"].ToString().Should().Be("current");

        using var json = JsonDocument.Parse(body);
        var cloudEvent = json.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        cloudEvent.GetProperty("specversion").GetString().Should().Be("1.0");
        cloudEvent.GetProperty("id").GetString().Should().Be("audit-1");
        cloudEvent.GetProperty("source").GetString().Should().Be("urn:aevatar:audit:projection-artifact");
        cloudEvent.GetProperty("type").GetString().Should().Be("workflow.run.completed");
        cloudEvent.GetProperty("subject").GetString().Should().Be("workflow_run/run-1");
        cloudEvent.GetProperty("dataschema").GetString().Should().Be("https://schemas.aevatar.ai/audit/1.0");
        cloudEvent.GetProperty("datacontenttype").GetString().Should().Be("application/json");
        cloudEvent.GetProperty("traceparent").GetString()
            .Should().Be("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
        cloudEvent.GetProperty("correlationid").GetString().Should().Be("correlation-1");
        cloudEvent.GetProperty("data").GetProperty("lifecyclePhase").GetString().Should().Be("terminal");
        cloudEvent.GetProperty("data").GetProperty("terminalOutcome").GetString().Should().Be("succeeded");
        Uri.TryCreate(cloudEvent.GetProperty("source").GetString(), UriKind.Absolute, out _).Should().BeTrue();
        Uri.TryCreate(cloudEvent.GetProperty("dataschema").GetString(), UriKind.Absolute, out _).Should().BeTrue();
    }

    [Fact]
    public async Task QueryAuditTrail_WhenRecordUsesLegacyContract_ReportsExplicitCompatibilityProjection()
    {
        var queryPort = new RecordingAuditTrailQueryPort { ReturnLegacyRecord = true };
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort),
            NullLoggerFactory.Instance);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.GetProperty("coverage").GetProperty("schemaCompatibility").GetString()
            .Should().Be("contains_legacy_records");
        var record = root.GetProperty("records").EnumerateArray().Should().ContainSingle().Subject;
        record.GetProperty("schemaVersion").GetString().Should().Be("legacy-v0");
        record.GetProperty("schemaCompatibility").GetString().Should().Be("legacy_mapped");
        record.GetProperty("eventKind").GetString().Should().Be("READ");
        record.GetProperty("source").GetString().Should().Be("urn:aevatar:audit:legacy");
        record.GetProperty("lifecyclePhase").GetString().Should().Be("terminal");
        record.GetProperty("terminalOutcome").GetString().Should().Be("succeeded");
    }

    [Fact]
    public async Task QueryAuditTrail_WhenLegacyFailureContainsFreeText_DoesNotExposeUntrustedFields()
    {
        var queryPort = new RecordingAuditTrailQueryPort
        {
            ReturnLegacyRecord = true,
            ReturnLegacyFailure = true,
        };
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort),
            NullLoggerFactory.Instance);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        body.Should().NotContain("legacy-sensitive");
        using var json = JsonDocument.Parse(body);
        var record = json.RootElement.GetProperty("records").EnumerateArray().Should().ContainSingle().Subject;
        record.TryGetProperty("annotations", out _).Should().BeFalse();
        record.GetProperty("provenance").ValueKind.Should().Be(JsonValueKind.Null);
        record.GetProperty("committedFact").ValueKind.Should().Be(JsonValueKind.Null);
        record.GetProperty("failure").GetProperty("code").GetString().Should().Be("legacy_failure");
        record.GetProperty("failure").GetProperty("sanitizedMessage").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task QueryAuditTrail_WhenQueryPortMissing_ReturnsServiceUnavailable()
    {
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort: null),
            NullLoggerFactory.Instance);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("AUDIT_QUERY_UNAVAILABLE");
    }

    [Fact]
    public async Task QueryAuditTrail_WhenStoreThrows_ReturnsSanitizedServiceUnavailable()
    {
        var queryPort = new ThrowingAuditTrailQueryPort(
            "https://elastic-secret.example:9200 Bearer secret-token raw-backend-detail");
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort);
        using var loggerProvider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort),
            loggerFactory);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("code").GetString().Should().Be("AUDIT_QUERY_UNAVAILABLE");
        body.Should().NotContain("elastic-secret").And.NotContain("secret-token").And.NotContain("raw-backend-detail");
        loggerProvider.Messages.Should().ContainSingle(message =>
            message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        loggerProvider.Messages.Should().NotContain(message =>
            message.Contains("elastic-secret", StringComparison.Ordinal) ||
            message.Contains("secret-token", StringComparison.Ordinal) ||
            message.Contains("raw-backend-detail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveAuditActor_WhenCallerScopeMissing_ReturnsUnauthorizedBeforeAuthorization()
    {
        var hasher = new RecordingHasher();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(scopeClaim: null, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: hasher, authorizer: authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", "user@example.test"));
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        authorizer.Calls.Should().Be(0);
        hasher.CanonicalActorKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAuditActor_WhenCallerScopeAmbiguous_ReturnsUnauthorizedBeforeAuthorization()
    {
        var hasher = new RecordingHasher();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(
            scopeClaim: null,
            bearer: "token",
            queryPort: null,
            scopeClaims:
            [
                new Claim("scope_id", CallerScope),
                new Claim("workflow.scope_id", OtherScope),
            ]);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: hasher, authorizer: authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", "user@example.test"));
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        authorizer.Calls.Should().Be(0);
        hasher.CanonicalActorKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAuditActor_WhenNonAdmin_DeniesWithoutHashing()
    {
        var hasher = new RecordingHasher();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: hasher, authorizer: authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", "user@example.test"));
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status403Forbidden);
        hasher.CanonicalActorKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAuditActor_WhenAdminAuthorizerMissing_ReturnsUnavailableBeforeHashing()
    {
        var hasher = new RecordingHasher();
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: hasher, authorizer: null),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", "user@example.test"));
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("AUDIT_ADMIN_AUTH_UNAVAILABLE");
        hasher.CanonicalActorKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAuditActor_WhenHasherMissing_ReturnsUnavailableAfterAdminAuthorization()
    {
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: null, authorizer: authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", "user@example.test"));
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("AUDIT_ACTOR_HASHER_UNAVAILABLE");
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAuditActor_WhenAdmin_ReturnsOnlyAuditIdentity()
    {
        var hasher = new RecordingHasher
        {
            Identity = new AuditActorIdentity("audit_actor:hash", "key-1"),
        };
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: hasher, authorizer: authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest(" nyxid ", "user@example.test"));
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        hasher.CanonicalActorKeys.Should().ContainSingle().Which.Should().Be("nyxid:user@example.test");
        body.Should().Contain("audit_actor:hash");
        body.Should().Contain("key-1");
        body.Should().NotContain("user@example.test");
        body.Should().NotContain("nyxid");
    }

    [Fact]
    public async Task ResolveAuditActor_WhenBodyMissingIdentity_ReturnsBadRequest()
    {
        var hasher = new RecordingHasher();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: hasher, authorizer: authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", " "));
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status400BadRequest);
        hasher.CanonicalActorKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAuditActor_WhenIdentityContainsColon_ReturnsBadRequestBeforeHashing()
    {
        var hasher = new RecordingHasher();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: hasher, authorizer: authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", "scope:user"));
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status400BadRequest);
        hasher.CanonicalActorKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task AddAuditTrailCapabilityBundle_ShouldMapRoutesAndAdminMetadata()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.AddAuditTrailCapabilityBundle();

        await using var app = builder.Build();
        app.MapAevatarCapabilities();

        var routeEndpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        routeEndpoints.Select(static endpoint => endpoint.RoutePattern.RawText)
            .Should()
            .Contain(["/api/audit/trail", "/api/audit/chat-activity", "/api/audit/trail/cloudevents", "/api/audit/actor-resolutions"]);
        routeEndpoints.Single(static endpoint => endpoint.RoutePattern.RawText == "/api/audit/trail")
            .Metadata
            .GetMetadata<AuditTrailEndpointAuditMetadata>()
            .Should()
            .BeEquivalentTo(new AuditTrailEndpointAuditMetadata("audit-trail", "query-cross-scope", "ADMIN"));
        routeEndpoints.Single(static endpoint => endpoint.RoutePattern.RawText == "/api/audit/trail/cloudevents")
            .Metadata
            .GetMetadata<AuditTrailEndpointAuditMetadata>()
            .Should()
            .BeEquivalentTo(new AuditTrailEndpointAuditMetadata("audit-trail", "export-cross-scope", "ADMIN"));
        routeEndpoints.Single(static endpoint => endpoint.RoutePattern.RawText == "/api/audit/actor-resolutions")
            .Metadata
            .GetMetadata<AuditTrailEndpointAuditMetadata>()
            .Should()
            .BeEquivalentTo(new AuditTrailEndpointAuditMetadata("audit-trail", "resolve-actor", "ADMIN"));
    }

    [Fact]
    public async Task AddAuditTrailCapabilityBundle_WhenQueryPortMissing_ReportsDegradedHealthContributor()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.AddAuditTrailCapabilityBundle();

        await using var app = builder.Build();

        var contributor = app.Services.GetServices<AevatarHealthContributorRegistration>()
            .Single(static registration => registration.Name == "audit-trail");
        var result = await contributor.ProbeAsync!(app.Services, CancellationToken.None);

        result.Status.Should().Be(AevatarHealthStatuses.Degraded);
        result.Message.Should().Be("Audit trail query port is not configured.");
    }

    [Fact]
    public async Task AddAuditTrailCapabilityBundle_WhenQueryFails_ReportsUnhealthyReadinessWithoutSensitiveDetail()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IAuditTrailQueryPort>(
            new ThrowingAuditTrailQueryPort("https://elastic-secret.example:9200 password=secret"));
        builder.AddAuditTrailCapabilityBundle();

        await using var app = builder.Build();

        var contributor = app.Services.GetServices<AevatarHealthContributorRegistration>()
            .Single(static registration => registration.Name == "audit-trail");
        var result = await contributor.ProbeAsync!(app.Services, CancellationToken.None);

        result.Status.Should().Be(AevatarHealthStatuses.Unhealthy);
        result.Message.Should().Be("Audit trail query/index is unavailable.");
        result.Details.Values.Should().NotContain(value =>
            value.Contains("elastic-secret", StringComparison.Ordinal) ||
            value.Contains("password=secret", StringComparison.Ordinal));
    }

    private static DefaultHttpContext BuildHttpContext(
        string? scopeClaim,
        string? bearer,
        IAuditTrailQueryPort? queryPort,
        IPlatformAdminAuthorizer? authorizer = null,
        IReadOnlyCollection<Claim>? scopeClaims = null,
        string? subject = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = BuildServiceProvider(queryPort, hasher: null, authorizer),
        };
        var claims = (scopeClaims ?? BuildScopeClaims(scopeClaim)).ToList();
        if (subject is not null)
            claims.Add(new Claim("uid", subject));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        if (bearer is not null)
            context.Request.Headers.Authorization = $"Bearer {bearer}";

        return context;
    }

    private static Claim[] BuildScopeClaims(string? scopeClaim) =>
        scopeClaim is null ? [] : [new Claim("scope_id", scopeClaim)];

    private static AuditTrailEndpointDependencies BuildEndpointDependencies(
        IAuditTrailQueryPort? queryPort = null,
        IAuditActorIdentityHasher? hasher = null,
        IPlatformAdminAuthorizer? authorizer = null) =>
        new(
            queryPort is null ? [] : [queryPort],
            authorizer is null ? [] : [authorizer],
            hasher is null ? [] : [hasher]);

    private static IServiceProvider BuildServiceProvider(
        IAuditTrailQueryPort? queryPort,
        IAuditActorIdentityHasher? hasher,
        IPlatformAdminAuthorizer? authorizer)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        if (queryPort is not null)
            services.AddSingleton(queryPort);
        if (hasher is not null)
            services.AddSingleton(hasher);
        if (authorizer is not null)
            services.AddSingleton(authorizer);

        return services.BuildServiceProvider();
    }

    private static async Task<int> ExecuteAsync(IResult result, HttpContext http)
    {
        var (status, _) = await ExecuteWithBodyAsync(result, http);
        return status;
    }

    private static async Task<(int Status, string Body)> ExecuteWithBodyAsync(IResult result, HttpContext http)
    {
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body);
        var body = await reader.ReadToEndAsync();
        return (http.Response.StatusCode, body);
    }

    private sealed class RecordingAuditTrailQueryPort : IAuditTrailQueryPort
    {
        public List<AuditTrailQuery> Queries { get; } = [];

        public bool ReturnLegacyRecord { get; init; }

        public bool ReturnLegacyFailure { get; init; }

        public Task<AuditTrailPage> QueryAsync(
            AuditTrailQuery query,
            CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            var record = new AuditRecord
            {
                AuditId = "audit-1",
                EventKind = "workflow.run.completed",
                Subject = "workflow_run/run-1",
                Source = "urn:aevatar:audit:projection-artifact",
                SchemaVersion = "1.0",
                ScopeId = query.ScopeId ?? "scope-from-store",
                AuditActorId = query.AuditActorId ?? "audit_actor:default",
                IdentityKeyId = "key-1",
                OperationName = "READ",
                Outcome = AuditOutcome.Success,
                LifecyclePhase = AuditLifecyclePhase.Terminal,
                TerminalOutcome = AuditTerminalOutcome.Succeeded,
                OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-02T03:04:05Z")),
                RecordedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-02T03:04:06Z")),
                Target = new AuditTarget { Kind = "workflow", Id = "wf-1" },
                Correlation = new AuditCorrelation
                {
                    TraceId = "0123456789abcdef0123456789abcdef",
                    SpanId = "0123456789abcdef",
                    Traceparent = "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01",
                    RequestId = "request-1",
                    CommandId = "command-1",
                    WorkflowRunId = "run-1",
                    CorrelationId = "correlation-1",
                    CausationId = "event-0",
                },
                Provenance = new AuditExecutionProvenance
                {
                    ScopeId = query.ScopeId ?? "scope-from-store",
                    RunId = "run-1",
                    CorrelationId = "correlation-1",
                    Chat = new AuditChatProvenance
                    {
                        Surface = AuditChatSurface.NyxidAssistant,
                        ConversationId = "conversation-alpha",
                        TurnId = "turn-alpha",
                        TaskId = "task-alpha",
                        StepId = "step-alpha",
                        ActionRequestId = "action-alpha",
                    },
                },
                Redaction = new AuditRedaction
                {
                    Policy = "aevatar.audit.safe-fields.v1",
                    ValuesSanitized = true,
                },
            };
            if (ReturnLegacyRecord)
            {
                record.EventKind = string.Empty;
                record.Subject = string.Empty;
                record.Source = string.Empty;
                record.SchemaVersion = string.Empty;
                record.LifecyclePhase = AuditLifecyclePhase.Unspecified;
                record.TerminalOutcome = AuditTerminalOutcome.Unspecified;
                record.RecordedAt = null;
                record.CapturePlane = AuditCapturePlane.Unspecified;
                record.RequestSummary = "legacy-sensitive-request";
                record.ResultSummary = "legacy-sensitive-result";
                record.ErrorSummary = "Bearer legacy-sensitive-token";
                record.Annotations["raw_body"] = "legacy-sensitive-body";
                record.Provenance.ActorId = "legacy-sensitive-external-subject";
                record.CommittedFactRef = new AuditCommittedFactReference
                {
                    CommittedEventId = "legacy-event-1",
                    ActorId = "legacy-sensitive-external-subject",
                    StateVersion = 1,
                };
                if (ReturnLegacyFailure)
                {
                    record.Outcome = AuditOutcome.Error;
                    record.ErrorCode = "legacy-sensitive-error-code";
                }
            }

            return Task.FromResult(new AuditTrailPage(
                [record],
                "cursor-2",
                DateTimeOffset.Parse("2026-01-02T03:04:07Z"),
                AuditQueryCoverage.Create(
                    query,
                    truncated: true,
                    ingestionWatermark: DateTimeOffset.Parse("2026-01-02T03:04:06Z"),
                    completeThrough: null,
                    schemaCompatibility: ReturnLegacyRecord
                        ? AuditSchemaCompatibility.ContainsLegacyRecords
                        : AuditSchemaCompatibility.Current)));
        }
    }

    private sealed class ThrowingAuditTrailQueryPort(string message) : IAuditTrailQueryPort
    {
        public Task<AuditTrailPage> QueryAsync(
            AuditTrailQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AuditTrailPage>(new InvalidOperationException(message));
    }

    private sealed class EmptyAuditTrailQueryPort : IAuditTrailQueryPort
    {
        public List<AuditTrailQuery> Queries { get; } = [];

        public Task<AuditTrailPage> QueryAsync(
            AuditTrailQuery query,
            CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult(new AuditTrailPage(
                [],
                null,
                DateTimeOffset.UtcNow,
                AuditQueryCoverage.Create(
                    query,
                    truncated: false,
                    ingestionWatermark: null,
                    completeThrough: null,
                    schemaCompatibility: AuditSchemaCompatibility.Current)));
        }
    }

    private sealed class RecordingHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Identity { get; init; } = new("audit_actor:test", "key-test");

        public IReadOnlyList<AuditActorIdentity>? Identities { get; init; }

        public List<string> CanonicalActorKeys { get; } = [];

        public AuditActorIdentity Hash(string canonicalActorKey)
        {
            CanonicalActorKeys.Add(canonicalActorKey);
            return Identity;
        }

        public IReadOnlyList<AuditActorIdentity> HashAll(string canonicalActorKey)
        {
            CanonicalActorKeys.Add(canonicalActorKey);
            return Identities ?? [Identity];
        }

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeAuthorizer : IPlatformAdminAuthorizer
    {
        private readonly bool _elevated;

        public FakeAuthorizer(bool elevated)
        {
            _elevated = elevated;
        }

        public int Calls { get; private set; }

        public Task<PlatformCaller> ResolveCallerAsync(string bearerToken, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_elevated
                ? new PlatformCaller(true, "admin", "admin@example.test", "admin-1")
                : PlatformCaller.NotElevated);
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Add(formatter(state, exception));
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Audit.Hosting.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
