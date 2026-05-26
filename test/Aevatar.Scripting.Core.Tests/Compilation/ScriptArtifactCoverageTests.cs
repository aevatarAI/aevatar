using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Compilation;

public class ScriptArtifactCoverageTests
{
    [Fact]
    public async Task ScriptBehaviorArtifact_ShouldDisposeOnlyOnce_AndRejectBehaviorCreationAfterDispose()
    {
        var disposeCount = 0;
        var behavior = new NoopBehavior();
        var artifact = new ScriptBehaviorArtifact(
            "script-1",
            "rev-1",
            "hash-1",
            behavior.Descriptor,
            behavior.Descriptor.ToContract(),
            static () => new NoopBehavior(),
            () =>
            {
                disposeCount += 1;
                return ValueTask.CompletedTask;
            });

        artifact.CreateBehavior().Should().NotBeNull();

        await artifact.DisposeAsync();
        await artifact.DisposeAsync();

        disposeCount.Should().Be(1);
        Action act = () => artifact.CreateBehavior();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void CachedResolver_ShouldReturnCachedArtifactWithoutRecompiling()
    {
        var compiler = new CountingCompiler(() => CreateArtifact("script-1", "rev-1"));
        var resolver = new CachedScriptBehaviorArtifactResolver(compiler);
        var request = CreateRequest();

        var first = resolver.Resolve(request);
        var second = resolver.Resolve(request);

        second.ScriptId.Should().Be(first.ScriptId);
        second.Revision.Should().Be(first.Revision);
        first.CreateBehavior().Should().BeAssignableTo<IDisposable>().Subject.Dispose();
        second.CreateBehavior().Should().BeAssignableTo<IDisposable>().Subject.Dispose();
        compiler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task CachedResolver_ShouldShareSingleCompilation_WhenConcurrentRequestsTargetSameArtifact()
    {
        var compileEntered = new ManualResetEventSlim(false);
        var allowCompileToReturn = new ManualResetEventSlim(false);
        var compiler = new CountingCompiler(
            artifactFactory: () =>
            {
                compileEntered.Set();
                allowCompileToReturn.Wait();
                return CreateArtifact("script-1", "rev-1");
            });
        var resolver = new CachedScriptBehaviorArtifactResolver(compiler);
        var request = CreateRequest();

        var firstTask = Task.Run(() => resolver.Resolve(request));
        compileEntered.Wait();

        var secondTask = Task.Run(() => resolver.Resolve(request));
        allowCompileToReturn.Set();

        var resolved = await Task.WhenAll(firstTask, secondTask);

        resolved[0].ScriptId.Should().Be(resolved[1].ScriptId);
        resolved[0].Revision.Should().Be(resolved[1].Revision);
        compiler.CallCount.Should().Be(1);
    }

    [Fact]
    public void CachedResolver_ShouldThrow_WhenCompilationFails()
    {
        var compiler = new FailingCompiler();
        var resolver = new CachedScriptBehaviorArtifactResolver(compiler);

        Action act = () => resolver.Resolve(CreateRequest());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Script artifact resolution failed: compile-failed");
    }

    [Fact]
    public void CachedResolver_ShouldRetryAfterFailedCompilation()
    {
        var compiler = new FailOnceCompiler();
        var resolver = new CachedScriptBehaviorArtifactResolver(compiler);
        var request = CreateRequest();

        Action first = () => resolver.Resolve(request);
        first.Should().Throw<InvalidOperationException>();

        var resolved = resolver.Resolve(request);

        resolved.ScriptId.Should().Be("script-1");
        compiler.CallCount.Should().Be(2);
    }

    [Fact]
    public void CachedResolver_ShouldUseTypedCompositeKey_WithoutDelimiterCollision()
    {
        var compiler = new RequestEchoCompiler();
        var resolver = new CachedScriptBehaviorArtifactResolver(compiler);

        var first = resolver.Resolve(CreateRequest(
            scriptId: "script",
            revision: "rev|hash",
            sourceHash: "entry",
            entryBehaviorTypeName: "type"));
        var second = resolver.Resolve(CreateRequest(
            scriptId: "script|rev",
            revision: "hash",
            sourceHash: "entry",
            entryBehaviorTypeName: "type"));

        second.Should().NotBeSameAs(first);
        first.ScriptId.Should().Be("script");
        first.Revision.Should().Be("rev|hash");
        second.ScriptId.Should().Be("script|rev");
        second.Revision.Should().Be("hash");
        compiler.CallCount.Should().Be(2);
    }

    [Fact]
    public void CachedResolver_ShouldEvictByConfiguredSizeLimit_AndDisposeAfterReturnedBehaviorDisposes()
    {
        var disposed = new List<string>();
        var disposeObserved = new ManualResetEventSlim(false);
        var compiler = new RequestEchoCompiler(request =>
        {
            disposed.Add(request.ScriptId);
            disposeObserved.Set();
        });
        using var resolver = new CachedScriptBehaviorArtifactResolver(compiler, maxCachedArtifacts: 1);

        var first = resolver.Resolve(CreateRequest(scriptId: "script-1"));
        var firstBehavior = first.CreateBehavior();
        var firstBehaviorDisposable = firstBehavior.Should().BeAssignableTo<IDisposable>().Subject;
        var second = resolver.Resolve(CreateRequest(scriptId: "script-2"));

        second.Should().NotBeSameAs(first);
        disposeObserved.Wait(TimeSpan.FromMilliseconds(100)).Should().BeFalse();
        firstBehaviorDisposable.Dispose();
        disposeObserved.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        disposed.Should().Contain("script-1");
        compiler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task EvictedArtifact_CanBeCreatedThenAsyncDisposed_ReleasesLease()
    {
        var disposed = new List<string>();
        var disposeObserved = new ManualResetEventSlim(false);
        var compiler = new RequestEchoCompiler(
            onDispose: request =>
            {
                disposed.Add(request.ScriptId);
                disposeObserved.Set();
            },
            behaviorFactory: static () => new AsyncDisposableNoopBehavior());
        using var resolver = new CachedScriptBehaviorArtifactResolver(compiler, maxCachedArtifacts: 1);

        var first = resolver.Resolve(CreateRequest(scriptId: "script-1"));
        var firstBehavior = first.CreateBehavior();
        var firstBehaviorAsyncDisposable = firstBehavior.Should().BeAssignableTo<IAsyncDisposable>().Subject;
        var second = resolver.Resolve(CreateRequest(scriptId: "script-2"));

        second.Should().NotBeSameAs(first);
        disposeObserved.Wait(TimeSpan.FromMilliseconds(100)).Should().BeFalse();
        await firstBehaviorAsyncDisposable.DisposeAsync();
        disposeObserved.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        disposed.Should().Contain("script-1");
        compiler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task EvictedArtifact_DirectDisposeAsync_ReleasesLease_WithoutCreatingBehavior()
    {
        var disposed = new List<string>();
        var disposeObserved = new ManualResetEventSlim(false);
        var compiler = new RequestEchoCompiler(request =>
        {
            disposed.Add(request.ScriptId);
            disposeObserved.Set();
        });
        using var resolver = new CachedScriptBehaviorArtifactResolver(compiler, maxCachedArtifacts: 1);

        var first = resolver.Resolve(CreateRequest(scriptId: "script-1"));
        var second = resolver.Resolve(CreateRequest(scriptId: "script-2"));

        second.Should().NotBeSameAs(first);
        disposeObserved.Wait(TimeSpan.FromMilliseconds(100)).Should().BeFalse();
        await first.DisposeAsync();
        disposeObserved.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        disposed.Should().Contain("script-1");
        compiler.CallCount.Should().Be(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CachedResolver_ShouldRejectNonPositiveMaxCachedArtifacts(long maxCachedArtifacts)
    {
        var compiler = new CountingCompiler(() => CreateArtifact("script-1", "rev-1"));

        Action act = () => new CachedScriptBehaviorArtifactResolver(compiler, maxCachedArtifacts);

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("maxCachedArtifacts");
    }

    [Fact]
    public async Task CachedResolver_ShouldKeepReturnedArtifactUsable_WhenConcurrentCapacityCompactionEvictsInFlightLazy()
    {
        var firstCompileStarted = new ManualResetEventSlim(false);
        var secondCompileStarted = new ManualResetEventSlim(false);
        var compileCallCount = 0;
        var allowCompileToReturn = new ManualResetEventSlim(false);
        var disposed = new List<string>();
        var disposeObserved = new CountdownEvent(2);
        var disposeGate = new object();
        var compiler = new RequestEchoCompiler(
            onCompile: _ =>
            {
                var call = Interlocked.Increment(ref compileCallCount);
                if (call == 1)
                    firstCompileStarted.Set();
                else if (call == 2)
                    secondCompileStarted.Set();

                allowCompileToReturn.Wait();
            },
            onDispose: request =>
            {
                lock (disposeGate)
                {
                    disposed.Add(request.ScriptId);
                }

                disposeObserved.Signal();
            });
        using var resolver = new CachedScriptBehaviorArtifactResolver(compiler, maxCachedArtifacts: 1);

        var firstTask = ResolveOnDedicatedThread(resolver, CreateRequest(scriptId: "script-1"));
        firstCompileStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        var secondTask = ResolveOnDedicatedThread(resolver, CreateRequest(scriptId: "script-2"));
        secondCompileStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        allowCompileToReturn.Set();
        var resolved = await Task.WhenAll(firstTask, secondTask);

        resolved.Select(artifact => artifact.ScriptId)
            .Should()
            .BeEquivalentTo(["script-1", "script-2"]);

        resolver.Resolve(CreateRequest(scriptId: "script-3"));

        var behaviors = resolved.Select(artifact => artifact.CreateBehavior()).ToArray();
        behaviors.Should().HaveCount(2);
        disposeObserved.Wait(TimeSpan.FromMilliseconds(100)).Should().BeFalse();

        foreach (var behavior in behaviors)
            behavior.Should().BeAssignableTo<IDisposable>().Subject.Dispose();

        disposeObserved.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        lock (disposeGate)
        {
            disposed.Should().BeEquivalentTo(["script-1", "script-2"]);
        }

        compiler.CallCount.Should().Be(3);
    }

    private static Task<ScriptBehaviorArtifact> ResolveOnDedicatedThread(
        CachedScriptBehaviorArtifactResolver resolver,
        ScriptBehaviorArtifactRequest request) =>
        Task.Factory.StartNew(
            () => resolver.Resolve(request),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static ScriptBehaviorArtifactRequest CreateRequest() =>
        CreateRequest(
            scriptId: "script-1",
            revision: "rev-1",
            sourceHash: "hash-1",
            entryBehaviorTypeName: string.Empty);

    private static ScriptBehaviorArtifactRequest CreateRequest(
        string scriptId,
        string revision = "rev-1",
        string sourceHash = "hash-1",
        string entryBehaviorTypeName = "") =>
        new(
            scriptId,
            revision,
            new ScriptSourcePackage(
                ScriptSourcePackage.CurrentFormat,
                [new ScriptSourceFile("Behavior.cs", "public sealed class DraftBehavior {}")],
                Array.Empty<ScriptSourceFile>(),
                entryBehaviorTypeName),
            sourceHash);

    private static ScriptBehaviorArtifact CreateArtifact(
        string scriptId,
        string revision,
        Action? onDispose = null,
        Func<IScriptBehaviorBridge>? behaviorFactory = null)
    {
        behaviorFactory ??= static () => new NoopBehavior();
        var behavior = behaviorFactory();
        return new ScriptBehaviorArtifact(
            scriptId,
            revision,
            "hash-1",
            behavior.Descriptor,
            behavior.Descriptor.ToContract(),
            behaviorFactory,
            () =>
            {
                onDispose?.Invoke();
                return ValueTask.CompletedTask;
            });
    }

    private sealed class CountingCompiler(Func<ScriptBehaviorArtifact> artifactFactory) : IScriptBehaviorCompiler
    {
        public int CallCount { get; private set; }

        public ScriptBehaviorCompilationResult Compile(ScriptBehaviorCompilationRequest request)
        {
            _ = request;
            CallCount += 1;
            return new ScriptBehaviorCompilationResult(true, artifactFactory(), Array.Empty<string>());
        }
    }

    private sealed class FailingCompiler : IScriptBehaviorCompiler
    {
        public ScriptBehaviorCompilationResult Compile(ScriptBehaviorCompilationRequest request)
        {
            _ = request;
            return new ScriptBehaviorCompilationResult(false, null, ["compile-failed"]);
        }
    }

    private sealed class FailOnceCompiler : IScriptBehaviorCompiler
    {
        public int CallCount { get; private set; }

        public ScriptBehaviorCompilationResult Compile(ScriptBehaviorCompilationRequest request)
        {
            CallCount += 1;
            if (CallCount == 1)
                return new ScriptBehaviorCompilationResult(false, null, ["compile-failed"]);

            return new ScriptBehaviorCompilationResult(
                true,
                CreateArtifact(request.ScriptId, request.Revision),
                Array.Empty<string>());
        }
    }

    private sealed class RequestEchoCompiler(
        Action<ScriptBehaviorCompilationRequest>? onDispose = null,
        Action<ScriptBehaviorCompilationRequest>? onCompile = null,
        Func<IScriptBehaviorBridge>? behaviorFactory = null) : IScriptBehaviorCompiler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ScriptBehaviorCompilationResult Compile(ScriptBehaviorCompilationRequest request)
        {
            Interlocked.Increment(ref _callCount);
            onCompile?.Invoke(request);
            return new ScriptBehaviorCompilationResult(
                true,
                CreateArtifact(
                    request.ScriptId,
                    request.Revision,
                    () => onDispose?.Invoke(request),
                    behaviorFactory),
                Array.Empty<string>());
        }
    }

    private class NoopBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder
                .OnCommand<SimpleTextCommand>(HandleAsync)
                .OnEvent<SimpleTextEvent>(
                    apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty })
                .ProjectState(static (state, _) => state == null
                    ? null
                    : new SimpleTextReadModel
                    {
                        HasValue = !string.IsNullOrWhiteSpace(state.Value),
                        Value = state.Value ?? string.Empty,
                    });
        }

        private static Task HandleAsync(
            SimpleTextCommand command,
            ScriptCommandContext<SimpleTextState> context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            context.Emit(new SimpleTextEvent
            {
                CommandId = command.CommandId ?? string.Empty,
                Current = new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = command.Value ?? string.Empty,
                },
            });
            return Task.CompletedTask;
        }
    }

    private sealed class AsyncDisposableNoopBehavior : NoopBehavior, IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
