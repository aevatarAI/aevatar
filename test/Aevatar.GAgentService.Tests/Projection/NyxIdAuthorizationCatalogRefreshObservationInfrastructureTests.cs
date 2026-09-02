using System.Runtime.CompilerServices;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class NyxIdAuthorizationCatalogRefreshObservationInfrastructureTests
{
    private const string RefreshObservationProjectionKind =
        "nyxid-authorization-catalog-refresh-observation";
    private static readonly DateTimeOffset RefreshStartedAt =
        DateTimeOffset.Parse("2026-07-21T09:00:00Z");

    [Theory]
    [InlineData(NyxIdAuthorizationCatalogRefreshOutcomeStatus.Started)]
    [InlineData(NyxIdAuthorizationCatalogRefreshOutcomeStatus.Observed)]
    [InlineData(NyxIdAuthorizationCatalogRefreshOutcomeStatus.Failed)]
    [InlineData(NyxIdAuthorizationCatalogRefreshOutcomeStatus.AccessDenied)]
    [InlineData(NyxIdAuthorizationCatalogRefreshOutcomeStatus.CatalogUnstable)]
    [InlineData(NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded)]
    public void Codec_ShouldRoundTripTypedCommittedOutcome(
        NyxIdAuthorizationCatalogRefreshOutcomeStatus status)
    {
        var codec = new NyxIdAuthorizationCatalogRefreshObservationSessionEventCodec();
        var outcome = new NyxIdAuthorizationCatalogRefreshCommittedOutcome(
            "refresh-alpha",
            status,
            42,
            status == NyxIdAuthorizationCatalogRefreshOutcomeStatus.Observed
                ? string.Empty
                : "stable_failure_code",
            DateTimeOffset.Parse("2026-07-21T09:00:00Z"));

        var eventType = codec.GetEventType(outcome);
        var decoded = codec.Deserialize(eventType, codec.Serialize(outcome));

        codec.Channel.Should().Be("nyxid-authorization-catalog-refresh-observation");
        eventType.Should().Be(NyxIdAuthorizationCatalogRefreshOutcomeEvent.Descriptor.FullName);
        decoded.Should().BeEquivalentTo(outcome);
        codec.Deserialize("different-event", codec.Serialize(outcome)).Should().BeNull();
        codec.Deserialize(eventType, ByteString.CopyFrom(new byte[] { 0x0A, 0x05 })).Should().BeNull();
    }

    [Fact]
    public async Task Projector_ShouldPublishOnlyMatchingCommittedRefreshOutcome()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new NyxIdAuthorizationCatalogRefreshObservationSessionEventProjector(hub);
        var context = new NyxIdAuthorizationCatalogRefreshObservationProjectionContext
        {
            RootActorId = "nyxid-authorization-catalog:owner-alpha",
            ProjectionKind = "nyxid-authorization-catalog-refresh-observation",
            SessionId = "refresh-alpha",
        };

        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new NyxIdAuthorizationCatalogRefreshOutcomeEvent
            {
                RefreshId = "refresh-alpha",
                Status = NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Observed,
                StateVersion = 7,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(
                    DateTimeOffset.Parse("2026-07-21T09:00:00Z")),
            }));
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new NyxIdAuthorizationCatalogRefreshOutcomeEvent
            {
                RefreshId = "refresh-other",
                Status = NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Failed,
                StateVersion = 8,
                FailureCode = "provider_unavailable",
                ObservedAtUtc = Timestamp.FromDateTimeOffset(
                    DateTimeOffset.Parse("2026-07-21T09:00:01Z")),
            }));
        await projector.ProjectAsync(
            context,
            CommittedEnvelope(new StringValue { Value = "not-a-refresh-outcome" }));

        hub.Published.Should().ContainSingle();
        var published = hub.Published[0];
        published.RootActorId.Should().Be("nyxid-authorization-catalog:owner-alpha");
        published.SessionId.Should().Be("refresh-alpha");
        published.Outcome.RefreshId.Should().Be("refresh-alpha");
        published.Outcome.Status.Should().Be(NyxIdAuthorizationCatalogRefreshOutcomeStatus.Observed);
        published.Outcome.StateVersion.Should().Be(7);
    }

    [Fact]
    public async Task PreparationPort_ShouldActivateAndReleaseExactRefreshScope()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingProjectionReleaseService<
            NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>();
        var port = new NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort(
            activation,
            release);

        var preparation = await port.PrepareAsync(
            "  nyxid-authorization-catalog:owner-alpha  ",
            "  refresh-alpha  ");

        preparation.Should().Be(new NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation(
            "nyxid-authorization-catalog:owner-alpha",
            "refresh-alpha"));
        activation.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ProjectionScopeStartRequest
            {
                RootActorId = "nyxid-authorization-catalog:owner-alpha",
                ProjectionKind = "nyxid-authorization-catalog-refresh-observation",
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = "refresh-alpha",
            });

        await port.ReleaseAsync(preparation!);

        release.Released.Should().ContainSingle();
        release.Released[0].ActorId.Should().Be("nyxid-authorization-catalog:owner-alpha");
        release.Released[0].RefreshId.Should().Be("refresh-alpha");
    }

    [Fact]
    public async Task PreparationPort_ShouldReleaseDeterministicScopeWhenActivationFailsAfterPartialSideEffect()
    {
        var activation = new PartiallyFailingActivationService();
        var release = new RecordingProjectionReleaseService<
            NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>();
        var port = new NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort(
            activation,
            release);

        var preparation = await port.PrepareAsync(
            "nyxid-authorization-catalog:owner-alpha",
            "refresh-alpha");

        preparation.Should().BeNull();
        activation.PartiallyCreatedScopes.Should().ContainSingle();
        release.Released.Should().ContainSingle();
        release.Released[0].ActorId.Should().Be("nyxid-authorization-catalog:owner-alpha");
        release.Released[0].RefreshId.Should().Be("refresh-alpha");
    }

    [Fact]
    public async Task PreparationPort_ShouldPropagateCallerCancellation()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingProjectionReleaseService<
            NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>();
        var port = new NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort(
            activation,
            release);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => port.PrepareAsync(
            "nyxid-authorization-catalog:owner-alpha",
            "refresh-alpha",
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PreparationPort_WhenCancellationRollbackFails_ShouldPreserveCallerCancellation()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingProjectionReleaseService<
            NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>
        {
            Exception = new InvalidOperationException("cleanup-private-detail"),
        };
        var port = new NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort(
            activation,
            release);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => port.PrepareAsync(
            "nyxid-authorization-catalog:owner-alpha",
            "refresh-alpha",
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        release.Released.Should().ContainSingle();
    }

    [Fact]
    public async Task PreparationPort_WhenFailureRollbackFails_ShouldPreservePreparationFailureResult()
    {
        var activation = new PartiallyFailingActivationService();
        var release = new RecordingProjectionReleaseService<
            NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>
        {
            Exception = new InvalidOperationException("cleanup-private-detail"),
        };
        var port = new NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort(
            activation,
            release);

        var preparation = await port.PrepareAsync(
            "nyxid-authorization-catalog:owner-alpha",
            "refresh-alpha");

        preparation.Should().BeNull();
        release.Released.Should().ContainSingle();
    }

    [Fact]
    public async Task ProjectionPort_ShouldAttachOnlyToPreparedExistingRefreshScope()
    {
        var hub = new RecordingSessionEventHub();
        var lease = new NyxIdAuthorizationCatalogRefreshObservationRuntimeLease(
            new NyxIdAuthorizationCatalogRefreshObservationProjectionContext
            {
                RootActorId = "nyxid-authorization-catalog:owner-alpha",
                ProjectionKind = "nyxid-authorization-catalog-refresh-observation",
                SessionId = "refresh-alpha",
            });
        var lookup = new RecordingAttachExistingLeaseLookup { Lease = lease };
        var release = new RecordingProjectionReleaseService<
            NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>();
        var port = new NyxIdAuthorizationCatalogRefreshObservationProjectionPort(
            new ServiceProjectionOptions { Enabled = true },
            release,
            hub,
            lookup);
        var sink = new RecordingEventSink();

        var attachment = await port.AttachExistingRefreshProjectionAsync(
            " nyxid-authorization-catalog:owner-alpha ",
            " refresh-alpha ",
            sink);

        attachment.Should().NotBeNull();
        lookup.Requests.Should().ContainSingle();
        lookup.Requests[0].RootActorId.Should().Be("nyxid-authorization-catalog:owner-alpha");
        lookup.Requests[0].SessionId.Should().Be("refresh-alpha");
        hub.LastSubscription.Should().Be((
            "nyxid-authorization-catalog:owner-alpha",
            "refresh-alpha"));

        var outcome = CreateOutcome();
        await hub.SubscriptionHandler!(outcome);
        sink.Events.Should().ContainSingle().Which.Should().BeSameAs(outcome);

        await port.DetachLiveSinkAsync(attachment!.LiveSinkLease);
        await port.ReleaseActorProjectionAsync(attachment.ProjectionLease);
        release.Released.Should().ContainSingle().Which.Should().BeSameAs(lease);
    }

    [Fact]
    public async Task RefreshPort_WhenSupersededWithProviderIncomplete_ShouldReleaseProductionObservationResources()
    {
        await using var services = CreateProductionObservationServices();
        var actorRuntime = services.GetRequiredService<IActorRuntime>();
        var commandPort = new NyxIdAuthorizationCatalogCommandPort(
            actorRuntime,
            services.GetRequiredService<IActorDispatchPort>());
        var sessionEventHub = services.GetRequiredService<
            IProjectionSessionEventHub<NyxIdAuthorizationCatalogRefreshCommittedOutcome>>();
        var providerHandler = new IncompleteProviderHandler();
        using var httpClient = new HttpClient(providerHandler)
        {
            BaseAddress = new Uri("https://nyx.example"),
        };
        var refreshPort = new NyxIdAuthorizationCatalogRefreshPort(
            commandPort,
            new EmptyCatalogQueryPort(),
            new TestNyxIdApiClientFactory(new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
                httpClient)),
            services.GetRequiredService<
                INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort>(),
            services.GetRequiredService<
                INyxIdAuthorizationCatalogRefreshObservationProjectionPort>(),
            new FakeTimeProvider(RefreshStartedAt),
            NullLogger<NyxIdAuthorizationCatalogRefreshPort>.Instance);
        var owner = PersonalOwner();
        var catalogActorId = NyxIdAuthorizationCatalogActorIds.Build(owner);
        var refresh = refreshPort.RefreshAsync(owner, "bearer-secret");

        await providerHandler.Blocked.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            var catalogActor = await actorRuntime.GetAsync(catalogActorId);
            catalogActor.Should().NotBeNull();
            var catalogAgent = catalogActor!.Agent
                .Should().BeOfType<NyxIdAuthorizationCatalogGAgent>().Subject;
            var losingRefreshId = catalogAgent.State.ActiveRefreshId;
            var scopeKey = new ProjectionRuntimeScopeKey(
                catalogActorId,
                RefreshObservationProjectionKind,
                ProjectionRuntimeMode.SessionObservation,
                losingRefreshId);
            var scopeActorId = ProjectionScopeActorId.Build(scopeKey);
            var forwardingRegistry = services.GetRequiredService<IStreamForwardingRegistry>();

            losingRefreshId.Should().NotBeNullOrWhiteSpace();
            (await forwardingRegistry.ListBySourceAsync(catalogActorId)).Should().Contain(binding =>
                string.Equals(binding.TargetStreamId, scopeActorId, StringComparison.Ordinal));

            await commandPort.BeginRefreshAsync(
                owner,
                "refresh-winner",
                RefreshStartedAt.AddSeconds(1),
                catalogAgent.State.LifecycleFence);

            var result = await refresh.WaitAsync(TimeSpan.FromSeconds(1));
            await providerHandler.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(1));
            result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Superseded);
            providerHandler.ProviderCompleted.Should().BeFalse();

            var scopeActor = await actorRuntime.GetAsync(scopeActorId);
            scopeActor.Should().NotBeNull();
            await DrainActorMailboxAsync(scopeActor!);
            var scopeAgent = scopeActor!.Agent.Should().BeOfType<ProjectionSessionScopeGAgent<
                NyxIdAuthorizationCatalogRefreshObservationProjectionContext>>().Subject;
            scopeAgent.State.Released.Should().BeTrue();
            scopeAgent.State.ObservationAttached.Should().BeFalse();
            (await forwardingRegistry.ListBySourceAsync(catalogActorId)).Should().NotContain(binding =>
                string.Equals(binding.TargetStreamId, scopeActorId, StringComparison.Ordinal));

            await AssertReleasedLiveSinkIsDetachedAsync(
                services.GetRequiredService<IStreamProvider>(),
                services.GetRequiredService<
                    IProjectionSessionEventCodec<NyxIdAuthorizationCatalogRefreshCommittedOutcome>>(),
                sessionEventHub,
                catalogActorId,
                losingRefreshId);
            providerHandler.ProviderCompleted.Should().BeFalse();
        }
        finally
        {
            providerHandler.CompleteCanceled();
            await providerHandler.Exited.WaitAsync(TimeSpan.FromSeconds(1));
            await IgnoreFailureAsync(refresh);
        }
    }

    [Fact]
    public void AddGAgentServiceProjection_ShouldRegisterCatalogRefreshObservationRuntime()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceProjection();

        services.Should().Contain(x =>
            x.ServiceType == typeof(
                INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort) &&
            x.ImplementationType == typeof(
                NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(INyxIdAuthorizationCatalogRefreshObservationProjectionPort) &&
            x.ImplementationType == typeof(
                NyxIdAuthorizationCatalogRefreshObservationProjectionPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(
                IProjectionSessionEventCodec<NyxIdAuthorizationCatalogRefreshCommittedOutcome>) &&
            x.ImplementationType == typeof(
                NyxIdAuthorizationCatalogRefreshObservationSessionEventCodec));
        services.Should().Contain(x =>
            x.ServiceType == typeof(
                IProjectionSessionEventHub<NyxIdAuthorizationCatalogRefreshCommittedOutcome>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionScopeActivationService<
                NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionScopeAttachExistingLeaseLookup<
                NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionProjector<
                NyxIdAuthorizationCatalogRefreshObservationProjectionContext>) &&
            x.ImplementationType == typeof(
                NyxIdAuthorizationCatalogRefreshObservationSessionEventProjector));
    }

    private static NyxIdAuthorizationCatalogRefreshCommittedOutcome CreateOutcome() =>
        new(
            "refresh-alpha",
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.Observed,
            10,
            string.Empty,
            DateTimeOffset.Parse("2026-07-21T09:00:00Z"));

    private static ServiceProvider CreateProductionObservationServices()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime(options => options.ThrowOnSubscriberError = true);
        services.AddAevatarAgentKindRegistry(builder =>
            builder.Register<NyxIdAuthorizationCatalogGAgent>());
        services.AddTransient<NyxIdAuthorizationCatalogGAgent>();
        services.AddEventSinkProjectionRuntimeCore<
            NyxIdAuthorizationCatalogRefreshObservationProjectionContext,
            NyxIdAuthorizationCatalogRefreshObservationRuntimeLease,
            NyxIdAuthorizationCatalogRefreshCommittedOutcome,
            ProjectionSessionScopeGAgent<NyxIdAuthorizationCatalogRefreshObservationProjectionContext>>(
            static scopeKey => new NyxIdAuthorizationCatalogRefreshObservationProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                SessionId = scopeKey.SessionId,
            },
            static context => new NyxIdAuthorizationCatalogRefreshObservationRuntimeLease(context));
        services.AddSingleton(new ServiceProjectionOptions { Enabled = true });
        services.AddSingleton<
            IProjectionSessionEventCodec<NyxIdAuthorizationCatalogRefreshCommittedOutcome>,
            NyxIdAuthorizationCatalogRefreshObservationSessionEventCodec>();
        services.AddSingleton<
            IProjectionSessionEventHub<NyxIdAuthorizationCatalogRefreshCommittedOutcome>,
            ProjectionSessionEventHub<NyxIdAuthorizationCatalogRefreshCommittedOutcome>>();
        services.AddSingleton<IProjectionProjector<
            NyxIdAuthorizationCatalogRefreshObservationProjectionContext>,
            NyxIdAuthorizationCatalogRefreshObservationSessionEventProjector>();
        services.AddSingleton<
            INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort,
            NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort>();
        services.AddSingleton<
            INyxIdAuthorizationCatalogRefreshObservationProjectionPort,
            NyxIdAuthorizationCatalogRefreshObservationProjectionPort>();
        return services.BuildServiceProvider();
    }

    private static AuthorizationOwnerIdentity PersonalOwner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = "owner-alpha",
    };

    private static Task DrainActorMailboxAsync(IActor actor) =>
        actor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new ReplayProjectionFailuresCommand()),
            Route = EnvelopeRouteSemantics.CreateDirect("test.projection.barrier", actor.Id),
        });

    private static async Task AssertReleasedLiveSinkIsDetachedAsync(
        IStreamProvider streamProvider,
        IProjectionSessionEventCodec<NyxIdAuthorizationCatalogRefreshCommittedOutcome> codec,
        IProjectionSessionEventHub<NyxIdAuthorizationCatalogRefreshCommittedOutcome> hub,
        string actorId,
        string refreshId)
    {
        var transportObserved = new TaskCompletionSource<ProjectionSessionEventTransportMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = streamProvider.GetStream($"{codec.Channel}:{actorId}:{refreshId}");
        await using var probe = await stream.SubscribeAsync<ProjectionSessionEventTransportMessage>(message =>
        {
            transportObserved.TrySetResult(message);
            return Task.CompletedTask;
        });

        // A leaked earlier sink would write to its completed EventChannel and stop this
        // ThrowOnSubscriberError stream before the later probe receives the message.
        await hub.PublishAsync(
            actorId,
            refreshId,
            new NyxIdAuthorizationCatalogRefreshCommittedOutcome(
                refreshId,
                NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded,
                1,
                "nyxid_catalog_refresh_superseded",
                RefreshStartedAt));

        var transport = await transportObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        transport.RootActorId.Should().Be(actorId);
        transport.SessionId.Should().Be(refreshId);
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // Test cleanup only observes a task whose public result was already asserted.
        }
    }

    private static EventEnvelope CommittedEnvelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Version = 1,
                    EventData = Any.Pack(payload),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            }),
        };

    private sealed class RecordingActivationService
        : IProjectionScopeActivationService<NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<NyxIdAuthorizationCatalogRefreshObservationRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new NyxIdAuthorizationCatalogRefreshObservationRuntimeLease(
                new NyxIdAuthorizationCatalogRefreshObservationProjectionContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                    SessionId = request.SessionId,
                }));
        }
    }

    private sealed class EmptyCatalogQueryPort : INyxIdAuthorizationCatalogQueryPort
    {
        public Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<NyxIdAuthorizationCatalogSnapshot?>(null);
        }
    }

    private sealed class TestNyxIdApiClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    private sealed class IncompleteProviderHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _exited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Blocked => _blocked.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

        public Task Exited => _exited.Task;

        public bool ProviderCompleted => _response.Task.IsCompleted;

        public void CompleteCanceled() => _response.TrySetCanceled();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(
                () => _cancellationObserved.TrySetResult(true));
            _blocked.TrySetResult(true);
            try
            {
                return await _response.Task;
            }
            finally
            {
                _exited.TrySetResult(true);
            }
        }
    }

    private sealed class PartiallyFailingActivationService
        : IProjectionScopeActivationService<NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> PartiallyCreatedScopes { get; } = [];

        public Task<NyxIdAuthorizationCatalogRefreshObservationRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PartiallyCreatedScopes.Add(request);
            throw new InvalidOperationException("relay readiness failed after scope creation");
        }
    }

    private sealed class RecordingAttachExistingLeaseLookup
        : IProjectionScopeAttachExistingLeaseLookup<
            NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public NyxIdAuthorizationCatalogRefreshObservationRuntimeLease? Lease { get; init; }

        public Task<NyxIdAuthorizationCatalogRefreshObservationRuntimeLease?> TryGetAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Lease);
        }
    }

    private sealed class RecordingSessionEventHub
        : IProjectionSessionEventHub<NyxIdAuthorizationCatalogRefreshCommittedOutcome>
    {
        public List<(
            string RootActorId,
            string SessionId,
            NyxIdAuthorizationCatalogRefreshCommittedOutcome Outcome)> Published { get; } = [];

        public (string RootActorId, string SessionId)? LastSubscription { get; private set; }

        public Func<NyxIdAuthorizationCatalogRefreshCommittedOutcome, ValueTask>?
            SubscriptionHandler { get; private set; }

        public Task PublishAsync(
            string rootActorId,
            string sessionId,
            NyxIdAuthorizationCatalogRefreshCommittedOutcome evt,
            CancellationToken ct = default)
        {
            Published.Add((rootActorId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<NyxIdAuthorizationCatalogRefreshCommittedOutcome, ValueTask> handler,
            CancellationToken ct = default)
        {
            LastSubscription = (rootActorId, sessionId);
            SubscriptionHandler = handler;
            return Task.FromResult<IAsyncDisposable>(new NoopSubscription());
        }
    }

    private sealed class RecordingEventSink
        : IEventSink<NyxIdAuthorizationCatalogRefreshCommittedOutcome>
    {
        public List<NyxIdAuthorizationCatalogRefreshCommittedOutcome> Events { get; } = [];

        public void Push(NyxIdAuthorizationCatalogRefreshCommittedOutcome evt) => Events.Add(evt);

        public ValueTask PushAsync(
            NyxIdAuthorizationCatalogRefreshCommittedOutcome evt,
            CancellationToken ct = default)
        {
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<NyxIdAuthorizationCatalogRefreshCommittedOutcome> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopSubscription : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
