using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
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
        var html = await ReadTextAsync(result);
        html.Should().Contain("已绑定");
        html.Should().Contain("/whoami");
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
        var html = await ReadTextAsync(result);
        // Issue #513 phase 1 substitute: the success page must name the
        // next-step slash commands so the user knows what to type back in
        // Lark after the OAuth round-trip.
        html.Should().Contain("绑定成功");
        html.Should().Contain("/model");
        html.Should().Contain("/whoami");
    }

    [Fact]
    public async Task HappyPath_RendersHtml_ContentTypeIsTextHtml()
    {
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming);
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
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
        var (text, contentType) = await ReadTextWithContentTypeAsync(result);

        contentType.Should().StartWith("text/html");
        text.Should().Contain("<!DOCTYPE html>");
    }

    [Fact]
    public async Task HappyPath_CommitsProjectsAndQueryResolvesBindingReadModel()
    {
        const string incoming = "bnd_projected";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming);
        var readModelStore = new InMemoryBindingDocumentStore();
        var queryPort = new ExternalIdentityBindingProjectionQueryPort(readModelStore);
        var readiness = new ExternalIdentityBindingProjectionReadinessPort(readModelStore);
        var projector = new ExternalIdentityBindingProjector(
            readModelStore,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-04-30T09:42:36Z")));
        var runtime = new ProjectingActorRuntime(projector);
        var dispatchPort = new InlineActorDispatchPort(runtime);

        var (result, _) = await InvokeCallbackAsync(broker, queryPort, readiness, runtime, dispatchPort);

        var html = await ReadTextAsync(result);
        html.Should().Contain("绑定成功");

        var resolved = await queryPort.ResolveAsync(subject);
        resolved.Should().NotBeNull();
        resolved!.Value.Should().Be(incoming);

        var materialized = await readModelStore.GetAsync(subject.ToActorId());
        materialized.Should().NotBeNull();
        materialized!.BindingId.Should().Be(incoming);
        materialized.IsActive.Should().BeTrue();
        materialized.StateVersion.Should().Be(1);
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
        IProjectionReadinessPort readiness,
        IActorRuntime? actorRuntime = null,
        IActorDispatchPort? actorDispatchPort = null)
    {
        actorRuntime ??= NewActorRuntime();
        if (actorDispatchPort is null)
        {
            actorDispatchPort = Substitute.For<IActorDispatchPort>();
            actorDispatchPort.DispatchAsync(Arg.Any<string>(), Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
        }
        var projectionPort = NewProjectionPort();
        var loggerFactory = NullLoggerFactory.Instance;

        var result = await IdentityOAuthEndpoints.HandleNyxIdOAuthCallbackAsync(
            code: "auth-code",
            state: "state-token",
            error: null,
            format: null,
            brokerCallback: broker,
            queryPort: queryPort,
            actorRuntime: actorRuntime,
            actorDispatchPort: actorDispatchPort,
            projectionReadiness: readiness,
            bindingProjectionPort: projectionPort,
            loggerFactory: loggerFactory,
            ct: CancellationToken.None);

        return (result, NewHttpContext());
    }

    private static async Task<JsonDocument> ReadJsonAsync(IResult result)
    {
        var (text, _) = await ReadTextWithContentTypeAsync(result);
        return JsonDocument.Parse(text);
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

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class InMemoryBindingDocumentStore :
        IProjectionDocumentReader<ExternalIdentityBindingDocument, string>,
        IProjectionWriteDispatcher<ExternalIdentityBindingDocument>
    {
        private readonly Dictionary<string, ExternalIdentityBindingDocument> _documents = new(StringComparer.Ordinal);

        public Task<ExternalIdentityBindingDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _documents.TryGetValue(key, out var document);
            return Task.FromResult(document?.Clone());
        }

        public Task<ProjectionDocumentQueryResult<ExternalIdentityBindingDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionDocumentQueryResult<ExternalIdentityBindingDocument>.Empty);

        public Task<ProjectionWriteResult> UpsertAsync(
            ExternalIdentityBindingDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _documents[readModel.Id] = readModel.Clone();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _documents.Remove(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class ProjectingActorRuntime(ExternalIdentityBindingProjector projector) : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);

        public async Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            if (typeof(TAgent) == typeof(ExternalIdentityBindingGAgent))
            {
                var actorId = id ?? throw new ArgumentNullException(nameof(id));
                if (_actors.TryGetValue(actorId, out var existing))
                    return existing;

                var actor = await ProjectingBindingActor.CreateAsync(actorId, projector, ct);
                _actors[actorId] = actor;
                return actor;
            }

            if (typeof(TAgent) == typeof(AevatarOAuthClientGAgent))
                return new NoopActor(id ?? AevatarOAuthClientGAgent.WellKnownId);

            throw new NotSupportedException(typeof(TAgent).FullName);
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException(agentType.FullName);

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            _actors.TryGetValue(id, out var actor);
            return Task.FromResult(actor);
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InlineActorDispatchPort(IActorRuntime runtime) : IActorDispatchPort
    {
        public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            var actor = await runtime.GetAsync(actorId).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Actor '{actorId}' was not activated.");
            await actor.HandleEventAsync(envelope, ct).ConfigureAwait(false);
        }
    }

    private sealed class ProjectingBindingActor : IActor
    {
        private readonly ExternalIdentityBindingGAgent _agent;
        private readonly ExternalIdentityBindingProjector _projector;
        private long _projectedVersion;

        private ProjectingBindingActor(
            string id,
            ExternalIdentityBindingGAgent agent,
            ExternalIdentityBindingProjector projector)
        {
            Id = id;
            _agent = agent;
            _projector = projector;
        }

        public string Id { get; }

        public IAgent Agent => _agent;

        public static async Task<ProjectingBindingActor> CreateAsync(
            string actorId,
            ExternalIdentityBindingProjector projector,
            CancellationToken ct)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IEventStore, IdentityGAgentTestHarness.InMemoryEventStore>();
            services.AddSingleton<EventSourcingRuntimeOptions>();
            services.AddTransient(
                typeof(IEventSourcingBehaviorFactory<>),
                typeof(DefaultEventSourcingBehaviorFactory<>));
            services.AddSingleton<IActorRuntimeCallbackScheduler, IdentityGAgentTestHarness.NoopCallbackScheduler>();
            var provider = services.BuildServiceProvider();

            var agent = new ExternalIdentityBindingGAgent
            {
                Services = provider,
                EventSourcingBehaviorFactory =
                    provider.GetRequiredService<IEventSourcingBehaviorFactory<ExternalIdentityBindingState>>(),
            };
            TestAgentIdentity.SetId(agent, actorId);
            await agent.ActivateAsync(ct);
            return new ProjectingBindingActor(actorId, agent, projector);
        }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            await _agent.HandleEventAsync(envelope, ct);
            var currentVersion = _agent.EventSourcing?.CurrentVersion ?? 0;
            if (currentVersion <= _projectedVersion)
                return;

            var context = new ExternalIdentityBindingMaterializationContext
            {
                RootActorId = Id,
                ProjectionKind = ExternalIdentityBindingProjectionPort.ProjectionKind,
            };
            var projectedEnvelope = TestEnvelopeBuilder.BuildCommittedEnvelope(
                _agent.State.Clone(),
                currentVersion,
                $"evt-{currentVersion}");
            await _projector.ProjectAsync(context, projectedEnvelope, ct);
            _projectedVersion = currentVersion;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoopActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent { get; } = Substitute.For<IAgent>();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static class TestAgentIdentity
    {
        private static readonly System.Reflection.MethodInfo SetIdMethod =
            typeof(GAgentBase).GetMethod(
                "SetId",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GAgentBase.SetId was not found.");

        public static void SetId(GAgentBase agent, string actorId) =>
            SetIdMethod.Invoke(agent, [actorId]);
    }
}
