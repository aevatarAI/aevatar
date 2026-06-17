using System.Security.Cryptography;
using System.Text;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExternalApprovalCallbackEndpointsTests
{
    [Fact]
    public async Task HandleAsync_WhenLookupMisses_ShouldReturnTooEarlyWithoutReplayAdmissionOrDispatch()
    {
        var replay = new RecordingReplayAdmissionPort();
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { Result = null };
        var http = CreateHttpContext();
        var body = Body("APPROVED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
            replay,
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(425);
        http.Response.Headers.RetryAfter.ToString().Should().Be("7");
        replay.Requests.Should().BeEmpty();
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenLookupFails_ShouldReturnServiceUnavailableWithoutReplayAdmissionOrDispatch()
    {
        var replay = new RecordingReplayAdmissionPort();
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { ThrowOnLookup = true };
        var http = CreateHttpContext();
        var body = Body("APPROVED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
            replay,
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        http.Response.Headers.RetryAfter.ToString().Should().Be("7");
        replay.Requests.Should().BeEmpty();
        dispatch.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData("APPROVED", "APPROVED")]
    [InlineData("REJECTED", "REJECTED")]
    [InlineData("CANCELLED", "CANCELED")]
    [InlineData("CANCELED", "CANCELED")]
    public async Task HandleAsync_WhenTerminalAccepted_ShouldDispatchOneSignalWithNormalizedPayload(
        string rawStatus,
        string normalizedStatus)
    {
        var replay = new RecordingReplayAdmissionPort();
        var dispatch = new RecordingSignalDispatch
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt("actor-run-1", "run-1", "accepted-cmd", "accepted-corr")),
        };
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext();
        var body = Body(rawStatus);
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentType = "application/json";
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
            replay,
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var responseBody = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        responseBody.Should().Contain(normalizedStatus);
        lookup.Requests.Should().ContainSingle();
        lookup.Requests.Single().Should().Be(("nyxid", "instance_code", "app-42"));
        replay.Requests.Should().ContainSingle();
        replay.Requests[0].RouteKey.Should().Be("external-approval");
        replay.Requests[0].SourceId.Should().Be("nyxid");
        replay.Requests[0].DeliveryId.Should().Be("external-approval:nyxid:instance_code:app-42");
        replay.Requests[0].PayloadFingerprint.Should().Be(normalizedStatus);
        replay.Requests[0].CommandId.Should().Be($"external-approval:nyxid:instance_code:app-42:{normalizedStatus}");
        dispatch.Commands.Should().ContainSingle();
        var command = dispatch.Commands[0];
        command.ActorId.Should().Be("actor-run-1");
        command.RunId.Should().Be("run-1");
        command.StepId.Should().Be("wait-approval");
        command.SignalName.Should().Be("approval-terminal");
        command.CommandId.Should().Be(replay.Requests[0].CommandId);
        command.CorrelationId.Should().Be(replay.Requests[0].CorrelationId);
        command.Payload.Should().Be(normalizedStatus);
        command.ExternalApproval.Should().NotBeNull();
        var externalApproval = command.ExternalApproval!;
        externalApproval.TerminalStatus.Should().Be(normalizedStatus);
        externalApproval.ProviderDeliveryEvidence.Should().NotBeNull();
        externalApproval.ProviderDeliveryEvidence!["delivery_id"].Should().Be("delivery-1");
        externalApproval.CallbackIdempotencyKey.Should().Be("idem-42");
    }

    [Fact]
    public async Task HandleAsync_WhenDuplicateSameTerminal_ShouldReturnAcceptedWithoutRedispatch()
    {
        var replay = new RecordingReplayAdmissionPort
        {
            Result = new WorkflowWebhookReplayAdmission(
                WorkflowWebhookReplayAdmissionStatus.DuplicateInProgress,
                ExistingCommandId: "cmd-existing",
                ExistingCorrelationId: "corr-existing"),
        };
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext();
        var body = Body("APPROVED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
            replay,
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var responseBody = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        responseBody.Should().Contain("cmd-existing");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenTerminalConflicts_ShouldReturnConflictWithoutDispatch()
    {
        var replay = new RecordingReplayAdmissionPort
        {
            Result = new WorkflowWebhookReplayAdmission(WorkflowWebhookReplayAdmissionStatus.PayloadConflict),
        };
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext();
        var body = Body("REJECTED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
            replay,
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenReplayAdmissionFails_ShouldReturnServiceUnavailableWithoutDispatch()
    {
        var replay = new RecordingReplayAdmissionPort { ThrowOnAdmit = true };
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext();
        var body = Body("APPROVED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
            replay,
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        http.Response.Headers.RetryAfter.ToString().Should().Be("7");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenCallbacksDisabled_ShouldReturnNotFoundWithoutLookupReplayOrDispatch()
    {
        var replay = new RecordingReplayAdmissionPort();
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext();
        var body = Body("APPROVED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);
        var options = CreateOptions();
        options.Enabled = false;

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
            replay,
            dispatch,
            Options.Create(options),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        lookup.Requests.Should().BeEmpty();
        replay.Requests.Should().BeEmpty();
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenRouteUnknown_ShouldReturnNotFoundWithoutLookupReplayOrDispatch()
    {
        var replay = new RecordingReplayAdmissionPort();
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext();
        var body = Body("APPROVED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "missing",
            lookup,
            replay,
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        lookup.Requests.Should().BeEmpty();
        replay.Requests.Should().BeEmpty();
        dispatch.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("expired")]
    public async Task HandleAsync_WhenHmacInvalid_ShouldReturnUnauthorizedWithoutLookupReplayOrDispatch(
        string authCase)
    {
        var replay = new RecordingReplayAdmissionPort();
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext();
        var body = Body("APPROVED");
        http.Request.Body = new MemoryStream(body);
        if (authCase == "invalid")
            Sign(http, "wrong-secret", body);
        if (authCase == "expired")
            Sign(http, "secret", body, DateTimeOffset.UtcNow.AddMinutes(-10));

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
            replay,
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        lookup.Requests.Should().BeEmpty();
        replay.Requests.Should().BeEmpty();
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenTerminalPayloadMalformed_ShouldReturnBadRequestWithoutLookupReplayOrDispatch()
    {
        var replay = new RecordingReplayAdmissionPort();
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext();
        var body = Encoding.UTF8.GetBytes("""{"instanceCode":"APP-42","requestId":"request-42","deliveryId":"delivery-1"}""");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
            replay,
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        lookup.Requests.Should().BeEmpty();
        replay.Requests.Should().BeEmpty();
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenReplayStoreMissing_ShouldReturnServiceUnavailableWithoutReplayOrDispatch()
    {
        var replay = new RecordingReplayAdmissionPort { IsAvailable = false };
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext();
        var body = Body("APPROVED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
            replay,
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        http.Response.Headers.RetryAfter.ToString().Should().Be("7");
        lookup.Requests.Should().ContainSingle();
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenDispatchFails_ShouldReleaseReplayAdmissionAndMapFailure()
    {
        var replay = new RecordingReplayAdmissionPort();
        var dispatch = new RecordingSignalDispatch
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.InvalidSignalName("actor-run-1", "run-1", string.Empty)),
        };
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext();
        var body = Body("APPROVED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
            replay,
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        replay.Requests.Should().ContainSingle();
        replay.Released.Should().ContainSingle();
        replay.Released.Single().Should().Be(replay.Requests[0]);
        dispatch.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_WhenDispatchThrows_ShouldReleaseReplayAdmissionAndReturnServerError()
    {
        var replay = new RecordingReplayAdmissionPort();
        var dispatch = new RecordingSignalDispatch { ThrowOnDispatch = true };
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext();
        var body = Body("APPROVED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
            replay,
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        replay.Requests.Should().ContainSingle();
        replay.Released.Should().ContainSingle();
        replay.Released.Single().Should().Be(replay.Requests[0]);
        dispatch.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task MappedEndpoint_WhenReplayStoreNotConfigured_ShouldReturnServiceUnavailable()
    {
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var dispatch = new RecordingSignalDispatch();
        await using var app = await CreateMappedAppAsync(lookup, dispatch);
        using var client = CreateClient(app);
        var body = Body("APPROVED");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/workflow-external-approvals/nyx")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        Sign(request, "secret", body);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull();
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(7));
        lookup.Requests.Should().ContainSingle();
        dispatch.Commands.Should().BeEmpty();
    }

    private static WorkflowExternalApprovalCallbackOptions CreateOptions()
    {
        var options = new WorkflowExternalApprovalCallbackOptions
        {
            Enabled = true,
            RetryAfterSeconds = 7,
        };
        options.Bindings.Add(new WorkflowExternalApprovalCallbackBindingOptions
        {
            RouteKey = "nyx",
            SourceId = "nyxid",
            ExternalIdKind = "instance_code",
            ExternalIdJsonPath = "instanceCode",
            InstanceCodeJsonPath = "instanceCode",
            RequestIdJsonPath = "requestId",
            StatusJsonPath = "status",
            DeliveryIdJsonPath = "deliveryId",
            HmacSecret = "secret",
        });
        return options;
    }

    private static WorkflowExternalApprovalContinuation ActiveContinuation() =>
        new(
            "actor-run-1",
            "run-1",
            "wait-approval",
            "approval-terminal",
            "nyxid",
            "instance_code",
            "app-42",
            "idem-42",
            "request-42",
            12,
            "evt-12",
            DateTimeOffset.Parse("2026-04-01T10:00:00Z"));

    private static byte[] Body(string status) =>
        Encoding.UTF8.GetBytes($$"""{"instanceCode":"APP-42","requestId":"request-42","status":"{{status}}","deliveryId":"delivery-1"}""");

    private static DefaultHttpContext CreateHttpContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static void Sign(HttpContext http, string secret, byte[] body, DateTimeOffset? timestamp = null)
    {
        var timestampValue = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signaturePayload = Encoding.UTF8.GetBytes(timestampValue + ".").Concat(body).ToArray();
        http.Request.Headers["X-Aevatar-Timestamp"] = timestampValue;
        http.Request.Headers["X-Aevatar-Signature"] = "sha256=" + Convert.ToHexString(hmac.ComputeHash(signaturePayload)).ToLowerInvariant();
    }

    private static void Sign(HttpRequestMessage request, string secret, byte[] body, DateTimeOffset? timestamp = null)
    {
        var timestampValue = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signaturePayload = Encoding.UTF8.GetBytes(timestampValue + ".").Concat(body).ToArray();
        request.Headers.Add("X-Aevatar-Timestamp", timestampValue);
        request.Headers.Add("X-Aevatar-Signature", "sha256=" + Convert.ToHexString(hmac.ComputeHash(signaturePayload)).ToLowerInvariant());
    }

    private static async Task<WebApplication> CreateMappedAppAsync(
        IWorkflowExternalApprovalContinuationLookupPort lookup,
        ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> dispatch)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLogging();
        builder.Services.AddOptions();
        builder.Services.AddSingleton(lookup);
        builder.Services.AddSingleton<IWorkflowWebhookReplayAdmissionPort, WorkflowWebhookReplayAdmissionPort>();
        builder.Services.AddSingleton(dispatch);
        builder.Services.Configure<WorkflowExternalApprovalCallbackOptions>(options =>
        {
            var configured = CreateOptions();
            options.Enabled = configured.Enabled;
            options.RetryAfterSeconds = configured.RetryAfterSeconds;
            options.Bindings.AddRange(configured.Bindings);
        });
        var app = builder.Build();
        WorkflowExternalApprovalCallbackEndpoints.Map(app.MapGroup("/api"));
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();

        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private static async Task<string> ReadBodyAsync(HttpResponse response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class RecordingLookupPort : IWorkflowExternalApprovalContinuationLookupPort
    {
        public List<(string SourceId, string ExternalIdKind, string ExternalId)> Requests { get; } = [];

        public WorkflowExternalApprovalContinuation? Result { get; set; }

        public bool ThrowOnLookup { get; set; }

        public Task<WorkflowExternalApprovalContinuation?> FindActiveAsync(
            string sourceId,
            string externalIdKind,
            string externalId,
            CancellationToken ct = default)
        {
            if (ThrowOnLookup)
                throw new InvalidOperationException("lookup unavailable");

            Requests.Add((sourceId, externalIdKind, externalId));
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingSignalDispatch
        : ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public List<WorkflowSignalCommand> Commands { get; } = [];

        public CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> Result { get; set; } =
            CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt("actor-run-1", "run-1", "cmd-accepted", "corr-accepted"));

        public bool ThrowOnDispatch { get; set; }

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowSignalCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            if (ThrowOnDispatch)
                throw new InvalidOperationException("dispatch unavailable");

            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingReplayAdmissionPort : IWorkflowWebhookReplayAdmissionPort
    {
        public List<WorkflowWebhookReplayAdmissionRequest> Requests { get; } = [];

        public List<WorkflowWebhookReplayAdmissionRequest> Released { get; } = [];

        public bool IsAvailable { get; init; } = true;

        public bool ThrowOnAdmit { get; set; }

        public WorkflowWebhookReplayAdmission Result { get; set; } =
            new(WorkflowWebhookReplayAdmissionStatus.Admitted);

        public ValueTask<WorkflowWebhookReplayAdmission> AdmitAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnAdmit)
                throw new InvalidOperationException("replay unavailable");

            Requests.Add(request);
            return ValueTask.FromResult(Result);
        }

        public ValueTask ReleaseAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Released.Add(request);
            return ValueTask.CompletedTask;
        }
    }
}
