using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Compilation;

public class RoslynScriptExecutionEngineTests
{
    [Fact]
    public async Task HandleRequestedEventAsync_ShouldReturnEmptyResult_WhenSourceIsBlank()
    {
        var engine = new RoslynScriptExecutionEngine();

        var result = await engine.HandleRequestedEventAsync(
            source: "   ",
            BuildRequestedEnvelope("claim.requested"),
            BuildContext(),
            CancellationToken.None);

        result.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyDomainEventAsync_ShouldReturnCurrentState_WhenSourceIsBlank()
    {
        var engine = new RoslynScriptExecutionEngine();
        var currentState = new Dictionary<string, Any>(StringComparer.Ordinal)
        {
            ["seed"] = Any.Pack(new StringValue { Value = "seed-state" }),
        };

        var next = await engine.ApplyDomainEventAsync(
            source: string.Empty,
            currentState,
            BuildDomainEventEnvelope("claim.approved"),
            CancellationToken.None);

        next.Should().BeSameAs(currentState);
    }

    [Fact]
    public async Task ReduceReadModelAsync_ShouldReturnCurrentReadModel_WhenSourceIsBlank()
    {
        var engine = new RoslynScriptExecutionEngine();
        var currentReadModel = new Dictionary<string, Any>(StringComparer.Ordinal)
        {
            ["seed"] = Any.Pack(new StringValue { Value = "seed-view" }),
        };

        var next = await engine.ReduceReadModelAsync(
            source: string.Empty,
            currentReadModel,
            BuildDomainEventEnvelope("claim.approved"),
            CancellationToken.None);

        next.Should().BeSameAs(currentReadModel);
    }

    [Fact]
    public async Task HandleRequestedEventAsync_ShouldThrow_WhenCompilationFails()
    {
        var engine = new RoslynScriptExecutionEngine();

        var act = () => engine.HandleRequestedEventAsync(
            "if (true {",
            BuildRequestedEnvelope("claim.requested"),
            BuildContext(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Script execution compilation failed*");
    }

    [Fact]
    public async Task HandleRequestedEventAsync_ShouldThrow_WhenRuntimeTypeIsMissing()
    {
        var engine = new RoslynScriptExecutionEngine();
        var source = """
using System.Threading;
using System.Threading.Tasks;

public sealed class NotARuntimeScript
{
    public Task<string> ExecuteAsync(CancellationToken ct) => Task.FromResult("noop");
}
""";

        var act = () => engine.HandleRequestedEventAsync(
            source,
            BuildRequestedEnvelope("claim.requested"),
            BuildContext(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*implementing IScriptPackageRuntime*");
    }

    [Fact]
    public async Task HandleRequestedEventAsync_ShouldThrow_WhenRuntimeTypeCannotBeInstantiated()
    {
        var engine = new RoslynScriptExecutionEngine();
        var source = """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf.WellKnownTypes;

public sealed class PrivateCtorRuntimeScript : IScriptPackageRuntime
{
    private PrivateCtorRuntimeScript() { }

    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct) =>
        Task.FromResult(new ScriptHandlerResult(System.Array.Empty<Google.Protobuf.IMessage>()));

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentState);

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentReadModel);
}
""";

        var act = () => engine.HandleRequestedEventAsync(
            source,
            BuildRequestedEnvelope("claim.requested"),
            BuildContext(),
            CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task HandleRequestedEventAsync_ShouldDisposeRuntime_WhenRuntimeImplementsIDisposable()
    {
        ScriptRuntimeLoaderTestHooks.Reset();
        var engine = new RoslynScriptExecutionEngine();
        var source = """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class DisposableRuntimeScript : IScriptPackageRuntime, IDisposable
{
    public void Dispose()
    {
        Aevatar.Scripting.Core.Tests.Compilation.ScriptRuntimeLoaderTestHooks.IncrementDispose();
    }

    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ScriptHandlerResult(
            new IMessage[] { new StringValue { Value = requestedEvent.EventType } }));
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentState);

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentReadModel);
}
""";

        var result = await engine.HandleRequestedEventAsync(
            source,
            BuildRequestedEnvelope("claim.requested"),
            BuildContext(),
            CancellationToken.None);

        result.DomainEvents.Should().ContainSingle();
        ScriptRuntimeLoaderTestHooks.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task ApplyAndReduce_ShouldExecuteRuntimeLogic_AndDisposeAsyncRuntime()
    {
        ScriptRuntimeLoaderTestHooks.Reset();
        var engine = new RoslynScriptExecutionEngine();
        var source = """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class AsyncDisposableRuntimeScript : IScriptPackageRuntime, IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        Aevatar.Scripting.Core.Tests.Compilation.ScriptRuntimeLoaderTestHooks.IncrementAsyncDispose();
        return ValueTask.CompletedTask;
    }

    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct) =>
        Task.FromResult(new ScriptHandlerResult(System.Array.Empty<IMessage>()));

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["state"] = Any.Pack(new StringValue { Value = "state:" + domainEvent.EventType }),
            });
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["view"] = Any.Pack(new StringValue { Value = "view:" + domainEvent.EventType }),
            });
    }
}
""";

        var nextState = await engine.ApplyDomainEventAsync(
            source,
            new Dictionary<string, Any>(StringComparer.Ordinal),
            BuildDomainEventEnvelope("claim.approved"),
            CancellationToken.None);

        nextState.Should().NotBeNull();
        nextState!["state"].Unpack<StringValue>().Value.Should().Be("state:claim.approved");

        var nextReadModel = await engine.ReduceReadModelAsync(
            source,
            new Dictionary<string, Any>(StringComparer.Ordinal),
            BuildDomainEventEnvelope("claim.approved"),
            CancellationToken.None);

        nextReadModel.Should().NotBeNull();
        nextReadModel!["view"].Unpack<StringValue>().Value.Should().Be("view:claim.approved");
        ScriptRuntimeLoaderTestHooks.AsyncDisposeCount.Should().Be(2);
    }

    private static ScriptRequestedEventEnvelope BuildRequestedEnvelope(string eventType) =>
        new(
            EventType: eventType,
            Payload: Any.Pack(new Struct()),
            EventId: "evt-1",
            CorrelationId: "corr-1",
            CausationId: "cause-1");

    private static ScriptDomainEventEnvelope BuildDomainEventEnvelope(string eventType) =>
        new(
            EventType: eventType,
            Payload: Any.Pack(new Struct()),
            EventId: "evt-2",
            CorrelationId: "corr-1",
            CausationId: "cause-1");

    private static ScriptExecutionContext BuildContext() =>
        new(
            ActorId: "runtime-1",
            ScriptId: "script-1",
            Revision: "rev-1",
            RunId: "run-1",
            CorrelationId: "corr-1");
}

public static class ScriptRuntimeLoaderTestHooks
{
    private static int _disposeCount;
    private static int _asyncDisposeCount;

    public static int DisposeCount => Volatile.Read(ref _disposeCount);
    public static int AsyncDisposeCount => Volatile.Read(ref _asyncDisposeCount);

    public static void IncrementDispose() => Interlocked.Increment(ref _disposeCount);

    public static void IncrementAsyncDispose() => Interlocked.Increment(ref _asyncDisposeCount);

    public static void Reset()
    {
        Interlocked.Exchange(ref _disposeCount, 0);
        Interlocked.Exchange(ref _asyncDisposeCount, 0);
    }
}
