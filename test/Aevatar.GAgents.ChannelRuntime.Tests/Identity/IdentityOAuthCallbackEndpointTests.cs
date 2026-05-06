using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour tests for <see cref="IdentityOAuthEndpoints.HandleNyxIdOAuthCallbackAsync"/>
/// covering the legacy already-bound heal path. ADR-0018 §Implementation
/// Notes #2 + PR #555 review (eanzhao): when a sender's binding actor was
/// committed in a previous deploy and the projection scope is being
/// activated for the first time, the actor takes its discard branch on
/// <c>CommitBindingCommand</c>; the readiness wait then can never observe
/// the incoming binding_id (the actor kept its existing one). The callback
/// MUST recognise that shape, revoke the orphan binding NyxID just minted
/// for the incoming code, and surface <c>already_bound</c> instead of the
/// pending-propagation hint — otherwise every retry leaks another orphan
/// at NyxID and the user sees the wrong message.
/// </summary>
public sealed class IdentityOAuthCallbackEndpointTests
{
    [Fact]
    public async Task LegacyAlreadyBound_OnReadinessTimeout_RevokesIncomingAndReturnsAlreadyBound()
    {
        var existing = new BindingId { Value = "bnd_existing" };
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming);
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        // Up-front check (before scope activation has materialised the doc):
        // returns null. Post-timeout check (after rebuild has fired): returns
        // the existing binding actor State holds.
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<BindingId?>(null),
                Task.FromResult<BindingId?>(existing));
        var readiness = Substitute.For<IProjectionReadinessPort>();
        readiness.WaitForBindingStateAsync(
                Arg.Any<ExternalSubjectRef>(),
                incoming,
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TimeoutException("readiness")));

        var (result, _) = await InvokeCallbackAsync(broker, queryPort, readiness);

        await broker.Received(1).RevokeBindingByIdAsync(incoming, Arg.Any<CancellationToken>());
        await ReadJsonAsync(result).ContinueWith(t =>
            t.Result.RootElement.GetProperty("status").GetString().Should().Be("already_bound"));
    }

    [Fact]
    public async Task PendingPropagation_WhenReadinessTimesOutAndReadmodelStillEmpty()
    {
        var subject = SampleSubject();
        var broker = NewBroker(subject, "bnd_incoming");
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(null));
        var readiness = Substitute.For<IProjectionReadinessPort>();
        readiness.WaitForBindingStateAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string?>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TimeoutException("readiness")));

        var (result, _) = await InvokeCallbackAsync(broker, queryPort, readiness);

        await broker.DidNotReceive().RevokeBindingByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        var doc = await ReadJsonAsync(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("binding_pending_propagation");
    }

    [Fact]
    public async Task HappyPath_WaitForBindingSucceeds_ReturnsBound()
    {
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming);
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        // Up-front check returns null; post-success path must NOT call
        // ResolveAsync a second time, so this single value is enough.
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(null));
        var readiness = Substitute.For<IProjectionReadinessPort>();
        readiness.WaitForBindingStateAsync(
                Arg.Any<ExternalSubjectRef>(),
                incoming,
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var (result, _) = await InvokeCallbackAsync(broker, queryPort, readiness);

        await broker.DidNotReceive().RevokeBindingByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await queryPort.Received(1).ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>());
        var doc = await ReadJsonAsync(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("bound");
    }

    // ─── Test plumbing ───

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

    private static ExternalIdentityBindingProjectionPort NewProjectionPort()
    {
        var activationService = Substitute.For<IProjectionScopeActivationService<ExternalIdentityBindingMaterializationRuntimeLease>>();
        activationService.EnsureAsync(Arg.Any<ProjectionScopeStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ExternalIdentityBindingMaterializationRuntimeLease?>(
                new ExternalIdentityBindingMaterializationRuntimeLease(
                    new ExternalIdentityBindingMaterializationContext
                    {
                        RootActorId = "test-actor",
                        ProjectionKind = ExternalIdentityBindingProjectionPort.ProjectionKind,
                    }))!);
        return new ExternalIdentityBindingProjectionPort(activationService);
    }

    private static IActorRuntime NewActorRuntime()
    {
        var noopActor = Substitute.For<IActor>();
        noopActor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var runtime = Substitute.For<IActorRuntime>();
        runtime.CreateAsync<ExternalIdentityBindingGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(noopActor));
        runtime.CreateAsync<AevatarOAuthClientGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(noopActor));
        return runtime;
    }

    private static async Task<(IResult Result, HttpContext Context)> InvokeCallbackAsync(
        INyxIdBrokerCallbackClient broker,
        IExternalIdentityBindingQueryPort queryPort,
        IProjectionReadinessPort readiness)
    {
        var actorRuntime = NewActorRuntime();
        var projectionPort = NewProjectionPort();
        var loggerFactory = NullLoggerFactory.Instance;

        var result = await IdentityOAuthEndpoints.HandleNyxIdOAuthCallbackAsync(
            code: "auth-code",
            state: "state-token",
            error: null,
            brokerCallback: broker,
            queryPort: queryPort,
            actorRuntime: actorRuntime,
            projectionReadiness: readiness,
            bindingProjectionPort: projectionPort,
            loggerFactory: loggerFactory,
            ct: CancellationToken.None);

        return (result, NewHttpContext());
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
        // Minimal-API IResult.ExecuteAsync (Json/Ok/etc.) resolves
        // ILoggerFactory and JsonOptions from RequestServices. Wire up a
        // tiny ServiceCollection so the result-types can render.
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
