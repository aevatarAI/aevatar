using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionRuntimeRegistrationTests
{
    [Fact]
    public void ProjectionRuntimeRegistrations_ShouldRegisterOneRedactionHook_WhenRepeatedOrComposed()
    {
        Action<IServiceCollection>[] registrationPaths =
        [
            services => services.AddProjectionMaterializationRuntimeCore<
                TestMaterializationContext,
                TestMaterializationLease,
                ProjectionMaterializationScopeGAgent<TestMaterializationContext>>(
                scopeKey => new TestMaterializationContext
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                },
                context => new TestMaterializationLease(context)),
            services => services.AddEventSinkProjectionRuntimeCore<
                TestSessionContext,
                TestSessionLease,
                StringValue,
                ProjectionSessionScopeGAgent<TestSessionContext>>(
                scopeKey => new TestSessionContext
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                    SessionId = scopeKey.SessionId,
                },
                context => new TestSessionLease(context)),
            services => services.AddProjectionScopeStatusRuntimeCore(),
        ];

        foreach (var register in registrationPaths)
        {
            var services = new ServiceCollection();
            register(services);
            register(services);
            AssertSingleRedactionHook(services);
        }

        var composed = new ServiceCollection();
        foreach (var register in registrationPaths)
        {
            register(composed);
            register(composed);
        }
        AssertSingleRedactionHook(composed);
    }

    [Fact]
    public async Task AddProjectionMaterializationRuntimeCore_ShouldRegisterLifecycleAndAdministrationServices()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);
        services.AddSingleton<IStreamForwardingRegistry>(dispatchPort);
        services.AddSingleton<IStreamForwardingBindingAuthority>(dispatchPort);

        services.AddProjectionMaterializationRuntimeCore<
            TestMaterializationContext,
            TestMaterializationLease,
            ProjectionMaterializationScopeGAgent<TestMaterializationContext>>(
            scopeKey => new TestMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new TestMaterializationLease(context));

        await using var provider = services.BuildServiceProvider();
        var contextFactory = provider.GetRequiredService<Func<ProjectionRuntimeScopeKey, TestMaterializationContext>>();
        var activation = provider.GetRequiredService<IProjectionScopeActivationService<TestMaterializationLease>>();
        var release = provider.GetRequiredService<IProjectionScopeReleaseService<TestMaterializationLease>>();
        provider.GetRequiredService<IProjectionFailureReplayService>().Should().NotBeNull();
        provider.GetRequiredService<IProjectionFailureAlertSink>().Should().NotBeNull();

        var scopeKey = new ProjectionRuntimeScopeKey("actor-1", "projection-a", ProjectionRuntimeMode.DurableMaterialization);
        var context = contextFactory(scopeKey);
        context.RootActorId.Should().Be("actor-1");
        context.ProjectionKind.Should().Be("projection-a");

        var lease = await activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = "actor-1",
            ProjectionKind = "projection-a",
            Mode = ProjectionRuntimeMode.DurableMaterialization,
        });
        await release.ReleaseIfIdleAsync(lease);

        runtime.CreatedActorIds.Should().ContainSingle()
            .Which.Should().Be(ProjectionScopeActorId.Build(scopeKey));
        runtime.CreatedByKind.Should().ContainSingle().Which.Should().Be((
            "projection.materialization-scope.test-materialization-context",
            ProjectionScopeActorId.Build(scopeKey)));
        dispatchPort.Dispatched.Should().HaveCount(2);
        dispatchPort.Dispatched[0].actorId.Should().Be(ProjectionScopeActorId.Build(scopeKey));
        dispatchPort.Dispatched[0].command.Payload!.Unpack<EnsureProjectionScopeCommand>().ProjectionKind.Should().Be("projection-a");
        dispatchPort.Dispatched[1].command.Payload!.Unpack<ReleaseProjectionScopeCommand>().ProjectionKind.Should().Be("projection-a");
    }

    private static void AssertSingleRedactionHook(IServiceCollection services) =>
        services.Where(descriptor =>
                descriptor.ServiceType == typeof(ICommittedStatePublicationHook) &&
                descriptor.ImplementationType == typeof(ProjectionScopeCommittedStateRedactionHook))
            .Should()
            .ContainSingle();

    [Fact]
    public async Task AddProjectionMaterializationRuntimeCore_ShouldReleaseSessionScopedMaterialization()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);
        services.AddSingleton<IStreamForwardingRegistry>(dispatchPort);
        services.AddSingleton<IStreamForwardingBindingAuthority>(dispatchPort);

        services.AddProjectionMaterializationRuntimeCore<
            TestSessionScopedMaterializationContext,
            TestSessionScopedMaterializationLease,
            ProjectionMaterializationScopeGAgent<TestSessionScopedMaterializationContext>>(
            scopeKey => new TestSessionScopedMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                SessionId = scopeKey.SessionId,
            },
            context => new TestSessionScopedMaterializationLease(context));

        await using var provider = services.BuildServiceProvider();
        var activation = provider.GetRequiredService<IProjectionScopeActivationService<TestSessionScopedMaterializationLease>>();
        var release = provider.GetRequiredService<IProjectionScopeReleaseService<TestSessionScopedMaterializationLease>>();

        var scopeKey = new ProjectionRuntimeScopeKey(
            "actor-1",
            "projection-a",
            ProjectionRuntimeMode.DurableMaterialization,
            "correlation-1");
        var lease = await activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = scopeKey.RootActorId,
            ProjectionKind = scopeKey.ProjectionKind,
            Mode = scopeKey.Mode,
            SessionId = scopeKey.SessionId,
        });
        await release.ReleaseIfIdleAsync(lease);

        runtime.CreatedActorIds.Should().ContainSingle()
            .Which.Should().Be(ProjectionScopeActorId.Build(scopeKey));
        dispatchPort.Dispatched.Should().HaveCount(2);
        dispatchPort.Dispatched[1].actorId.Should().Be(ProjectionScopeActorId.Build(scopeKey));
        dispatchPort.Dispatched[1].command.Payload!.Unpack<ReleaseProjectionScopeCommand>().SessionId
            .Should().Be("correlation-1");
    }

    [Fact]
    public async Task AddProjectionMaterializationRuntimeCore_ShouldNotWriteObservationRelayFromActivationService()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var streamProvider = new RecordingStreamProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);
        services.AddSingleton<IStreamForwardingRegistry>(dispatchPort);
        services.AddSingleton<IStreamForwardingBindingAuthority>(dispatchPort);
        services.AddSingleton<IStreamProvider>(streamProvider);

        services.AddProjectionMaterializationRuntimeCore<
            TestMaterializationContext,
            TestMaterializationLease,
            ProjectionMaterializationScopeGAgent<TestMaterializationContext>>(
            scopeKey => new TestMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new TestMaterializationLease(context));

        await using var provider = services.BuildServiceProvider();
        var activation = provider.GetRequiredService<IProjectionScopeActivationService<TestMaterializationLease>>();

        await activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = "actor-relay",
            ProjectionKind = "projection-relay",
            Mode = ProjectionRuntimeMode.DurableMaterialization,
        });

        streamProvider.Streams.Should().BeEmpty();
        dispatchPort.Dispatched.Should().ContainSingle();
        var command = dispatchPort.Dispatched[0].command.Payload!.Unpack<EnsureProjectionScopeCommand>();
        command.RootActorId.Should().Be("actor-relay");
    }

    [Fact]
    public async Task AddProjectionMaterializationRuntimeCore_ShouldPreserveDurableScope_WhenRelayReadinessFails()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);
        var failingRegistry = new FailingStreamForwardingRegistry(
            new TimeoutException("relay unavailable"),
            successfulReadsBeforeFailure: 2);
        services.AddSingleton<IStreamForwardingRegistry>(failingRegistry);
        services.AddSingleton<IStreamForwardingBindingAuthority>(failingRegistry);

        services.AddProjectionMaterializationRuntimeCore<
            TestMaterializationContext,
            TestMaterializationLease,
            ProjectionMaterializationScopeGAgent<TestMaterializationContext>>(
            scopeKey => new TestMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new TestMaterializationLease(context));

        await using var provider = services.BuildServiceProvider();
        var activation = provider.GetRequiredService<IProjectionScopeActivationService<TestMaterializationLease>>();
        var scopeKey = new ProjectionRuntimeScopeKey(
            "actor-durable-relay-failure",
            "projection-durable-relay-failure",
            ProjectionRuntimeMode.DurableMaterialization);

        var act = () => activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = scopeKey.RootActorId,
            ProjectionKind = scopeKey.ProjectionKind,
            Mode = scopeKey.Mode,
        });

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("relay unavailable");
        dispatchPort.Dispatched.Should().ContainSingle();
        dispatchPort.Dispatched[0].actorId.Should().Be(ProjectionScopeActorId.Build(scopeKey));
        dispatchPort.Dispatched[0].command.Payload!.Unpack<EnsureProjectionScopeCommand>().Mode
            .Should().Be(ProjectionScopeMode.DurableMaterialization);
    }

    [Fact]
    public async Task AddEventSinkProjectionRuntimeCore_ShouldReleaseSessionScope_WhenRelayReadinessFails()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);
        var failingRegistry = new FailingStreamForwardingRegistry(
            new TimeoutException("relay unavailable"),
            successfulReadsBeforeFailure: 2);
        services.AddSingleton<IStreamForwardingRegistry>(failingRegistry);
        services.AddSingleton<IStreamForwardingBindingAuthority>(failingRegistry);

        services.AddEventSinkProjectionRuntimeCore<
            TestSessionContext,
            TestSessionLease,
            StringValue,
            ProjectionSessionScopeGAgent<TestSessionContext>>(
            scopeKey => new TestSessionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                SessionId = scopeKey.SessionId,
            },
            context => new TestSessionLease(context));

        await using var provider = services.BuildServiceProvider();
        var activation = provider.GetRequiredService<IProjectionScopeActivationService<TestSessionLease>>();
        var scopeKey = new ProjectionRuntimeScopeKey(
            "actor-relay-failure",
            "projection-relay-failure",
            ProjectionRuntimeMode.SessionObservation,
            "session-relay-failure");

        var act = () => activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = scopeKey.RootActorId,
            ProjectionKind = scopeKey.ProjectionKind,
            Mode = scopeKey.Mode,
            SessionId = scopeKey.SessionId,
        });

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("relay unavailable");
        dispatchPort.Dispatched.Select(item => item.command.Payload!.TypeUrl).Should().Equal(
            Any.Pack(new EnsureProjectionScopeCommand()).TypeUrl,
            Any.Pack(new ReleaseProjectionScopeCommand()).TypeUrl);
        dispatchPort.Dispatched.Should().OnlyContain(item => item.actorId == ProjectionScopeActorId.Build(scopeKey));
        dispatchPort.Dispatched[1].command.Payload!.Unpack<ReleaseProjectionScopeCommand>().SessionId
            .Should().Be(scopeKey.SessionId);
    }

    [Fact]
    public async Task AddProjectionMaterializationRuntimeCore_ShouldRegisterAttachExistingLeaseLookup()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);
        services.AddSingleton<IStreamForwardingRegistry>(dispatchPort);
        services.AddSingleton<IStreamForwardingBindingAuthority>(dispatchPort);

        services.AddProjectionMaterializationRuntimeCore<
            TestSessionScopedMaterializationContext,
            TestSessionScopedMaterializationLease,
            ProjectionMaterializationScopeGAgent<TestSessionScopedMaterializationContext>>(
            scopeKey => new TestSessionScopedMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                SessionId = scopeKey.SessionId,
            },
            context => new TestSessionScopedMaterializationLease(context));

        await using var provider = services.BuildServiceProvider();
        var lookup = provider.GetRequiredService<IProjectionScopeAttachExistingLeaseLookup<TestSessionScopedMaterializationLease>>();
        var scopeKey = new ProjectionRuntimeScopeKey(
            "actor-lookup",
            "projection-lookup",
            ProjectionRuntimeMode.DurableMaterialization,
            "correlation-lookup");

        var missing = await lookup.TryGetAsync(new ProjectionScopeStartRequest
        {
            RootActorId = scopeKey.RootActorId,
            ProjectionKind = scopeKey.ProjectionKind,
            Mode = scopeKey.Mode,
            SessionId = scopeKey.SessionId,
        });
        runtime.ExistingActorIds.Add(ProjectionScopeActorId.Build(scopeKey));
        var lease = await lookup.TryGetAsync(new ProjectionScopeStartRequest
        {
            RootActorId = scopeKey.RootActorId,
            ProjectionKind = scopeKey.ProjectionKind,
            Mode = scopeKey.Mode,
            SessionId = scopeKey.SessionId,
        });

        missing.Should().BeNull();
        lease.Should().NotBeNull();
        lease!.Context.RootActorId.Should().Be("actor-lookup");
        lease.Context.ProjectionKind.Should().Be("projection-lookup");
        lease.Context.SessionId.Should().Be("correlation-lookup");
        runtime.CreatedActorIds.Should().BeEmpty();
        dispatchPort.Dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectionScopeAttachExistingLeaseLookup_ShouldValidateInputsAndCancellation()
    {
        var runtime = new RecordingActorRuntime();
        var lookup = new ProjectionScopeAttachExistingLeaseLookup<TestSessionScopedMaterializationLease, TestSessionScopedMaterializationContext>(
            runtime,
            static request => new TestSessionScopedMaterializationContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
                SessionId = request.SessionId,
            },
            static (_, context) => new TestSessionScopedMaterializationLease(context));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> nullRequest = () => lookup.TryGetAsync(null!);
        Func<Task> canceledRequest = () => lookup.TryGetAsync(new ProjectionScopeStartRequest
        {
            RootActorId = "actor-canceled",
            ProjectionKind = "projection-canceled",
            Mode = ProjectionRuntimeMode.DurableMaterialization,
        }, cts.Token);

        await nullRequest.Should().ThrowAsync<ArgumentNullException>();
        await canceledRequest.Should().ThrowAsync<OperationCanceledException>();
        runtime.CreatedActorIds.Should().BeEmpty();
    }

    [Fact]
    public void ProjectionScopeAttachExistingLeaseLookup_ShouldValidateConstructorDependencies()
    {
        var runtime = new RecordingActorRuntime();
        Func<ProjectionScopeStartRequest, TestSessionScopedMaterializationContext> contextFactory =
            static request => new TestSessionScopedMaterializationContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
                SessionId = request.SessionId,
            };
        Func<ProjectionRuntimeScopeKey, TestSessionScopedMaterializationContext, TestSessionScopedMaterializationLease> leaseFactory =
            static (_, context) => new TestSessionScopedMaterializationLease(context);

        var nullRuntime = () => new ProjectionScopeAttachExistingLeaseLookup<TestSessionScopedMaterializationLease, TestSessionScopedMaterializationContext>(
            null!,
            contextFactory,
            leaseFactory);
        var nullContextFactory = () => new ProjectionScopeAttachExistingLeaseLookup<TestSessionScopedMaterializationLease, TestSessionScopedMaterializationContext>(
            runtime,
            null!,
            leaseFactory);
        var nullLeaseFactory = () => new ProjectionScopeAttachExistingLeaseLookup<TestSessionScopedMaterializationLease, TestSessionScopedMaterializationContext>(
            runtime,
            contextFactory,
            null!);

        nullRuntime.Should().Throw<ArgumentNullException>().WithParameterName("runtime");
        nullContextFactory.Should().Throw<ArgumentNullException>().WithParameterName("contextFactory");
        nullLeaseFactory.Should().Throw<ArgumentNullException>().WithParameterName("leaseFactory");
    }

    [Fact]
    public async Task AddEventSinkProjectionRuntimeCore_ShouldRegisterSessionLifecycleAndSessionScopeContext()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);
        services.AddSingleton<IStreamForwardingRegistry>(dispatchPort);
        services.AddSingleton<IStreamForwardingBindingAuthority>(dispatchPort);

        services.AddEventSinkProjectionRuntimeCore<
            TestSessionContext,
            TestSessionLease,
            StringValue,
            ProjectionSessionScopeGAgent<TestSessionContext>>(
            scopeKey => new TestSessionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                SessionId = scopeKey.SessionId,
            },
            context => new TestSessionLease(context));

        await using var provider = services.BuildServiceProvider();
        var contextFactory = provider.GetRequiredService<Func<ProjectionRuntimeScopeKey, TestSessionContext>>();
        var activation = provider.GetRequiredService<IProjectionScopeActivationService<TestSessionLease>>();
        var release = provider.GetRequiredService<IProjectionScopeReleaseService<TestSessionLease>>();

        var scopeKey = new ProjectionRuntimeScopeKey("actor-2", "projection-b", ProjectionRuntimeMode.SessionObservation, "session-9");
        var context = contextFactory(scopeKey);
        context.RootActorId.Should().Be("actor-2");
        context.ProjectionKind.Should().Be("projection-b");
        context.SessionId.Should().Be("session-9");

        var lease = await activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = "actor-2",
            ProjectionKind = "projection-b",
            Mode = ProjectionRuntimeMode.SessionObservation,
            SessionId = "session-9",
        });
        await release.ReleaseIfIdleAsync(lease);

        runtime.CreatedActorIds.Should().ContainSingle()
            .Which.Should().Be(ProjectionScopeActorId.Build(scopeKey));
        runtime.CreatedByKind.Should().ContainSingle().Which.Should().Be((
            "projection.session-scope.test-session-context",
            ProjectionScopeActorId.Build(scopeKey)));
        dispatchPort.Dispatched.Should().HaveCount(2);
        dispatchPort.Dispatched[0].command.Payload!.Unpack<EnsureProjectionScopeCommand>().SessionId.Should().Be("session-9");
        dispatchPort.Dispatched[1].command.Payload!.Unpack<ReleaseProjectionScopeCommand>().SessionId.Should().Be("session-9");
    }

    [Fact]
    public async Task AddEventSinkProjectionRuntimeCore_ShouldRegisterAttachExistingSessionLeaseLookup()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);
        services.AddSingleton<IStreamForwardingRegistry>(dispatchPort);
        services.AddSingleton<IStreamForwardingBindingAuthority>(dispatchPort);

        services.AddEventSinkProjectionRuntimeCore<
            TestSessionContext,
            TestSessionLease,
            StringValue,
            ProjectionSessionScopeGAgent<TestSessionContext>>(
            scopeKey => new TestSessionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                SessionId = scopeKey.SessionId,
            },
            context => new TestSessionLease(context));

        await using var provider = services.BuildServiceProvider();
        var lookup = provider.GetRequiredService<IProjectionScopeAttachExistingLeaseLookup<TestSessionLease>>();
        var scopeKey = new ProjectionRuntimeScopeKey(
            "actor-session",
            "projection-session",
            ProjectionRuntimeMode.SessionObservation,
            "session-lookup");
        runtime.ExistingActorIds.Add(ProjectionScopeActorId.Build(scopeKey));

        var lease = await lookup.TryGetAsync(new ProjectionScopeStartRequest
        {
            RootActorId = scopeKey.RootActorId,
            ProjectionKind = scopeKey.ProjectionKind,
            Mode = scopeKey.Mode,
            SessionId = scopeKey.SessionId,
        });

        lease.Should().NotBeNull();
        lease!.Context.RootActorId.Should().Be("actor-session");
        lease.Context.SessionId.Should().Be("session-lookup");
        runtime.CreatedActorIds.Should().BeEmpty();
        dispatchPort.Dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectionFailureReplayService_ShouldOnlyDispatchForExistingScope()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var service = new ProjectionFailureReplayService(runtime, dispatchPort, dispatchPort);
        var scopeKey = new ProjectionRuntimeScopeKey("actor-3", "projection-c", ProjectionRuntimeMode.DurableMaterialization);
        runtime.ExistingActorIds.Add(ProjectionScopeActorId.Build(scopeKey));

        var replayed = await service.ReplayAsync(scopeKey, 0);
        var missing = await service.ReplayAsync(
            new ProjectionRuntimeScopeKey("missing", "projection-d", ProjectionRuntimeMode.DurableMaterialization),
            3);

        replayed.Should().BeTrue();
        missing.Should().BeFalse();
        dispatchPort.Dispatched.Should().ContainSingle();
        var replay = dispatchPort.Dispatched[0].command.Payload!.Unpack<ReplayProjectionFailuresCommand>();
        replay.MaxItems.Should().Be(1);
        replay.AutomaticRecovery.Should().BeFalse();
    }

    [Fact]
    public async Task ProjectionFailureReplayService_ShouldDispatchTypedAutomaticRecoveryCommand()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var service = new ProjectionFailureReplayService(runtime, dispatchPort, dispatchPort);
        var scopeKey = new ProjectionRuntimeScopeKey(
            "actor-automatic",
            "projection-automatic",
            ProjectionRuntimeMode.SessionObservation,
            "session-automatic");
        var actorId = ProjectionScopeActorId.Build(scopeKey);
        runtime.ExistingActorIds.Add(actorId);

        var replayed = await service.ReplayAutomaticallyAsync(
            scopeKey,
            observedScopeStateVersion: 17,
            maxItems: 0);

        replayed.Should().BeTrue();
        dispatchPort.Dispatched.Should().ContainSingle();
        var dispatched = dispatchPort.Dispatched[0];
        dispatched.actorId.Should().Be(actorId);
        dispatched.command.Route.PublisherActorId.Should().Be("projection.scope.automatic-recovery");
        dispatched.command.Route.GetTargetActorId().Should().Be(actorId);
        var command = dispatched.command.Payload!.Unpack<ReplayProjectionFailuresCommand>();
        command.MaxItems.Should().Be(1);
        command.AutomaticRecovery.Should().BeTrue();
        command.ObservedScopeStateVersion.Should().Be(17);
    }

    [Fact]
    public async Task ProjectionFailureReplayService_WhenScopeStateNeedsRecovery_ShouldRecreateFromDurableRelayKindBeforeReplay()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var service = new ProjectionFailureReplayService(runtime, dispatchPort, dispatchPort);
        var scopeKey = new ProjectionRuntimeScopeKey(
            "actor-recovery",
            "projection-recovery",
            ProjectionRuntimeMode.DurableMaterialization);
        var actorId = ProjectionScopeActorId.Build(scopeKey);
        await dispatchPort.UpsertAsync(ProjectionScopeObservationRelayBinding.Create(
            scopeKey.RootActorId,
            actorId,
            "projection.materialization-scope.recovery-test",
            12));

        var replayed = await service.ReplayAutomaticallyAsync(
            scopeKey,
            observedScopeStateVersion: 23,
            maxItems: 5);

        replayed.Should().BeTrue();
        runtime.CreatedByKind.Should().ContainSingle().Which.Should().Be(
            ("projection.materialization-scope.recovery-test", actorId));
        dispatchPort.Dispatched.Should().ContainSingle();
        dispatchPort.Dispatched[0].actorId.Should().Be(actorId);
    }

    [Fact]
    public async Task ProjectionFailureReplayService_WhenMissingScopeHasNoTypedDurableRelay_ShouldNotGuessIdentity()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort(runtime);
        var service = new ProjectionFailureReplayService(runtime, dispatchPort, dispatchPort);
        var scopeKey = new ProjectionRuntimeScopeKey(
            "actor-without-recovery-evidence",
            "projection-without-recovery-evidence",
            ProjectionRuntimeMode.DurableMaterialization);

        var replayed = await service.ReplayAutomaticallyAsync(
            scopeKey,
            observedScopeStateVersion: 1,
            maxItems: 1);

        replayed.Should().BeFalse();
        runtime.CreatedByKind.Should().BeEmpty();
        dispatchPort.Dispatched.Should().BeEmpty();
    }

    [Fact]
    public void ProjectionFailureRetentionPolicy_ShouldTrimOldestFailures()
    {
        var failures = new Google.Protobuf.Collections.RepeatedField<ProjectionFailureDiagnostic>();
        failures.Add(new ProjectionFailureDiagnostic { FailureId = "f1" });
        failures.Add(new ProjectionFailureDiagnostic { FailureId = "f2" });
        failures.Add(new ProjectionFailureDiagnostic { FailureId = "f3" });

        var dropped = ProjectionFailureRetentionPolicy.Trim(failures, 2);

        failures.Select(x => x.FailureId).Should().Equal("f2", "f3");
        dropped.Select(x => x.FailureId).Should().Equal("f1");
    }

    [Fact]
    public async Task LoggingProjectionFailureAlertSink_ShouldValidateInputs_AndComplete()
    {
        var sink = new LoggingProjectionFailureAlertSink();
        var alert = new ProjectionFailureAlert(
            ProjectionFailureAlertKind.FailureRecorded,
            new ProjectionRuntimeScopeKey("actor-4", "projection-d", ProjectionRuntimeMode.DurableMaterialization),
            "failure-1",
            "projection-execution",
            "event-1",
            "type://event",
            9,
            "boom",
            1,
            0,
            [],
            0,
            DateTimeOffset.UtcNow);

        Func<Task> nullAct = () => sink.PublishAsync(null!);
        await nullAct.Should().ThrowAsync<ArgumentNullException>();

        await sink.PublishAsync(alert);
    }

    [Fact]
    public void ProjectionScopeAgentRegistration_ShouldGenerateNonGenericPrimaryKind()
    {
        var registration = ProjectionScopeAgentRegistration.Create<NonGenericScopeAgent>();

        registration.Kind.Should().Be("projection.scope");
        registration.ImplementationType.Should().Be(typeof(NonGenericScopeAgent));
    }

    [Fact]
    public void ProjectionScopeAgentRegistration_ShouldRespectExplicitConcreteScopeDeclaration()
    {
        var registration = ProjectionScopeAgentRegistration.Create<ExplicitScopeAgent>();

        registration.Kind.Should().Be("projection.materialization-scope.explicit-test");
        registration.ImplementationType.Should().Be(typeof(ExplicitScopeAgent));
        registration.StateContractType.Should().Be(typeof(ProjectionScopeState));
        registration.StateSchemaVersion.Should().Be(3);
    }

    [Fact]
    public void ProjectionScopeAgentRegistration_ShouldGenerateFallbackGenericPrimaryKind()
    {
        var registration = ProjectionScopeAgentRegistration.Create<FallbackScopeAgent<TestFallbackScopeContext>>();

        registration.Kind.Should().Be("projection.scope.test-fallback-scope-context");
        registration.ImplementationType.Should().Be(typeof(FallbackScopeAgent<TestFallbackScopeContext>));
    }

    [Fact]
    public void ProjectionScopeAgentRegistration_ShouldRegisterExpectedStateSchemaMigrations()
    {
        var durable = ProjectionScopeAgentRegistration
            .Create<ProjectionMaterializationScopeGAgent<TestMaterializationContext>>();

        durable.StateSchemaVersion.Should().Be(1);
        var migration = durable.PrebuiltStateMigrationSteps.Should().ContainSingle().Subject;
        migration.FromStateVersion.Should().Be(0);
        migration.ToStateVersion.Should().Be(1);
        migration.StateContractType.Should().Be(typeof(ProjectionScopeState));
        migration.MigrationType.Should().Be(typeof(ProjectionScopeStateActivationSealMigration));
        migration.RequiredCapability.Should().Be(RuntimeFleetCapability.ProjectionScopeStatusTerminalV3);
        migration.RequiredContractId.Should().Be(
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1);
        migration.RequiredContractVersion.Should().Be(
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion);
        migration.RequiredGateStatus.Should().Be(RuntimeFleetCapabilityGateStatus.Open);

        var terminal = ProjectionScopeAgentRegistration.Create<ProjectionScopeStatusGAgent>();
        terminal.StateContractType.Should().Be(typeof(ProjectionScopeStatusTerminalState));
        terminal.StateSchemaVersion.Should().Be(ProjectionScopeStatusGAgent.SupportedStateSchemaVersion);
        terminal.StateMigrationTypes.Should().Equal(
            typeof(ProjectionScopeStatusTerminalStateV0ToV1Migration));
        terminal.PrebuiltStateMigrationSteps.Should().BeNullOrEmpty();
        var terminalMigration = new ProjectionScopeStatusTerminalStateV0ToV1Migration();
        terminalMigration.FromStateVersion.Should().Be(0);
        terminalMigration.ToStateVersion.Should().Be(1);

        var session = ProjectionScopeAgentRegistration
            .Create<ProjectionSessionScopeGAgent<TestSessionContext>>();
        session.StateSchemaVersion.Should().Be(0);
        session.StateMigrationTypes.Should().BeNullOrEmpty();
        session.PrebuiltStateMigrationSteps.Should().BeNullOrEmpty();
    }

    [Fact]
    public void RetiredProjectionScopeTokens_ShouldNotRetireLiveMaterializationScopeKinds()
    {
        // A retired-actor spec must NEVER list a currently-live materialization scope
        // kind. RetiredActorTarget.MatchesRuntimeKind compares the probed runtime kind
        // to the retired tokens by exact ordinal equality, so a live kind in the retired
        // list destroys the live projection scope on every startup cleanup pass, leaving
        // its read model un-materialized.
        //
        // Regression guard for #1763: the legacy CLR-name tokens
        // ("Aevatar.GAgents.ChannelRuntime.*MaterializationContext") were translated into
        // the live "projection.materialization-scope.*" kinds. Because the kind is derived
        // from the context type's *simple name* (namespace-independent), the old and new
        // materialization contexts collapse to the same kind, so retiring it silently
        // killed the channel/device/scheduled read-model projections on every boot.
        var userAgentCatalogKind = ProjectionScopeAgentRegistration
            .Create<ProjectionMaterializationScopeGAgent<UserAgentCatalogMaterializationContext>>()
            .Kind;
        var channelBotRegistrationKind = ProjectionScopeAgentRegistration
            .Create<ProjectionMaterializationScopeGAgent<ChannelBotRegistrationMaterializationContext>>()
            .Kind;
        var deviceRegistrationKind = ProjectionScopeAgentRegistration
            .Create<ProjectionMaterializationScopeGAgent<DeviceRegistrationMaterializationContext>>()
            .Kind;

        var scheduledTokens = new ScheduledRetiredActorSpec()
            .Targets
            .SelectMany(static target => target.RetiredKindTokens)
            .ToArray();
        var channelTokens = new ChannelRuntimeRetiredActorSpec()
            .Targets
            .SelectMany(static target => target.RetiredKindTokens)
            .ToArray();
        var deviceTokens = new DeviceRetiredActorSpec()
            .Targets
            .SelectMany(static target => target.RetiredKindTokens)
            .ToArray();

        userAgentCatalogKind.Should().Be("projection.materialization-scope.user-agent-catalog-materialization-context");
        channelBotRegistrationKind.Should().Be("projection.materialization-scope.channel-bot-registration-materialization-context");
        deviceRegistrationKind.Should().Be("projection.materialization-scope.device-registration-materialization-context");
        scheduledTokens.Should().NotContain(userAgentCatalogKind);
        channelTokens.Should().NotContain(channelBotRegistrationKind);
        deviceTokens.Should().NotContain(deviceRegistrationKind);
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public HashSet<string> ExistingActorIds { get; } = [];
        public List<string> CreatedActorIds { get; } = [];
        public List<(string agentKind, string actorId)> CreatedByKind { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            ExistingActorIds.Add(actorId);
            CreatedActorIds.Add(actorId);
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(agentKind);
            var actorId = id ?? Guid.NewGuid().ToString("N");
            ExistingActorIds.Add(actorId);
            CreatedActorIds.Add(actorId);
            CreatedByKind.Add((agentKind, actorId));
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(ExistingActorIds.Contains(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort
        : IActorDispatchPort,
          IStreamForwardingRegistry,
          IStreamForwardingBindingAuthority
    {
        private readonly RecordingActorRuntime _runtime;
        private readonly Dictionary<(string Source, string Target), StreamForwardingBinding> _bindings = [];

        public RecordingActorDispatchPort(RecordingActorRuntime runtime)
        {
            _runtime = runtime;
        }

        public List<(string actorId, EventEnvelope command)> Dispatched { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatched.Add((actorId, envelope));
            if (envelope.Payload?.Is(EnsureProjectionScopeCommand.Descriptor) == true)
            {
                var command = envelope.Payload.Unpack<EnsureProjectionScopeCommand>();
                var targetKind = _runtime.CreatedByKind
                    .Last(item => string.Equals(item.actorId, actorId, StringComparison.Ordinal))
                    .agentKind;
                _bindings[(command.RootActorId, actorId)] = ProjectionScopeObservationRelayBinding.Create(
                    command.RootActorId,
                    actorId,
                    targetKind,
                    1);
            }
            else if (envelope.Payload?.Is(ReleaseProjectionScopeCommand.Descriptor) == true)
            {
                var command = envelope.Payload.Unpack<ReleaseProjectionScopeCommand>();
                _bindings.Remove((command.RootActorId, actorId));
            }

            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            _bindings[(binding.SourceStreamId, binding.TargetStreamId)] = binding;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sourceStreamId, string targetStreamId, CancellationToken ct = default)
        {
            _bindings.Remove((sourceStreamId, targetStreamId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListBySourceAsync(
            string sourceStreamId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StreamForwardingBinding>>(
                _bindings.Values.Where(binding => binding.SourceStreamId == sourceStreamId).ToList());

        public Task<StreamForwardingBinding?> GetAsync(
            string sourceStreamId,
            string targetStreamId,
            CancellationToken ct = default) =>
            Task.FromResult(_bindings.GetValueOrDefault((sourceStreamId, targetStreamId)));
    }

    private sealed class FailingStreamForwardingRegistry(
        Exception failure,
        int successfulReadsBeforeFailure = 0)
        : IStreamForwardingRegistry,
          IStreamForwardingBindingAuthority
    {
        private int _readCount;
        public Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RemoveAsync(
            string sourceStreamId,
            string targetStreamId,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StreamForwardingBinding>> ListBySourceAsync(
            string sourceStreamId,
            CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<StreamForwardingBinding>>(failure);

        public Task<StreamForwardingBinding?> GetAsync(
            string sourceStreamId,
            string targetStreamId,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _readCount) <= successfulReadsBeforeFailure)
                return Task.FromResult<StreamForwardingBinding?>(null);

            return Task.FromException<StreamForwardingBinding?>(failure);
        }
    }

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        public Dictionary<string, RecordingStream> Streams { get; } = new(StringComparer.Ordinal);

        public IStream GetStream(string actorId)
        {
            if (!Streams.TryGetValue(actorId, out var stream))
            {
                stream = new RecordingStream(actorId);
                Streams[actorId] = stream;
            }

            return stream;
        }
    }

    private sealed class RecordingStream(string streamId) : IStream
    {
        public string StreamId { get; } = streamId;

        public List<StreamForwardingBinding> UpsertedRelays { get; } = [];

        public Task ProduceAsync<T>(T message, CancellationToken ct = default)
            where T : IMessage
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : IMessage, new()
        {
            ct.ThrowIfCancellationRequested();
            _ = handler;
            return Task.FromResult<IAsyncDisposable>(new NoOpSubscription());
        }

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UpsertedRelays.Add(binding);
            return Task.CompletedTask;
        }

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = targetStreamId;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>(UpsertedRelays);
        }

        private sealed class NoOpSubscription : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public IAgent Agent => throw new NotSupportedException();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class TestMaterializationContext : IProjectionMaterializationContext
    {
        public string RootActorId { get; init; } = string.Empty;

        public string ProjectionKind { get; init; } = string.Empty;
    }

    private sealed class TestMaterializationLease
        : ProjectionRuntimeLeaseBase,
          IProjectionContextRuntimeLease<TestMaterializationContext>
    {
        public TestMaterializationLease(TestMaterializationContext context)
            : base(context.RootActorId)
        {
            Context = context;
        }

        public TestMaterializationContext Context { get; }
    }

    private sealed class TestSessionScopedMaterializationContext : IProjectionSessionScopedMaterializationContext
    {
        public string RootActorId { get; init; } = string.Empty;

        public string ProjectionKind { get; init; } = string.Empty;

        public string SessionId { get; init; } = string.Empty;
    }

    private sealed class TestSessionScopedMaterializationLease
        : ProjectionRuntimeLeaseBase,
          IProjectionContextRuntimeLease<TestSessionScopedMaterializationContext>
    {
        public TestSessionScopedMaterializationLease(TestSessionScopedMaterializationContext context)
            : base(context.RootActorId)
        {
            Context = context;
        }

        public TestSessionScopedMaterializationContext Context { get; }
    }

    private sealed class TestSessionContext : IProjectionSessionContext
    {
        public string RootActorId { get; init; } = string.Empty;

        public string ProjectionKind { get; init; } = string.Empty;

        public string SessionId { get; init; } = string.Empty;
    }

    // Refactor (iter367/cluster-issue377): Old pattern: test lease implemented IProjectionPortSessionLease.
    // Refactor (iter367/cluster-issue377): Old pattern: ScopeId repeated Context.RootActorId.
    // Refactor (iter367/cluster-issue377): New principle: test registration only requires typed context lease.
    // Refactor (iter367/cluster-issue377): New principle: assertions read RootActorId from Context.
    private sealed class TestSessionLease
        : EventSinkProjectionRuntimeLeaseBase<StringValue>,
          IProjectionContextRuntimeLease<TestSessionContext>
    {
        public TestSessionLease(TestSessionContext context)
            : base(context.RootActorId)
        {
            Context = context;
        }

        public TestSessionContext Context { get; }

        public string SessionId => Context.SessionId;
    }

    private sealed class TestFallbackScopeContext;

    private sealed class NonGenericScopeAgent : IAgent
    {
        public string Id => "non-generic";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("non-generic");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
    }

    [GAgent("projection.materialization-scope.explicit-test", StateSchemaVersion = 3)]
    private sealed class ExplicitScopeAgent
        : ProjectionMaterializationScopeGAgentBase<TestMaterializationContext>;

    private sealed class FallbackScopeAgent<TContext> : IAgent
    {
        public string Id => "fallback";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("fallback");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
    }
}
