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

        second.Should().BeSameAs(first);
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

        resolved[0].Should().BeSameAs(resolved[1]);
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

    private static ScriptBehaviorArtifactRequest CreateRequest() =>
        new(
            "script-1",
            "rev-1",
            ScriptSourcePackageSerializer.DeserializeOrWrapCSharp("public sealed class DraftBehavior {}"),
            "hash-1");

    private static ScriptBehaviorArtifact CreateArtifact(
        string scriptId,
        string revision,
        Action? onDispose = null)
    {
        var behavior = new NoopBehavior();
        return new ScriptBehaviorArtifact(
            scriptId,
            revision,
            "hash-1",
            behavior.Descriptor,
            behavior.Descriptor.ToContract(),
            static () => new NoopBehavior(),
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

    private sealed class NoopBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
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
}
