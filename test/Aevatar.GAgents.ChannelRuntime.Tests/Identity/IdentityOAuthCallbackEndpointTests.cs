using System.Text;
using System.Text.Json;
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
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour tests for <see cref="IdentityOAuthEndpoints.HandleNyxIdOAuthCallbackAsync"/>.
/// </summary>
public sealed class IdentityOAuthCallbackEndpointTests
{
    [Fact]
    public async Task AcceptedPath_DispatchesBindingCommandAndReturnsPendingJson()
    {
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming);
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(null));
        var bindingDispatch = new RecordingCommandDispatch<CommitBindingCommand>();
        var capabilityDispatch = new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            bindingDispatch,
            capabilityDispatch,
            format: "json");

        bindingDispatch.Commands.Should().ContainSingle();
        bindingDispatch.Commands[0].ExternalSubject.Should().Be(subject);
        bindingDispatch.Commands[0].BindingId.Should().Be(incoming);
        capabilityDispatch.Commands.Should().ContainSingle();

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("binding_pending");
        doc.RootElement.GetProperty("status_url").GetString().Should().Be("/api/oauth/aevatar-client/status");
    }

    [Fact]
    public async Task DispatchRejected_RevokesIncomingAndReturns503()
    {
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming);
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(null));
        var bindingDispatch = new RejectingCommandDispatch<CommitBindingCommand>();
        var capabilityDispatch = new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            bindingDispatch,
            capabilityDispatch);

        await broker.Received(1).RevokeBindingByIdAsync(incoming, Arg.Any<CancellationToken>());
        capabilityDispatch.Commands.Should().BeEmpty();
        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("error").GetString().Should().Be("actor_dispatch_rejected");
    }

    [Fact]
    public async Task AlreadyBound_RevokesIncomingAndReturnsAlreadyBound()
    {
        var existing = new BindingId { Value = "bnd_existing" };
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming);
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(existing));
        var bindingDispatch = new RecordingCommandDispatch<CommitBindingCommand>();
        var capabilityDispatch = new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            bindingDispatch,
            capabilityDispatch);

        await broker.Received(1).RevokeBindingByIdAsync(incoming, Arg.Any<CancellationToken>());
        bindingDispatch.Commands.Should().BeEmpty();
        capabilityDispatch.Commands.Should().BeEmpty();
        var html = await ReadTextAsync(result);
        html.Should().Contain("已绑定");
        html.Should().Contain("/whoami");
    }

    [Fact]
    public async Task DispatchFailure_RevokesIncomingAndReturns503()
    {
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming);
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(null));
        var bindingDispatch = new ThrowingCommandDispatch<CommitBindingCommand>();
        var capabilityDispatch = new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            bindingDispatch,
            capabilityDispatch);

        await broker.Received(1).RevokeBindingByIdAsync(incoming, Arg.Any<CancellationToken>());
        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task AcceptedPath_RendersHtml_ContentTypeIsTextHtml()
    {
        var subject = SampleSubject();
        var broker = NewBroker(subject, "bnd_incoming");
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(null));

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            new RecordingCommandDispatch<CommitBindingCommand>(),
            new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>());
        var (text, contentType) = await ReadTextWithContentTypeAsync(result);

        contentType.Should().StartWith("text/html");
        text.Should().Contain("<!DOCTYPE html>");
        text.Should().Contain("已受理");
        text.Should().Contain("/whoami");
    }

    // Refactor (iter27/cluster-028-identity-oauth-endpoint):
    //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
    //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
    private static async Task<IResult> InvokeCallbackAsync(
        INyxIdBrokerCallbackClient broker,
        IExternalIdentityBindingQueryPort queryPort,
        ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> bindingDispatch,
        ICommandDispatchService<ObserveBrokerCapabilityCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> capabilityDispatch,
        string? format = null)
    {
        return await IdentityOAuthEndpoints.HandleNyxIdOAuthCallbackAsync(
            code: "auth-code",
            state: "state-token",
            error: null,
            format: format,
            brokerCallback: broker,
            queryPort: queryPort,
            bindingDispatch: bindingDispatch,
            brokerCapabilityDispatch: capabilityDispatch,
            loggerFactory: NullLoggerFactory.Instance,
            ct: CancellationToken.None);
    }

    private static ExternalSubjectRef SampleSubject() => new()
    {
        Platform = "lark",
        Tenant = "ou_tenant_x",
        ExternalUserId = "ou_user_y",
    };

    private static INyxIdBrokerCallbackClient NewBroker(ExternalSubjectRef subject, string bindingId)
    {
        var broker = Substitute.For<INyxIdBrokerCallbackClient>();
        broker.TryDecodeStateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CallbackStateDecode.Ok(
                correlationId: "correlation-1",
                subject: subject,
                verifier: "pkce-verifier")));
        broker.ExchangeAuthorizationCodeAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BrokerAuthorizationCodeResult(bindingId, IdToken: null, AccessToken: null)));
        return broker;
    }

    private static async Task<string> ReadTextAsync(IResult result)
    {
        var (text, _) = await ReadTextWithContentTypeAsync(result);
        return text;
    }

    private static async Task<(string Text, string? ContentType)> ReadTextWithContentTypeAsync(IResult result)
    {
        var context = NewHttpContext();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (text, context.Response.ContentType);
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
