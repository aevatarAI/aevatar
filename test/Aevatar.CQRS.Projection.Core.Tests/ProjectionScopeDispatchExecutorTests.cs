using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionScopeDispatchExecutorTests
{
    [Fact]
    public async Task ExecuteMaterializersAsync_ShouldAggregateFailures_AndContinueSiblingMaterializers()
    {
        var executeMethod = GetExecuteMaterializersMethod();
        var context = new TestMaterializationContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "projection-a",
        };
        var envelope = new EventEnvelope { Id = "evt-1" };
        var thirdMaterializerCalls = 0;

        var aggregateTask = (Task)executeMethod
            .MakeGenericMethod(typeof(TestMaterializationContext))
            .Invoke(
                null,
                [
                    new IProjectionMaterializer<TestMaterializationContext>[]
                    {
                        new TestMaterializer((_, _, _) => ValueTask.CompletedTask),
                        new TestMaterializer((_, _, _) => ValueTask.FromException(new InvalidOperationException("boom"))),
                        new TestMaterializer((_, _, _) =>
                        {
                            thirdMaterializerCalls++;
                            return ValueTask.CompletedTask;
                        }),
                    },
                    context,
                    envelope,
                    CancellationToken.None,
                ])!;

        var aggregate = await Assert.ThrowsAsync<ProjectionDispatchAggregateException>(() => aggregateTask);
        aggregate.Failures.Should().ContainSingle();
        aggregate.Failures[0].ProjectorOrder.Should().Be(2);
        aggregate.Failures[0].ProjectorName.Should().Be(nameof(TestMaterializer));
        aggregate.InnerException.Should().BeOfType<InvalidOperationException>();
        aggregate.Message.Should().Contain("TestMaterializer#2");
        thirdMaterializerCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteMaterializersAsync_WhenCancellationIsRequested_ShouldThrowOperationCanceledException()
    {
        var executeMethod = GetExecuteMaterializersMethod();
        var context = new TestMaterializationContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "projection-a",
        };
        var envelope = new EventEnvelope { Id = "evt-1" };
        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var cancelledTask = (Task)executeMethod
            .MakeGenericMethod(typeof(TestMaterializationContext))
            .Invoke(
                null,
                [
                    new IProjectionMaterializer<TestMaterializationContext>[]
                    {
                        new TestMaterializer((_, _, ct) => ValueTask.FromException(new OperationCanceledException(ct))),
                    },
                    context,
                    envelope,
                    cancelled.Token,
                ])!;

        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelledTask);
    }

    private static MethodInfo GetExecuteMaterializersMethod()
    {
        var executorType = typeof(ProjectionFailureReplayService).Assembly
            .GetType("Aevatar.CQRS.Projection.Core.Orchestration.ProjectionScopeDispatchExecutor");
        executorType.Should().NotBeNull();

        var executeMethod = executorType!.GetMethod(
            "ExecuteMaterializersAsync",
            BindingFlags.Public | BindingFlags.Static);
        executeMethod.Should().NotBeNull();
        return executeMethod!;
    }

    private sealed class TestMaterializationContext : IProjectionMaterializationContext
    {
        public string RootActorId { get; init; } = string.Empty;

        public string ProjectionKind { get; init; } = string.Empty;
    }

    private sealed class TestMaterializer : IProjectionMaterializer<TestMaterializationContext>
    {
        private readonly Func<TestMaterializationContext, EventEnvelope, CancellationToken, ValueTask> _projectAsync;

        public TestMaterializer(Func<TestMaterializationContext, EventEnvelope, CancellationToken, ValueTask> projectAsync)
        {
            _projectAsync = projectAsync;
        }

        public ValueTask ProjectAsync(TestMaterializationContext context, EventEnvelope envelope, CancellationToken ct = default) =>
            _projectAsync(context, envelope, ct);
    }
}
