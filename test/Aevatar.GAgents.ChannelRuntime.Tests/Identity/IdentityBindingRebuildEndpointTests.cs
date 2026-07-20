using System.Text;
using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour tests for <see cref="IdentityOAuthEndpoints.HandleNyxIdBindingRebuildAsync"/>,
/// the operator-gated headless recovery endpoint for a wiped NyxID owner binding readmodel.
/// Mirrors the admin-auth + dispatch shape of the OAuth-client rebuild endpoint tests.
/// </summary>
public sealed class IdentityBindingRebuildEndpointTests
{
    private const string AdminBearer = "admin-bearer-token";
    private const string OwnerUserId = "ou_owner_z";

    [Fact]
    public async Task Returns503_WhenAuthorizerMissing()
    {
        var dispatch = new RecordingCommandDispatch<RebuildBindingProjectionCommand>();
        var result = await InvokeRebuildAsync(authorizer: null, bearer: AdminBearer, body: SampleBody(), dispatch: dispatch);

        var doc = await ReadJsonAsync(result);
        doc.RootElement.GetProperty("error").GetString().Should().Be("rebuild_admin_authorizer_unavailable");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns403_WhenBearerMissing()
    {
        var dispatch = new RecordingCommandDispatch<RebuildBindingProjectionCommand>();
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(true),
            bearer: null,
            body: SampleBody(),
            dispatch: dispatch);

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns403_WhenCallerNotElevated()
    {
        var dispatch = new RecordingCommandDispatch<RebuildBindingProjectionCommand>();
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(false),
            bearer: AdminBearer,
            body: SampleBody(),
            dispatch: dispatch);

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        dispatch.Commands.Should().BeEmpty("a non-admin caller must never reach dispatch");
    }

    [Fact]
    public async Task Returns400_WhenExternalUserIdMissing()
    {
        var dispatch = new RecordingCommandDispatch<RebuildBindingProjectionCommand>();
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(true),
            bearer: AdminBearer,
            body: new IdentityOAuthEndpoints.RebuildNyxIdBindingRequest(external_user_id: "  ", platform: null, tenant: null),
            dispatch: dispatch);

        var doc = await ReadJsonAsync(result);
        doc.RootElement.GetProperty("error").GetString().Should().Be("external_user_id_required");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchesRebuildCommand_ForNyxIdOwnerSubject_AndReturnsAccepted()
    {
        var dispatch = new RecordingCommandDispatch<RebuildBindingProjectionCommand>(
            static cmd => new ChannelIdentityOAuthAcceptedReceipt(
                ActorId: cmd.ExternalSubject.ToActorId(),
                CommandId: "cmd-1",
                CorrelationId: "cmd-1"));
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(
                elevated: true,
                grantSource: PlatformAdminGrantSources.NyxIdPlatformRole),
            bearer: AdminBearer,
            body: new IdentityOAuthEndpoints.RebuildNyxIdBindingRequest(external_user_id: OwnerUserId, platform: null, tenant: null),
            dispatch: dispatch);

        dispatch.Commands.Should().ContainSingle();
        var subject = dispatch.Commands[0].ExternalSubject;
        subject.Platform.Should().Be(OwnerScope.NyxIdPlatform, "the owner platform defaults to NyxID");
        subject.Tenant.Should().BeEmpty();
        subject.ExternalUserId.Should().Be(OwnerUserId);

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("rebuild_pending");
        doc.RootElement.GetProperty("actor_id").GetString().Should().Be(subject.ToActorId());
        doc.RootElement.GetProperty("admin_grant_source").GetString().Should().Be(PlatformAdminGrantSources.NyxIdPlatformRole);
    }

    [Fact]
    public async Task Returns503_WhenDispatchThrows()
    {
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(true),
            bearer: AdminBearer,
            body: SampleBody(),
            dispatch: new ThrowingCommandDispatch<RebuildBindingProjectionCommand>());

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Returns503_WhenDispatchRejects()
    {
        var result = await InvokeRebuildAsync(
            authorizer: new FakePlatformAdminAuthorizer(true),
            bearer: AdminBearer,
            body: SampleBody(),
            dispatch: new RejectingCommandDispatch<RebuildBindingProjectionCommand>());

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("error").GetString().Should().Be("actor_dispatch_rejected");
    }

    private static IdentityOAuthEndpoints.RebuildNyxIdBindingRequest SampleBody() =>
        new(external_user_id: OwnerUserId, platform: null, tenant: null);

    private static Task<IResult> InvokeRebuildAsync(
        IPlatformAdminAuthorizer? authorizer,
        string? bearer,
        IdentityOAuthEndpoints.RebuildNyxIdBindingRequest body,
        ICommandDispatchService<RebuildBindingProjectionCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> dispatch,
        CancellationToken ct = default)
    {
        var http = NewHttpContext();
        if (!string.IsNullOrEmpty(bearer))
            http.Request.Headers.Authorization = "Bearer " + bearer;

        return IdentityOAuthEndpoints.HandleNyxIdBindingRebuildCoreAsync(
            http: http,
            body: body,
            adminAuthorizer: authorizer,
            rebuildDispatch: dispatch,
            loggerFactory: NullLoggerFactory.Instance,
            ct: ct);
    }

    private sealed class FakePlatformAdminAuthorizer(
        bool elevated,
        string role = "admin",
        string grantSource = PlatformAdminGrantSources.NyxIdPlatformRole) : IPlatformAdminAuthorizer
    {
        public Task<PlatformCaller> ResolveCallerAsync(string bearerToken, CancellationToken ct = default) =>
            Task.FromResult(elevated
                ? new PlatformCaller(true, role, "admin@example.com", "admin-1", grantSource)
                : PlatformCaller.NotElevated);
    }

    private static async Task<JsonDocument> ReadJsonAsync(IResult result)
    {
        var context = NewHttpContext();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return JsonDocument.Parse(text);
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
