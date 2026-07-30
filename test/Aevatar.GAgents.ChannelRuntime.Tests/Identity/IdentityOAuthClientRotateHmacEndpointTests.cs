using System.Text;
using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour tests for <see cref="IdentityOAuthEndpoints.HandleAevatarOAuthClientRotateHmacAsync"/>:
/// the operator disaster-recovery path that forces a fresh HMAC state-token key
/// when the vault entry behind the persisted key reference is lost. Same aevatar
/// admin gate as the client rebuild endpoint.
/// </summary>
public sealed class IdentityOAuthClientRotateHmacEndpointTests
{
    private const string AdminBearer = "admin-bearer-token";

    [Fact]
    public async Task Returns503_WhenAuthorizerMissing()
    {
        var dispatch = new RecordingCommandDispatch<RotateAevatarOAuthClientHmacKeyCommand>();
        var result = await InvokeRotateAsync(authorizer: null, bearer: AdminBearer, dispatch: dispatch);

        var (doc, statusCode) = await ReadJsonWithStatusAsync(result);
        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        doc.RootElement.GetProperty("error").GetString().Should().Be("rebuild_admin_authorizer_unavailable");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns403_WhenCallerNotElevated()
    {
        var dispatch = new RecordingCommandDispatch<RotateAevatarOAuthClientHmacKeyCommand>();
        var result = await InvokeRotateAsync(
            authorizer: new FakePlatformAdminAuthorizer(elevated: false),
            bearer: AdminBearer,
            dispatch: dispatch);

        var (doc, statusCode) = await ReadJsonWithStatusAsync(result);
        statusCode.Should().Be(StatusCodes.Status403Forbidden);
        doc.RootElement.GetProperty("error").GetString().Should().Be("rebuild_admin_required");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns403_WhenBearerMissing()
    {
        var dispatch = new RecordingCommandDispatch<RotateAevatarOAuthClientHmacKeyCommand>();
        var result = await InvokeRotateAsync(
            authorizer: new FakePlatformAdminAuthorizer(elevated: true),
            bearer: null,
            dispatch: dispatch);

        var (_, statusCode) = await ReadJsonWithStatusAsync(result);
        statusCode.Should().Be(StatusCodes.Status403Forbidden);
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchesRotateCommand_AndReturnsAccepted()
    {
        var dispatch = new RecordingCommandDispatch<RotateAevatarOAuthClientHmacKeyCommand>(
            static _ => new ChannelIdentityOAuthAcceptedReceipt(
                ActorId: AevatarOAuthClientGAgent.WellKnownId,
                CommandId: "rotate-1",
                CorrelationId: "rotate-1"));
        var result = await InvokeRotateAsync(
            authorizer: new FakePlatformAdminAuthorizer(elevated: true),
            bearer: AdminBearer,
            dispatch: dispatch,
            idempotencyKey: "rotation-request-alpha",
            expectedCurrentKid: "v1");

        var command = dispatch.Commands.Should().ContainSingle().Subject;
        command.IdempotencyKey.Should().Be("rotation-request-alpha");
        command.ExpectedCurrentKid.Should().Be("v1");

        var (doc, statusCode) = await ReadJsonWithStatusAsync(result);
        statusCode.Should().Be(StatusCodes.Status202Accepted);
        doc.RootElement.GetProperty("status").GetString().Should().Be("rotate_pending");
        doc.RootElement.GetProperty("command_id").GetString().Should().Be("rotate-1");
        doc.RootElement.GetProperty("status_url").GetString().Should().Be("/api/oauth/aevatar-client/status");
    }

    [Theory]
    [InlineData(null, "v1")]
    [InlineData("rotation-request-alpha", null)]
    public async Task Returns400_WhenRotationPreconditionIsMissing(
        string? idempotencyKey,
        string? expectedCurrentKid)
    {
        var dispatch = new RecordingCommandDispatch<RotateAevatarOAuthClientHmacKeyCommand>();

        var result = await InvokeRotateAsync(
            new FakePlatformAdminAuthorizer(elevated: true),
            AdminBearer,
            dispatch,
            idempotencyKey,
            expectedCurrentKid);

        var (doc, statusCode) = await ReadJsonWithStatusAsync(result);
        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        doc.RootElement.GetProperty("error").GetString().Should().Be("rotation_precondition_required");
        dispatch.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("W/\"v1\"")]
    [InlineData("*")]
    [InlineData("\"v1\", \"v2\"")]
    public async Task Returns400_WhenIfMatchIsNotOneStrongEntityTag(string ifMatch)
    {
        var dispatch = new RecordingCommandDispatch<RotateAevatarOAuthClientHmacKeyCommand>();

        var result = await InvokeRotateAsync(
            new FakePlatformAdminAuthorizer(elevated: true),
            AdminBearer,
            dispatch,
            rawIfMatch: ifMatch);

        var (doc, statusCode) = await ReadJsonWithStatusAsync(result);
        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        doc.RootElement.GetProperty("error").GetString().Should().Be("rotation_precondition_required");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns400_WhenIdempotencyKeyHasMultipleHeaderValues()
    {
        var dispatch = new RecordingCommandDispatch<RotateAevatarOAuthClientHmacKeyCommand>();

        var result = await InvokeRotateAsync(
            new FakePlatformAdminAuthorizer(elevated: true),
            AdminBearer,
            dispatch,
            idempotencyKey: null,
            rawIdempotencyKeys: ["rotation-request-alpha", "rotation-request-beta"]);

        var (doc, statusCode) = await ReadJsonWithStatusAsync(result);
        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        doc.RootElement.GetProperty("error").GetString().Should().Be("rotation_precondition_required");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns400_WhenIdempotencyKeyContainsAComma()
    {
        var dispatch = new RecordingCommandDispatch<RotateAevatarOAuthClientHmacKeyCommand>();

        var result = await InvokeRotateAsync(
            new FakePlatformAdminAuthorizer(elevated: true),
            AdminBearer,
            dispatch,
            idempotencyKey: "rotation-request-alpha,rotation-request-beta");

        var (doc, statusCode) = await ReadJsonWithStatusAsync(result);
        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        doc.RootElement.GetProperty("error").GetString().Should().Be("rotation_precondition_required");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns503_WhenDispatchRejects()
    {
        var result = await InvokeRotateAsync(
            authorizer: new FakePlatformAdminAuthorizer(elevated: true),
            bearer: AdminBearer,
            dispatch: new RejectingCommandDispatch<RotateAevatarOAuthClientHmacKeyCommand>());

        var (doc, statusCode) = await ReadJsonWithStatusAsync(result);
        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        doc.RootElement.GetProperty("error").GetString().Should().Be("actor_dispatch_rejected");
    }

    [Fact]
    public async Task Returns503_WhenDispatchThrows()
    {
        var result = await InvokeRotateAsync(
            authorizer: new FakePlatformAdminAuthorizer(elevated: true),
            bearer: AdminBearer,
            dispatch: new ThrowingCommandDispatch<RotateAevatarOAuthClientHmacKeyCommand>());

        var (doc, statusCode) = await ReadJsonWithStatusAsync(result);
        statusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        doc.RootElement.GetProperty("error").GetString().Should().Be("actor_dispatch_failed");
    }

    private static Task<IResult> InvokeRotateAsync(
        IPlatformAdminAuthorizer? authorizer,
        string? bearer,
        ICommandDispatchService<RotateAevatarOAuthClientHmacKeyCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> dispatch,
        string? idempotencyKey = "rotation-request-alpha",
        string? expectedCurrentKid = "v1",
        string? rawIfMatch = null,
        string[]? rawIdempotencyKeys = null)
    {
        var http = NewHttpContext();
        if (!string.IsNullOrEmpty(bearer))
            http.Request.Headers.Authorization = "Bearer " + bearer;
        if (rawIdempotencyKeys is not null)
            http.Request.Headers["Idempotency-Key"] = rawIdempotencyKeys;
        else if (idempotencyKey is not null)
            http.Request.Headers["Idempotency-Key"] = idempotencyKey;
        if (rawIfMatch is not null)
            http.Request.Headers.IfMatch = rawIfMatch;
        else if (expectedCurrentKid is not null)
            http.Request.Headers.IfMatch = $"\"{expectedCurrentKid}\"";

        return IdentityOAuthEndpoints.HandleAevatarOAuthClientRotateHmacCoreAsync(
            http: http,
            adminAuthorizer: authorizer,
            rotateDispatch: dispatch,
            loggerFactory: NullLoggerFactory.Instance,
            ct: default);
    }

    private sealed class FakePlatformAdminAuthorizer(bool elevated) : IPlatformAdminAuthorizer
    {
        public Task<PlatformCaller> ResolveCallerAsync(string bearerToken, CancellationToken ct = default)
        {
            return Task.FromResult(elevated
                ? new PlatformCaller(true, "admin", "admin@example.com", "admin-1", PlatformAdminGrantSources.NyxIdPlatformRole)
                : PlatformCaller.NotElevated);
        }
    }

    private static async Task<(JsonDocument Document, int StatusCode)> ReadJsonWithStatusAsync(IResult result)
    {
        var context = NewHttpContext();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (JsonDocument.Parse(text), context.Response.StatusCode);
    }

    private static HttpContext NewHttpContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        return new DefaultHttpContext
        {
            RequestServices = provider,
            Response =
            {
                Body = new MemoryStream(),
            },
        };
    }
}
