using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionRuntimeRegistrationTests
{
    [Fact]
    public async Task AddProjectionMaterializationRuntimeCore_ShouldRegisterLifecycleAndAdministrationServices()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);

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
        var contextFactory = provider.GetRequiredService<IProjectionScopeContextFactory<TestMaterializationContext>>();
        var activation = provider.GetRequiredService<IProjectionMaterializationActivationService<TestMaterializationLease>>();
        var release = provider.GetRequiredService<IProjectionMaterializationReleaseService<TestMaterializationLease>>();
        provider.GetRequiredService<IProjectionFailureReplayService>().Should().NotBeNull();
        provider.GetRequiredService<IProjectionFailureAlertSink>().Should().NotBeNull();

        var scopeKey = new ProjectionRuntimeScopeKey("actor-1", "projection-a", ProjectionRuntimeMode.DurableMaterialization);
        var context = contextFactory.Create(scopeKey);
        context.RootActorId.Should().Be("actor-1");
        context.ProjectionKind.Should().Be("projection-a");

        var lease = await activation.EnsureAsync(new ProjectionMaterializationStartRequest
        {
            RootActorId = "actor-1",
            ProjectionKind = "projection-a",
        });
        await release.ReleaseIfIdleAsync(lease);

        runtime.CreatedActorIds.Should().ContainSingle()
            .Which.Should().Be(ProjectionScopeActorId.Build(scopeKey));
        dispatchPort.Dispatched.Should().HaveCount(2);
        dispatchPort.Dispatched[0].actorId.Should().Be(ProjectionScopeActorId.Build(scopeKey));
        dispatchPort.Dispatched[0].command.Payload!.Unpack<EnsureProjectionScopeCommand>().ProjectionKind.Should().Be("projection-a");
        dispatchPort.Dispatched[1].command.Payload!.Unpack<ReleaseProjectionScopeCommand>().ProjectionKind.Should().Be("projection-a");
    }

    [Fact]
    public async Task AddEventSinkProjectionRuntimeCore_ShouldRegisterSessionLifecycleAndSessionScopeContext()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatchPort);

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
        var contextFactory = provider.GetRequiredService<IProjectionScopeContextFactory<TestSessionContext>>();
        var activation = provider.GetRequiredService<IProjectionSessionActivationService<TestSessionLease>>();
        var release = provider.GetRequiredService<IProjectionSessionReleaseService<TestSessionLease>>();

        var scopeKey = new ProjectionRuntimeScopeKey("actor-2", "projection-b", ProjectionRuntimeMode.SessionObservation, "session-9");
        var context = contextFactory.Create(scopeKey);
        context.RootActorId.Should().Be("actor-2");
        context.ProjectionKind.Should().Be("projection-b");
        context.SessionId.Should().Be("session-9");

        var lease = await activation.EnsureAsync(new ProjectionSessionStartRequest
        {
            RootActorId = "actor-2",
            ProjectionKind = "projection-b",
            SessionId = "session-9",
        });
        await release.ReleaseIfIdleAsync(lease);

        runtime.CreatedActorIds.Should().ContainSingle()
            .Which.Should().Be(ProjectionScopeActorId.Build(scopeKey));
        dispatchPort.Dispatched.Should().HaveCount(2);
        dispatchPort.Dispatched[0].command.Payload!.Unpack<EnsureProjectionScopeCommand>().SessionId.Should().Be("session-9");
        dispatchPort.Dispatched[1].command.Payload!.Unpack<ReleaseProjectionScopeCommand>().SessionId.Should().Be("session-9");
    }

    [Fact]
    public async Task ProjectionFailureReplayService_ShouldOnlyDispatchForExistingScope()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = new ProjectionFailureReplayService(runtime, dispatchPort);
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
    }

    [Fact]
    public void ProjectionFailureRetentionPolicy_ShouldTrimOldestFailures()
    {
        var failures = new Google.Protobuf.Collections.RepeatedField<ProjectionScopeFailure>();
        failures.Add(new ProjectionScopeFailure { FailureId = "f1" });
        failures.Add(new ProjectionScopeFailure { FailureId = "f2" });
        failures.Add(new ProjectionScopeFailure { FailureId = "f3" });

        ProjectionFailureRetentionPolicy.Trim(failures, 2);

        failures.Select(x => x.FailureId).Should().Equal("f2", "f3");
    }

    [Fact]
    public async Task LoggingProjectionFailureAlertSink_ShouldValidateInputs_AndComplete()
    {
        var sink = new LoggingProjectionFailureAlertSink();
        var alert = new ProjectionFailureAlert(
            new ProjectionRuntimeScopeKey("actor-4", "projection-d", ProjectionRuntimeMode.DurableMaterialization),
            "failure-1",
            "projection-execution",
            "event-1",
            "type://event",
            9,
            "boom",
            1,
            DateTimeOffset.UtcNow);

        Func<Task> nullAct = () => sink.PublishAsync(null!);
        await nullAct.Should().ThrowAsync<ArgumentNullException>();

        await sink.PublishAsync(alert);
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public HashSet<string> ExistingActorIds { get; } = [];
        public List<string> CreatedActorIds { get; } = [];

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

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(ExistingActorIds.Contains(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope command)> Dispatched { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatched.Add((actorId, envelope));
            return Task.CompletedTask;
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

    private sealed class TestSessionContext : IProjectionSessionContext
    {
        public string RootActorId { get; init; } = string.Empty;

        public string ProjectionKind { get; init; } = string.Empty;

        public string SessionId { get; init; } = string.Empty;
    }

    private sealed class TestSessionLease
        : EventSinkProjectionRuntimeLeaseBase<StringValue>,
          IProjectionPortSessionLease,
          IProjectionContextRuntimeLease<TestSessionContext>
    {
        public TestSessionLease(TestSessionContext context)
            : base(context.RootActorId)
        {
            Context = context;
        }

        public TestSessionContext Context { get; }

        public string ScopeId => Context.RootActorId;

        public string SessionId => Context.SessionId;
    }
}
