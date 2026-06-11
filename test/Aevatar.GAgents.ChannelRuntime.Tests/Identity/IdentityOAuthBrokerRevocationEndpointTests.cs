using System.Security.Cryptography;
using System.Text;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour tests for NyxID broker revocation endpoint command dispatch.
/// </summary>
public sealed class IdentityOAuthBrokerRevocationEndpointTests
{
    private static readonly byte[] CurrentKey =
        Convert.FromHexString("11111111111111111111111111111111111111111111111111111111111111aa");

    private static readonly byte[] WebhookBody =
        Encoding.UTF8.GetBytes("""{"EventType":"binding_revoked","BindingId":"bnd_1","Reason":"nyxid_revoked","Platform":"lark","Tenant":"t","ExternalUserId":"u"}""");

    [Fact]
    public async Task ValidWebhook_DispatchesRevokeCommandAndReturnsAccepted()
    {
        var dispatch = new RecordingCommandDispatch<RevokeBindingCommand>(
            static command => new ChannelIdentityOAuthAcceptedReceipt(
                ActorId: command.ExternalSubject.ToActorId(),
                CommandId: "cmd-1",
                CorrelationId: "cmd-1"));
        var result = await InvokeEndpointAsync(dispatch);

        dispatch.Commands.Should().ContainSingle();
        var command = dispatch.Commands[0];
        command.ExternalSubject.Should().BeEquivalentTo(new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "t",
            ExternalUserId = "u",
        });
        command.Reason.Should().Be("nyxid_revoked");

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
    }

    [Fact]
    public async Task DispatchRejected_ReturnsProblem()
    {
        var result = await InvokeEndpointAsync(new RejectingCommandDispatch<RevokeBindingCommand>());

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    private static Task<IResult> InvokeEndpointAsync(
        ICommandDispatchService<RevokeBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> dispatch)
    {
        var http = NewHttpContext();
        http.Request.Body = new MemoryStream(WebhookBody);
        http.Request.Headers[BrokerRevocationWebhookValidator.SignatureHeader] = SignBody(CurrentKey);
        var validator = new BrokerRevocationWebhookValidator(
            new FakeOAuthClientProvider(NewSnapshot()),
            Options.Create(new NyxIdBrokerOptions()));

        return IdentityOAuthEndpoints.HandleBrokerRevocationWebhookAsync(
            http,
            validator,
            dispatch,
            NullLoggerFactory.Instance,
            CancellationToken.None);
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

    private static string SignBody(byte[] key)
    {
        var hmac = HMACSHA256.HashData(key, WebhookBody);
        return $"sha256={Convert.ToHexString(hmac).ToLowerInvariant()}";
    }

    private static AevatarOAuthClientSnapshot NewSnapshot() => new(
        ClientId: "aevatar-channel-binding",
        ClientIdIssuedAt: DateTimeOffset.Parse("2026-04-30T09:00:00Z"),
        HmacKid: "v2",
        HmacKey: CurrentKey,
        HmacKeyRotatedAt: DateTimeOffset.Parse("2026-04-30T09:30:00Z"),
        NyxIdAuthority: "https://nyxid.test",
        BrokerCapabilityObserved: true,
        BrokerCapabilityObservedAt: DateTimeOffset.Parse("2026-04-30T09:00:00Z"));

    private sealed class FakeOAuthClientProvider(AevatarOAuthClientSnapshot snapshot) : IAevatarOAuthClientProvider
    {
        public Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

}
