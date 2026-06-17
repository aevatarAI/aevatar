using System.Security.Cryptography;
using System.Text;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
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
        var replay = new RecordingReplayStore();
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { Result = null };
        var http = CreateHttpContext(replay);
        var body = Body("APPROVED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
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
        var replay = new RecordingReplayStore();
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { ThrowOnLookup = true };
        var http = CreateHttpContext(replay);
        var body = Body("APPROVED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
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
        var replay = new RecordingReplayStore();
        var dispatch = new RecordingSignalDispatch
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt("actor-run-1", "run-1", "accepted-cmd", "accepted-corr")),
        };
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext(replay);
        var body = Body(rawStatus);
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentType = "application/json";
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var responseBody = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        responseBody.Should().Contain(normalizedStatus);
        lookup.Requests.Should().ContainSingle().Subject.Should().Be(("nyxid", "instance_code", "app-42"));
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
        command.ExternalApproval!.TerminalStatus.Should().Be(normalizedStatus);
        command.ExternalApproval.ProviderDeliveryEvidence["delivery_id"].Should().Be("delivery-1");
        command.ExternalApproval.CallbackIdempotencyKey.Should().Be("idem-42");
    }

    [Fact]
    public async Task HandleAsync_WhenDuplicateSameTerminal_ShouldReturnAcceptedWithoutRedispatch()
    {
        var replay = new RecordingReplayStore
        {
            Result = new WorkflowWebhookReplayAdmission(
                WorkflowWebhookReplayAdmissionStatus.DuplicateInProgress,
                ExistingCommandId: "cmd-existing",
                ExistingCorrelationId: "corr-existing"),
        };
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext(replay);
        var body = Body("APPROVED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
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
        var replay = new RecordingReplayStore
        {
            Result = new WorkflowWebhookReplayAdmission(WorkflowWebhookReplayAdmissionStatus.PayloadConflict),
        };
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext(replay);
        var body = Body("REJECTED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
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
        var replay = new RecordingReplayStore { ThrowOnAdmit = true };
        var dispatch = new RecordingSignalDispatch();
        var lookup = new RecordingLookupPort { Result = ActiveContinuation() };
        var http = CreateHttpContext(replay);
        var body = Body("APPROVED");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowExternalApprovalCallbackEndpoints.HandleAsync(
            http,
            "nyx",
            lookup,
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        http.Response.Headers.RetryAfter.ToString().Should().Be("7");
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

    private static DefaultHttpContext CreateHttpContext(IWorkflowWebhookReplayStore replayStore)
    {
        var services = new ServiceCollection();
        services.AddSingleton(replayStore);
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static void Sign(HttpContext http, string secret, byte[] body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signaturePayload = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        http.Request.Headers["X-Aevatar-Timestamp"] = timestamp;
        http.Request.Headers["X-Aevatar-Signature"] = "sha256=" + Convert.ToHexString(hmac.ComputeHash(signaturePayload)).ToLowerInvariant();
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

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowSignalCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingReplayStore : IWorkflowWebhookReplayStore
    {
        public List<WorkflowWebhookReplayAdmissionRequest> Requests { get; } = [];

        public List<WorkflowWebhookReplayAdmissionRequest> Released { get; } = [];

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
