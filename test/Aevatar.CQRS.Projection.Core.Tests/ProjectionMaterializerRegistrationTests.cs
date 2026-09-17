using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Observability;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Runtime.Observability;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionMaterializerRegistrationTests
{
    [Fact]
    public void AddCurrentStateProjectionMaterializer_ShouldRegisterBaseAndTypedContracts()
    {
        var services = new ServiceCollection();

        services.AddCurrentStateProjectionMaterializer<TestContext, TestCurrentStateMaterializer>();

        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionMaterializer<TestContext>) &&
            x.ImplementationType == typeof(ObservedProjectionMaterializer<TestContext, TestCurrentStateMaterializer>));
        services.Should().Contain(x => x.ServiceType == typeof(TestCurrentStateMaterializer));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICurrentStateProjectionMaterializer<TestContext>>()
            .Should().BeOfType<ObservedCurrentStateProjectionMaterializer<TestContext, TestCurrentStateMaterializer>>();
        provider.GetRequiredService<TestCurrentStateMaterializer>().Should().NotBeNull();
    }

    [Fact]
    public void AddProjectionArtifactMaterializer_ShouldRegisterBaseAndTypedContracts()
    {
        var services = new ServiceCollection();

        services.AddProjectionArtifactMaterializer<TestContext, TestArtifactMaterializer>();

        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionMaterializer<TestContext>) &&
            x.ImplementationType == typeof(ObservedProjectionMaterializer<TestContext, TestArtifactMaterializer>));
        services.Should().Contain(x => x.ServiceType == typeof(TestArtifactMaterializer));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionArtifactMaterializer<TestContext>>()
            .Should().BeOfType<ObservedProjectionArtifactMaterializer<TestContext, TestArtifactMaterializer>>();
        provider.GetRequiredService<TestArtifactMaterializer>().Should().NotBeNull();
    }

    [Fact]
    public async Task ObservedProjectionMaterializer_ShouldDelegateAndEmitMaterializeActivity()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var services = new ServiceCollection();
        services.AddCurrentStateProjectionMaterializer<TestContext, TestCurrentStateMaterializer>();
        using var provider = services.BuildServiceProvider();
        var materializer = provider.GetRequiredService<IProjectionMaterializer<TestContext>>();
        var inner = provider.GetRequiredService<TestCurrentStateMaterializer>();

        await materializer.ProjectAsync(
            new TestContext
            {
                RootActorId = "actor-1",
                ProjectionKind = "test",
            },
            CreateCommittedEnvelope("outer-1", "state-event-1", 42));

        inner.ProjectCount.Should().Be(1);
        var activity = stopped
            .Where(x => x.DisplayName == AevatarActivitySource.ProjectionMaterializeActivityName)
            .Should()
            .ContainSingle()
            .Which;
        activity.GetTagItem(AevatarActivitySource.ProjectionNameTag).Should().Be(nameof(TestContext));
        activity.GetTagItem(AevatarActivitySource.ProjectionLastEventIdTag).Should().Be("state-event-1");
        activity.GetTagItem(AevatarActivitySource.ProjectionStateVersionTag).Should().Be(42L);
    }

    [Fact]
    public async Task ObservedProjectionMaterializer_ShouldLogCompleted_WithAuthoritativeStateVersion()
    {
        var inner = new TestCurrentStateMaterializer();
        var logger = new RecordingLogger<ObservedProjectionMaterializer<TestContext, TestCurrentStateMaterializer>>();
        var materializer = new ObservedProjectionMaterializer<TestContext, TestCurrentStateMaterializer>(inner, logger);

        await materializer.ProjectAsync(
            new TestContext { ProjectionKind = "projection-alpha" },
            CreateCommittedEnvelopeWithoutEventData("outer-1", "state-event-1", 42));

        inner.ProjectCount.Should().Be(1);
        var entry = logger.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Properties["ProjectionKind"].Should().Be("projection-alpha");
        entry.Properties["MaterializerKind"].Should().Be(nameof(TestCurrentStateMaterializer));
        entry.Properties["StateVersion"].Should().Be(42L);
        entry.Properties["Result"].Should().Be(ProjectionProcessingMetrics.ResultCompleted);
        entry.Properties["ElapsedMs"].Should().BeOfType<double>().Which.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ObservedProjectionMaterializer_ShouldLogNullStateVersion_ForNonCommittedEnvelope()
    {
        var inner = new TestCurrentStateMaterializer();
        var logger = new RecordingLogger<ObservedProjectionMaterializer<TestContext, TestCurrentStateMaterializer>>();
        var materializer = new ObservedProjectionMaterializer<TestContext, TestCurrentStateMaterializer>(inner, logger);

        await materializer.ProjectAsync(
            new TestContext { ProjectionKind = "projection-alpha" },
            new EventEnvelope { Id = "raw-event-1" });

        logger.Entries.Should().ContainSingle().Which.Properties["StateVersion"].Should().BeNull();
    }

    [Fact]
    public async Task ObservedProjectionMaterializer_ShouldMarkActivityError_WhenInnerThrows()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var logger = new RecordingLogger<ObservedProjectionMaterializer<TestContext, ThrowingMaterializer>>();
        var materializer = new ObservedProjectionMaterializer<TestContext, ThrowingMaterializer>(
            new ThrowingMaterializer(),
            logger);

        Func<Task> act = async () => await materializer.ProjectAsync(
            new TestContext(),
            new EventEnvelope { Id = "outer-error" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("projection boom");
        stopped
            .Where(x => x.DisplayName == AevatarActivitySource.ProjectionMaterializeActivityName)
            .Should()
            .ContainSingle()
            .Which
            .Status
            .Should()
            .Be(ActivityStatusCode.Error);
        var entry = logger.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Properties["Result"].Should().Be(ProjectionProcessingMetrics.ResultFailed);
        entry.Properties["ErrorType"].Should().Be(nameof(InvalidOperationException));
        entry.Exception.Should().BeNull("materializer exceptions can contain provider or payload details");
    }

    [Fact]
    public async Task ObservedProjectionMaterializer_ShouldLogCancelled_WhenRequestedTokenIsCancelled()
    {
        var inner = new TestCurrentStateMaterializer();
        var logger = new RecordingLogger<ObservedProjectionMaterializer<TestContext, TestCurrentStateMaterializer>>();
        var materializer = new ObservedProjectionMaterializer<TestContext, TestCurrentStateMaterializer>(inner, logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> act = async () => await materializer.ProjectAsync(
            new TestContext { ProjectionKind = "projection-cancelled" },
            new EventEnvelope { Id = "cancelled-1" },
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.ProjectCount.Should().Be(0);
        var entry = logger.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Properties["Result"].Should().Be(ProjectionProcessingMetrics.ResultCancelled);
        entry.Properties.Should().NotContainKey("ErrorType");
    }

    [Fact]
    public async Task ObservedProjectionMaterializer_ShouldLogFailed_WhenCancellationWasNotRequested()
    {
        var logger = new RecordingLogger<ObservedProjectionMaterializer<TestContext, UnrelatedCancellationMaterializer>>();
        var materializer = new ObservedProjectionMaterializer<TestContext, UnrelatedCancellationMaterializer>(
            new UnrelatedCancellationMaterializer(),
            logger);

        Func<Task> act = async () => await materializer.ProjectAsync(
            new TestContext { ProjectionKind = "projection-failed" },
            new EventEnvelope { Id = "foreign-cancellation-1" });

        await act.Should().ThrowAsync<OperationCanceledException>()
            .WithMessage("foreign cancellation");
        var entry = logger.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Properties["Result"].Should().Be(ProjectionProcessingMetrics.ResultFailed);
        entry.Properties["ErrorType"].Should().Be(nameof(OperationCanceledException));
    }

    [Fact]
    public async Task ObservedProjectionMaterializer_ShouldIgnoreLoggerFailure_AfterSuccessfulMaterialization()
    {
        var inner = new TestCurrentStateMaterializer();
        var materializer = new ObservedProjectionMaterializer<TestContext, TestCurrentStateMaterializer>(
            inner,
            new ThrowingLogger<ObservedProjectionMaterializer<TestContext, TestCurrentStateMaterializer>>());

        Func<Task> act = async () => await materializer.ProjectAsync(
            new TestContext(),
            new EventEnvelope { Id = "success-with-logger-failure" });

        await act.Should().NotThrowAsync();
        inner.ProjectCount.Should().Be(1);
    }

    [Fact]
    public async Task ObservedProjectionMaterializer_ShouldPreserveInnerFailure_WhenLoggerFails()
    {
        var materializer = new ObservedProjectionMaterializer<TestContext, ThrowingMaterializer>(
            new ThrowingMaterializer(),
            new ThrowingLogger<ObservedProjectionMaterializer<TestContext, ThrowingMaterializer>>());

        Func<Task> act = async () => await materializer.ProjectAsync(
            new TestContext(),
            new EventEnvelope { Id = "failure-with-logger-failure" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("projection boom");
    }

    [Fact]
    public async Task ObservedProjectionMaterializer_ShouldAddWorkflowRunTag_ForWorkflowExecutionContext()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var inner = new WorkflowMaterializer();
        var materializer = new ObservedProjectionMaterializer<WorkflowExecutionProjectionContext, WorkflowMaterializer>(inner);

        await materializer.ProjectAsync(
            new WorkflowExecutionProjectionContext
            {
                RootActorId = "workflow-run-1",
                ProjectionKind = "workflow",
            },
            new EventEnvelope { Id = "workflow-event-1" });

        inner.ProjectCount.Should().Be(1);
        stopped
            .Where(x => x.DisplayName == AevatarActivitySource.ProjectionMaterializeActivityName)
            .Should()
            .ContainSingle()
            .Which
            .GetTagItem(AevatarActivitySource.WorkflowRunIdTag)
            .Should()
            .Be("workflow-run-1");
    }

    [Fact]
    public async Task ObservedProjectionMaterializerWrappers_ShouldDelegateToInnerMaterializers()
    {
        var currentStateInner = new TestCurrentStateMaterializer();
        var currentState = new ObservedCurrentStateProjectionMaterializer<TestContext, TestCurrentStateMaterializer>(
            currentStateInner);
        var artifactInner = new TestArtifactMaterializer();
        var artifact = new ObservedProjectionArtifactMaterializer<TestContext, TestArtifactMaterializer>(
            artifactInner);

        await currentState.ProjectAsync(new TestContext(), new EventEnvelope { Id = "current-state-1" });
        await artifact.ProjectAsync(new TestContext(), new EventEnvelope { Id = "artifact-1" });

        currentStateInner.ProjectCount.Should().Be(1);
        artifactInner.ProjectCount.Should().Be(1);
    }

    private sealed class TestContext : IProjectionMaterializationContext
    {
        public string RootActorId { get; init; } = "actor-1";

        public string ProjectionKind { get; init; } = "projection";
    }

    private sealed class WorkflowExecutionProjectionContext : IProjectionMaterializationContext
    {
        public string RootActorId { get; init; } = "workflow-run";

        public string ProjectionKind { get; init; } = "workflow";
    }

    private sealed class TestCurrentStateMaterializer : ICurrentStateProjectionMaterializer<TestContext>
    {
        public int ProjectCount { get; private set; }

        public ValueTask ProjectAsync(TestContext context, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            ProjectCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestArtifactMaterializer : IProjectionArtifactMaterializer<TestContext>
    {
        public int ProjectCount { get; private set; }

        public ValueTask ProjectAsync(TestContext context, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            ProjectCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WorkflowMaterializer : IProjectionMaterializer<WorkflowExecutionProjectionContext>
    {
        public int ProjectCount { get; private set; }

        public ValueTask ProjectAsync(
            WorkflowExecutionProjectionContext context,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            ProjectCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingMaterializer : IProjectionMaterializer<TestContext>
    {
        public ValueTask ProjectAsync(TestContext context, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("projection boom");
        }
    }

    private sealed class UnrelatedCancellationMaterializer : IProjectionMaterializer<TestContext>
    {
        public ValueTask ProjectAsync(TestContext context, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            _ = ct;
            throw new OperationCanceledException("foreign cancellation");
        }
    }

    private sealed record RecordedLogEntry(
        LogLevel Level,
        IReadOnlyDictionary<string, object?> Properties,
        Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<RecordedLogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = eventId;
            _ = formatter;
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            Entries.Add(new RecordedLogEntry(logLevel, properties, exception));
        }
    }

    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger boom");
    }

    private static EventEnvelope CreateCommittedEnvelope(string envelopeId, string eventId, long version)
    {
        return new EventEnvelope
        {
            Id = envelopeId,
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    EventData = Google.Protobuf.WellKnownTypes.Any.Pack(new StringValue { Value = "payload" }),
                },
            }),
        };
    }

    private static EventEnvelope CreateCommittedEnvelopeWithoutEventData(
        string envelopeId,
        string eventId,
        long version) =>
        new()
        {
            Id = envelopeId,
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                },
            }),
        };
}
