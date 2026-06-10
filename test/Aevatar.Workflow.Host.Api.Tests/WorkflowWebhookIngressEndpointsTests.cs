using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowWebhookIngressEndpointsTests
{
    [Fact]
    public async Task HandleAsync_ShouldDispatchTypedWorkflowCommand_WhenHmacAndReplayAdmissionPass()
    {
        var dispatch = new RecordingWorkflowDispatch();
        dispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
            new WorkflowChatRunAcceptedReceipt("actor-1", "invoice-flow", "accepted-cmd", "accepted-corr"));
        var replay = new RecordingReplayStore();
        var http = CreateHttpContext(replay);
        var body = Encoding.UTF8.GetBytes("""{"id":"delivery-1","text":"invoice ready"}""");
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentType = "application/json";
        Sign(http, "secret", body);

        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "invoice",
            new WorkflowWebhookIngressRequestBuilder(Options.Create(CreateOptions())),
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var responseBody = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        responseBody.Should().Contain("accepted-cmd");
        responseBody.Should().Contain("delivery-1");
        replay.Requests.Should().ContainSingle();
        replay.Requests[0].DeliveryId.Should().Be("delivery-1");
        replay.Requests[0].CommandId.Should().Be("webhook:invoice:lark:delivery-1");
        replay.Requests[0].CorrelationId.Should().Be("webhook:invoice:lark:delivery-1");
        dispatch.Commands.Should().ContainSingle();
        var command = dispatch.Commands[0];
        command.Source.WorkflowName.Should().Be("invoice-flow");
        command.Prompt.Should().Be("Webhook says invoice ready");
        command.CommandIdSeed.Should().Be("webhook:invoice:lark:delivery-1");
        command.CorrelationIdSeed.Should().Be(command.CommandIdSeed);
        command.ExternalIngress.Should().NotBeNull();
        command.ExternalIngress!.RouteKey.Should().Be("invoice");
        command.ExternalIngress.SourceId.Should().Be("lark");
        command.ExternalIngress.DeliveryId.Should().Be("delivery-1");
        command.ExternalIngress.AuthScheme.Should().Be("hmac-sha256");
        command.ExternalIngress.ContentType.Should().Be("application/json");
        command.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_ShouldFailClosed_WhenReplayStoreMissing()
    {
        var http = CreateHttpContext(replayStore: null);
        var body = Encoding.UTF8.GetBytes("""{"id":"delivery-1","text":"invoice ready"}""");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "invoice",
            new WorkflowWebhookIngressRequestBuilder(Options.Create(CreateOptions())),
            new RecordingWorkflowDispatch(),
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var responseBody = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        responseBody.Should().Contain("WEBHOOK_REPLAY_STORE_UNAVAILABLE");
    }

    [Fact]
    public async Task HandleAsync_ShouldRejectInvalidSignatureBeforeDispatch()
    {
        var dispatch = new RecordingWorkflowDispatch();
        var replay = new RecordingReplayStore();
        var http = CreateHttpContext(replay);
        var body = Encoding.UTF8.GetBytes("""{"id":"delivery-1","text":"invoice ready"}""");
        http.Request.Body = new MemoryStream(body);
        http.Request.Headers["X-Aevatar-Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        http.Request.Headers["X-Aevatar-Signature"] = "sha256=bad";

        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "invoice",
            new WorkflowWebhookIngressRequestBuilder(Options.Create(CreateOptions())),
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var responseBody = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        responseBody.Should().Contain("WEBHOOK_AUTH_INVALID");
        dispatch.Commands.Should().BeEmpty();
        replay.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ShouldFailClosed_WhenHmacSecretMissing()
    {
        var dispatch = new RecordingWorkflowDispatch();
        var replay = new RecordingReplayStore();
        var options = CreateOptions();
        options.Bindings[0].HmacSecret = null;
        var http = CreateHttpContext(replay);
        var body = Encoding.UTF8.GetBytes("""{"id":"delivery-1","text":"invoice ready"}""");
        http.Request.Body = new MemoryStream(body);

        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "invoice",
            new WorkflowWebhookIngressRequestBuilder(Options.Create(options)),
            dispatch,
            Options.Create(options),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var responseBody = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        responseBody.Should().Contain("WEBHOOK_AUTH_CONFIG_REQUIRED");
        dispatch.Commands.Should().BeEmpty();
        replay.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ShouldRejectPayloadConflict()
    {
        var dispatch = new RecordingWorkflowDispatch();
        var replay = new RecordingReplayStore
        {
            Result = new WorkflowWebhookReplayAdmission(WorkflowWebhookReplayAdmissionStatus.PayloadConflict),
        };
        var http = CreateHttpContext(replay);
        var body = Encoding.UTF8.GetBytes("""{"id":"delivery-1","text":"invoice ready"}""");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "invoice",
            new WorkflowWebhookIngressRequestBuilder(Options.Create(CreateOptions())),
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var responseBody = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        responseBody.Should().Contain("WEBHOOK_DELIVERY_PAYLOAD_CONFLICT");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnAcceptedDuplicateWithoutDispatch()
    {
        var dispatch = new RecordingWorkflowDispatch();
        var replay = new RecordingReplayStore
        {
            Result = new WorkflowWebhookReplayAdmission(
                WorkflowWebhookReplayAdmissionStatus.DuplicateInProgress,
                ExistingCommandId: "cmd-existing",
                ExistingCorrelationId: "corr-existing"),
        };
        var http = CreateHttpContext(replay);
        var body = Encoding.UTF8.GetBytes("""{"id":"delivery-1","text":"invoice ready"}""");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "invoice",
            new WorkflowWebhookIngressRequestBuilder(Options.Create(CreateOptions())),
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var responseBody = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        responseBody.Should().Contain("DuplicateInProgress");
        responseBody.Should().Contain("cmd-existing");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnAcceptedCompletedDuplicateWithoutDispatch()
    {
        var dispatch = new RecordingWorkflowDispatch();
        var replay = new RecordingReplayStore
        {
            Result = new WorkflowWebhookReplayAdmission(
                WorkflowWebhookReplayAdmissionStatus.DuplicateCompleted,
                ExistingCommandId: "cmd-existing",
                ExistingCorrelationId: "corr-existing"),
        };
        var http = CreateHttpContext(replay);
        var body = Encoding.UTF8.GetBytes("""{"id":"delivery-1","text":"invoice ready"}""");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "invoice",
            new WorkflowWebhookIngressRequestBuilder(Options.Create(CreateOptions())),
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var responseBody = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        responseBody.Should().Contain("DuplicateCompleted");
        responseBody.Should().Contain("cmd-existing");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ShouldReleaseReplayAdmission_WhenDispatchFails()
    {
        var dispatch = new RecordingWorkflowDispatch
        {
            Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.WorkflowNotFound),
        };
        var replay = new RecordingReplayStore();
        var http = CreateHttpContext(replay);
        var body = Encoding.UTF8.GetBytes("""{"id":"delivery-1","text":"invoice ready"}""");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "invoice",
            new WorkflowWebhookIngressRequestBuilder(Options.Create(CreateOptions())),
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        replay.Released.Should().ContainSingle();
        replay.Released[0].DeliveryId.Should().Be("delivery-1");
        dispatch.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_ShouldKeepReplayAdmission_WhenDispatchIsAccepted()
    {
        var dispatch = new RecordingWorkflowDispatch();
        dispatch.Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
            new WorkflowChatRunAcceptedReceipt("actor-1", "invoice-flow", "accepted-cmd", "accepted-corr"));
        var replay = new RecordingReplayStore();
        var http = CreateHttpContext(replay);
        var body = Encoding.UTF8.GetBytes("""{"id":"delivery-1","text":"invoice ready"}""");
        http.Request.Body = new MemoryStream(body);
        Sign(http, "secret", body);

        var result = await WorkflowWebhookIngressEndpoints.HandleAsync(
            http,
            "invoice",
            new WorkflowWebhookIngressRequestBuilder(Options.Create(CreateOptions())),
            dispatch,
            Options.Create(CreateOptions()),
            NullLoggerFactory.Instance,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        replay.Released.Should().BeEmpty();
        replay.Completed.Should().ContainSingle();
        replay.Completed[0].DeliveryId.Should().Be("delivery-1");
    }

    [Fact]
    public async Task InMemoryWorkflowWebhookReplayStore_ShouldReturnCompletedDuplicateAfterCompletion()
    {
        var store = new InMemoryWorkflowWebhookReplayStore();
        var request = new WorkflowWebhookReplayAdmissionRequest(
            "invoice",
            "lark",
            "delivery-1",
            "fingerprint-1",
            DateTimeOffset.UnixEpoch,
            "cmd-1",
            "corr-1");

        var first = await store.AdmitAsync(request);
        await store.CompleteAsync(request);
        var duplicate = await store.AdmitAsync(request with { CommandId = "cmd-2", CorrelationId = "corr-2" });
        var conflict = await store.AdmitAsync(request with { PayloadFingerprint = "fingerprint-2" });

        first.Status.Should().Be(WorkflowWebhookReplayAdmissionStatus.Admitted);
        duplicate.Status.Should().Be(WorkflowWebhookReplayAdmissionStatus.DuplicateCompleted);
        duplicate.ExistingCommandId.Should().Be("cmd-1");
        duplicate.ExistingCorrelationId.Should().Be("corr-1");
        conflict.Status.Should().Be(WorkflowWebhookReplayAdmissionStatus.PayloadConflict);
    }

    private static WorkflowWebhookIngressOptions CreateOptions()
    {
        var options = new WorkflowWebhookIngressOptions
        {
            Enabled = true,
        };
        options.Bindings.Add(new WorkflowWebhookIngressBindingOptions
        {
            RouteKey = "invoice",
            SourceId = "lark",
            WorkflowName = "invoice-flow",
            ScopeId = "scope-1",
            DeliveryIdJsonPath = "id",
            PromptTemplate = "Webhook says {{text}}",
            HmacSecret = "secret",
        });
        return options;
    }

    private static DefaultHttpContext CreateHttpContext(IWorkflowWebhookReplayStore? replayStore)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        if (replayStore != null)
            services.AddSingleton(replayStore);
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

    private sealed class RecordingWorkflowDispatch
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public List<WorkflowChatRunRequest> Commands { get; } = [];

        public CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> Result { get; set; } =
            CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.WorkflowNotFound);

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingReplayStore : IWorkflowWebhookReplayStore
    {
        public List<WorkflowWebhookReplayAdmissionRequest> Requests { get; } = [];

        public List<WorkflowWebhookReplayAdmissionRequest> Completed { get; } = [];

        public List<WorkflowWebhookReplayAdmissionRequest> Released { get; } = [];

        public WorkflowWebhookReplayAdmission Result { get; set; } =
            new(WorkflowWebhookReplayAdmissionStatus.Admitted);

        public ValueTask<WorkflowWebhookReplayAdmission> AdmitAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(Result);
        }

        public ValueTask CompleteAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Completed.Add(request);
            return ValueTask.CompletedTask;
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
