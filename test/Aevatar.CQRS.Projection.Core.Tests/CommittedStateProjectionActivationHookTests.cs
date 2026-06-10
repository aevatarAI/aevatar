using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions.EventSourcing;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class CommittedStateProjectionActivationHookTests
{
    [Fact]
    public async Task BeforePublishAsync_ShouldDispatchExistingProjectionScopeStartRequest()
    {
        var activation = new RecordingActivationService<TestLease>();
        var hook = CreateHook(
            [new StaticPlanProvider(BuildPlan("actor-1", "projection-a", typeof(TestLease)))],
            services => services.AddSingleton<IProjectionScopeActivationService<TestLease>>(activation));

        await hook.BeforePublishAsync(BuildContext(), CancellationToken.None);

        activation.Requests.Should().ContainSingle();
        activation.Requests[0].RootActorId.Should().Be("actor-1");
        activation.Requests[0].ProjectionKind.Should().Be("projection-a");
        activation.Requests[0].Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldOnlyEnsureProjectionScopeBeforeNormalPublication()
    {
        var activation = new RecordingActivationService<TestLease>();
        var dispatchPort = new RecordingActorDispatchPort();
        var hook = CreateHook(
            [new StaticPlanProvider(BuildPlan("actor-1", "projection-a", typeof(TestLease)))],
            services =>
            {
                services.AddSingleton<IProjectionScopeActivationService<TestLease>>(activation);
                services.AddSingleton<IActorDispatchPort>(dispatchPort);
            });

        await hook.BeforePublishAsync(BuildContext(), CancellationToken.None);

        activation.Requests.Should().ContainSingle();
        dispatchPort.Dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldDeduplicateDuplicatePlansWithinOnePublication()
    {
        var activation = new RecordingActivationService<TestLease>();
        var duplicate = BuildPlan("actor-1", "projection-a", typeof(TestLease));
        var hook = CreateHook(
            [new StaticPlanProvider(duplicate, duplicate)],
            services => services.AddSingleton<IProjectionScopeActivationService<TestLease>>(activation));

        await hook.BeforePublishAsync(BuildContext(), CancellationToken.None);

        activation.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldContinuePublication_WhenProviderFails()
    {
        var activation = new RecordingActivationService<TestLease>();
        var hook = CreateHook(
            [
                new ThrowingPlanProvider(),
                new StaticPlanProvider(BuildPlan("actor-1", "projection-a", typeof(TestLease))),
            ],
            services => services.AddSingleton<IProjectionScopeActivationService<TestLease>>(activation));

        await hook.BeforePublishAsync(BuildContext("actor-fail"), CancellationToken.None);

        activation.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldContinuePublication_WhenActivationServiceMissing()
    {
        var hook = CreateHook(
            [new StaticPlanProvider(BuildPlan("actor-1", "projection-a", typeof(TestLease)))],
            _ => { });

        var act = () => hook.BeforePublishAsync(BuildContext("actor-missing"), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Dispatcher_ShouldReportMissingActivationService()
    {
        var dispatcher = new ProjectionActivationPlanDispatcher(new ServiceCollection().BuildServiceProvider());

        var act = () => dispatcher.DispatchAsync(BuildPlan("actor-1", "projection-a", typeof(TestLease)));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Projection activation service for lease*TestLease*not registered*");
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldContinuePublication_WhenActivationDispatchFails()
    {
        var hook = CreateHook(
            [new StaticPlanProvider(BuildPlan("actor-1", "projection-a", typeof(TestLease)))],
            services => services.AddSingleton<IProjectionScopeActivationService<TestLease>>(
                new ThrowingActivationService<TestLease>()));

        var act = () => hook.BeforePublishAsync(BuildContext("actor-dispatch-fail"), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static CommittedStateProjectionActivationHook CreateHook(
        IEnumerable<IProjectionActivationPlanProvider> providers,
        Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        var serviceProvider = services.BuildServiceProvider();
        return new CommittedStateProjectionActivationHook(
            providers,
            new ProjectionActivationPlanDispatcher(serviceProvider));
    }

    private static ProjectionActivationPlan BuildPlan(string actorId, string projectionKind, System.Type leaseType) =>
        new()
        {
            LeaseType = leaseType,
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = projectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };

    private static CommittedStatePublicationContext BuildContext(string actorId = "actor-1") =>
        new()
        {
            ActorId = actorId,
            ActorType = typeof(CommittedStateProjectionActivationHookTests),
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = actorId,
                    EventId = "evt-1",
                    EventData = Any.Pack(new StringValue { Value = "committed" }),
                },
                StateRoot = Any.Pack(new StringValue { Value = "state" }),
            },
        };

    private sealed class StaticPlanProvider(params ProjectionActivationPlan[] plans) : IProjectionActivationPlanProvider
    {
        public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
        {
            _ = context;
            return plans;
        }
    }

    private sealed class ThrowingPlanProvider : IProjectionActivationPlanProvider
    {
        public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context) =>
            throw new InvalidOperationException("provider failed");
    }

    private sealed class ThrowingActivationService<TLease> : IProjectionScopeActivationService<TLease>
        where TLease : class, IProjectionRuntimeLease
    {
        public Task<TLease> EnsureAsync(ProjectionScopeStartRequest request, CancellationToken ct = default) =>
            Task.FromException<TLease>(new InvalidOperationException("activation failed"));
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope envelope)> Dispatched { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Dispatched.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingActivationService<TLease> : IProjectionScopeActivationService<TLease>
        where TLease : class, IProjectionRuntimeLease
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<TLease> EnsureAsync(ProjectionScopeStartRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult<TLease>((Activator.CreateInstance(typeof(TLease), request.RootActorId) as TLease)!);
        }
    }

    private sealed class TestLease(string rootEntityId) : IProjectionRuntimeLease
    {
        public string RootEntityId { get; } = rootEntityId;
    }
}
