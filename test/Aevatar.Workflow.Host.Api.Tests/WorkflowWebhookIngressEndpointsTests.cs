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
        replay.Requests[0].CommandId.Should().MatchRegex("^webhook:[0-9a-f]{64}$");
        replay.Requests[0].CorrelationId.Should().Be(replay.Requests[0].CommandId);
        dispatch.Commands.Should().ContainSingle();
        var command = dispatch.Commands[0];
        command.Source.WorkflowName.Should().Be("invoice-flow");
        command.Prompt.Should().Be("""{"message":"Webhook says invoice ready"}""");
        command.CommandIdSeed.Should().Be(replay.Requests[0].CommandId);
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
    public async Task RequestBuilder_ShouldRejectDeliveryHeaderThatDiffersFromSignedBody()
    {
        var options = CreateOptions();
        options.Bindings[0].DeliveryIdHeader = "X-Delivery-Id";
        var http = CreateHttpContext(new RecordingReplayStore());
        var body = Encoding.UTF8.GetBytes("""{"id":"signed-id","text":"ready"}""");
        http.Request.Headers["X-Delivery-Id"] = "forged-id";
        Sign(http, "secret", body);

        var result = new WorkflowWebhookIngressRequestBuilder(Options.Create(options)).Build(
            http.Request,
            "invoice",
            body,
            DateTimeOffset.UtcNow);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("WEBHOOK_DELIVERY_ID_MISMATCH");
    }

    [Fact]
    public void RequestBuilder_ShouldUseUnambiguousStableCommandSeeds()
    {
        var firstOptions = CreateOptions();
        firstOptions.Bindings[0].RouteKey = "a:b";
        firstOptions.Bindings[0].SourceId = "c";
        var firstHttp = CreateHttpContext(new RecordingReplayStore());
        var body = Encoding.UTF8.GetBytes("""{"id":"d","text":"ready"}""");
        Sign(firstHttp, "secret", body);

        var firstBuilder = new WorkflowWebhookIngressRequestBuilder(Options.Create(firstOptions));
        var first = firstBuilder.Build(firstHttp.Request, "a:b", body, DateTimeOffset.UtcNow);
        var repeated = firstBuilder.Build(firstHttp.Request, "a:b", body, DateTimeOffset.UtcNow);

        var secondOptions = CreateOptions();
        secondOptions.Bindings[0].RouteKey = "a";
        secondOptions.Bindings[0].SourceId = "b:c";
        var secondHttp = CreateHttpContext(new RecordingReplayStore());
        Sign(secondHttp, "secret", body);
        var second = new WorkflowWebhookIngressRequestBuilder(Options.Create(secondOptions)).Build(
            secondHttp.Request,
            "a",
            body,
            DateTimeOffset.UtcNow);

        first.Succeeded.Should().BeTrue();
        repeated.Request!.CommandIdSeed.Should().Be(first.Request!.CommandIdSeed);
        second.Succeeded.Should().BeTrue();
        second.Request!.CommandIdSeed.Should().NotBe(first.Request.CommandIdSeed);
        first.Request.CommandIdSeed.Should().MatchRegex("^webhook:[0-9a-f]{64}$");
    }

    [Fact]
    public async Task RequestBuilder_ShouldJsonEscapeTemplateValues()
    {
        var options = CreateOptions();
        var http = CreateHttpContext(new RecordingReplayStore());
        var body = Encoding.UTF8.GetBytes(
            """{"id":"delivery-1","text":"quote \" slash \\ newline\nnext"}""");
        Sign(http, "secret", body);

        var result = new WorkflowWebhookIngressRequestBuilder(Options.Create(options)).Build(
            http.Request,
            "invoice",
            body,
            DateTimeOffset.UtcNow);

        result.Succeeded.Should().BeTrue();
        using var prompt = JsonDocument.Parse(result.Request!.Prompt);
        prompt.RootElement.GetProperty("message").GetString()
            .Should().Be("Webhook says quote \" slash \\ newline\nnext");
    }

    [Fact]
    public async Task RequestBuilder_ShouldRejectMissingTemplatePath()
    {
        var options = CreateOptions();
        var http = CreateHttpContext(new RecordingReplayStore());
        var body = Encoding.UTF8.GetBytes("""{"id":"delivery-1"}""");
        Sign(http, "secret", body);

        var result = new WorkflowWebhookIngressRequestBuilder(Options.Create(options)).Build(
            http.Request,
            "invoice",
            body,
            DateTimeOffset.UtcNow);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("WEBHOOK_PROMPT_PATH_MISSING");
    }

    [Fact]
    public async Task RequestBuilder_ShouldRenderRunDateInConfiguredTimeZone()
    {
        var options = CreateOptions();
        options.Bindings[0].PromptTemplate = """{"date":"{{@run_date}}"}""";
        options.Bindings[0].TimeZoneId = "Asia/Singapore";
        var http = CreateHttpContext(new RecordingReplayStore());
        var body = Encoding.UTF8.GetBytes("""{"id":"delivery-1"}""");
        var receivedAt = DateTimeOffset.Parse("2026-08-13T16:30:00Z");
        Sign(http, "secret", body, receivedAt.ToUnixTimeSeconds());

        var singapore = new WorkflowWebhookIngressRequestBuilder(Options.Create(options)).Build(
            http.Request,
            "invoice",
            body,
            receivedAt);
        singapore.Request!.Prompt.Should().Be("""{"date":"2026-08-14"}""");

        options.Bindings[0].TimeZoneId = null;
        var utc = new WorkflowWebhookIngressRequestBuilder(Options.Create(options)).Build(
            http.Request,
            "invoice",
            body,
            receivedAt);
        utc.Request!.Prompt.Should().Be("""{"date":"2026-08-13"}""");
    }

    [Fact]
    public async Task RequestBuilder_ShouldRejectTimestampOutsideUnixRange()
    {
        var options = CreateOptions();
        var http = CreateHttpContext(new RecordingReplayStore());
        var body = Encoding.UTF8.GetBytes("""{"id":"delivery-1","text":"ready"}""");
        http.Request.Headers["X-Aevatar-Timestamp"] = long.MaxValue.ToString();
        http.Request.Headers["X-Aevatar-Signature"] = "sha256=invalid";

        var result = new WorkflowWebhookIngressRequestBuilder(Options.Create(options)).Build(
            http.Request,
            "invoice",
            body,
            DateTimeOffset.UtcNow);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        result.ErrorCode.Should().Be("WEBHOOK_AUTH_INVALID");
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
            PromptTemplate = """{"message":"Webhook says {{text}}"}""",
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

    private static void Sign(
        HttpContext http,
        string secret,
        byte[] body,
        long? timestampUnixSeconds = null)
    {
        var timestamp = (timestampUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
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

        public ValueTask ReleaseAsync(
            WorkflowWebhookReplayAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Released.Add(request);
            return ValueTask.CompletedTask;
        }
    }
}
