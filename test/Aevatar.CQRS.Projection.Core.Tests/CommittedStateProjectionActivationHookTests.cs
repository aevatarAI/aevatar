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
    public async Task BeforePublishAsync_ShouldFailPublication_AfterAttemptingRemainingProviders()
    {
        var activation = new RecordingActivationService<TestLease>();
        var failingProvider = new ThrowingPlanProvider();
        var remainingProvider = new StaticPlanProvider(BuildPlan("actor-1", "projection-a", typeof(TestLease)));
        var hook = CreateHook(
            [
                failingProvider,
                remainingProvider,
            ],
            services => services.AddSingleton<IProjectionScopeActivationService<TestLease>>(activation));

        var act = () => hook.BeforePublishAsync(BuildContext("actor-fail"), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AggregateException>();
        exception.Which.InnerExceptions.Should().ContainSingle()
            .Which.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("provider failed");
        failingProvider.Calls.Should().Be(1);
        remainingProvider.Calls.Should().Be(1);
        activation.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldFailPublication_WhenActivationServiceMissing()
    {
        var hook = CreateHook(
            [new StaticPlanProvider(BuildPlan("actor-1", "projection-a", typeof(TestLease)))],
            _ => { });

        var act = () => hook.BeforePublishAsync(BuildContext("actor-missing"), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AggregateException>();
        exception.Which.InnerExceptions.Should().ContainSingle()
            .Which.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("Projection activation service for lease")
            .And.Contain("TestLease")
            .And.Contain("not registered");
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
    public async Task BeforePublishAsync_ShouldFailPublication_WhenActivationDispatchFails()
    {
        var hook = CreateHook(
            [new StaticPlanProvider(BuildPlan("actor-1", "projection-a", typeof(TestLease)))],
            services => services.AddSingleton<IProjectionScopeActivationService<TestLease>>(
                new ThrowingActivationService<TestLease>()));

        var act = () => hook.BeforePublishAsync(BuildContext("actor-dispatch-fail"), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AggregateException>();
        exception.Which.InnerExceptions.Should().ContainSingle()
            .Which.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("activation failed");
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldAttemptAllProviderAndPlanBranches_BeforeThrowingAggregateException()
    {
        var activation = new RecordingFailingActivationService<TestLease>("projection-a", "projection-c");
        var firstFailingProvider = new ThrowingPlanProvider("provider one failed");
        var planProvider = new StaticPlanProvider(
            BuildPlan("actor-1", "projection-a", typeof(TestLease)),
            BuildPlan("actor-1", "projection-b", typeof(TestLease)));
        var secondFailingProvider = new ThrowingPlanProvider("provider two failed");
        var remainingPlanProvider = new StaticPlanProvider(
            BuildPlan("actor-1", "projection-c", typeof(TestLease)));
        var hook = CreateHook(
            [firstFailingProvider, planProvider, secondFailingProvider, remainingPlanProvider],
            services => services.AddSingleton<IProjectionScopeActivationService<TestLease>>(activation));

        var act = () => hook.BeforePublishAsync(BuildContext("actor-failures"), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AggregateException>();
        exception.Which.InnerExceptions.Select(inner => inner.Message).Should().BeEquivalentTo(
            "provider one failed",
            "activation failed for projection-a",
            "provider two failed",
            "activation failed for projection-c");
        firstFailingProvider.Calls.Should().Be(1);
        planProvider.Calls.Should().Be(1);
        secondFailingProvider.Calls.Should().Be(1);
        remainingPlanProvider.Calls.Should().Be(1);
        activation.Requests.Select(request => request.ProjectionKind).Should().Equal(
            "projection-a",
            "projection-b",
            "projection-c");
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldImmediatelyPropagateProviderCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var activation = new RecordingActivationService<TestLease>();
        var remainingProvider = new StaticPlanProvider(
            BuildPlan("actor-1", "projection-a", typeof(TestLease)));
        var hook = CreateHook(
            [new CancelingPlanProvider(cancellation.Token), remainingProvider],
            services => services.AddSingleton<IProjectionScopeActivationService<TestLease>>(activation));

        var act = () => hook.BeforePublishAsync(BuildContext("actor-canceled"), cancellation.Token);

        await act.Should().ThrowExactlyAsync<OperationCanceledException>();
        remainingProvider.Calls.Should().Be(0);
        activation.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldImmediatelyPropagateDispatcherCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var cancelingActivation = new CancelingActivationService<TestLease>(cancellation);
        var skippedActivation = new RecordingActivationService<OtherTestLease>();
        var remainingProvider = new StaticPlanProvider(
            BuildPlan("actor-1", "projection-c", typeof(OtherTestLease)));
        var hook = CreateHook(
            [
                new StaticPlanProvider(
                    BuildPlan("actor-1", "projection-a", typeof(TestLease)),
                    BuildPlan("actor-1", "projection-b", typeof(OtherTestLease))),
                remainingProvider,
            ],
            services =>
            {
                services.AddSingleton<IProjectionScopeActivationService<TestLease>>(cancelingActivation);
                services.AddSingleton<IProjectionScopeActivationService<OtherTestLease>>(skippedActivation);
            });

        var act = () => hook.BeforePublishAsync(BuildContext("actor-canceled"), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        cancelingActivation.Requests.Should().ContainSingle();
        skippedActivation.Requests.Should().BeEmpty();
        remainingProvider.Calls.Should().Be(0);
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
        public int Calls { get; private set; }

        public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
        {
            _ = context;
            Calls++;
            return plans;
        }
    }

    private sealed class ThrowingPlanProvider(string message = "provider failed") : IProjectionActivationPlanProvider
    {
        public int Calls { get; private set; }

        public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
        {
            _ = context;
            Calls++;
            throw new InvalidOperationException(message);
        }
    }

    private sealed class CancelingPlanProvider(CancellationToken cancellationToken) : IProjectionActivationPlanProvider
    {
        public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
        {
            _ = context;
            throw new OperationCanceledException(cancellationToken);
        }
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

    private sealed class RecordingFailingActivationService<TLease>(params string[] failingProjectionKinds)
        : IProjectionScopeActivationService<TLease>
        where TLease : class, IProjectionRuntimeLease
    {
        private readonly HashSet<string> _failingProjectionKinds = failingProjectionKinds.ToHashSet(StringComparer.Ordinal);

        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<TLease> EnsureAsync(ProjectionScopeStartRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (_failingProjectionKinds.Contains(request.ProjectionKind))
            {
                return Task.FromException<TLease>(
                    new InvalidOperationException($"activation failed for {request.ProjectionKind}"));
            }

            return Task.FromResult<TLease>((Activator.CreateInstance(typeof(TLease), request.RootActorId) as TLease)!);
        }
    }

    private sealed class CancelingActivationService<TLease>(CancellationTokenSource cancellation)
        : IProjectionScopeActivationService<TLease>
        where TLease : class, IProjectionRuntimeLease
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<TLease> EnsureAsync(ProjectionScopeStartRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            cancellation.Cancel();
            return Task.FromCanceled<TLease>(ct);
        }
    }

    private sealed class TestLease(string rootEntityId) : IProjectionRuntimeLease
    {
        public string RootEntityId { get; } = rootEntityId;
    }

    private sealed class OtherTestLease(string rootEntityId) : IProjectionRuntimeLease
    {
        public string RootEntityId { get; } = rootEntityId;
    }
}
