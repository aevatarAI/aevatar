using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Tools.Cli.Hosting;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Tools.Cli.Tests;

public class ScriptGenerateOrchestratorTests
{
    [Fact]
    public async Task GenerateAsync_ShouldDisposeCompiledArtifactAfterSuccessfulValidation()
    {
        var disposed = 0;
        var compiler = new FakeCompiler([
            new ScriptBehaviorCompilationResult(
                true,
                CreateArtifact(() => disposed++),
                Array.Empty<string>()),
        ]);
        var orchestrator = new ScriptGenerateOrchestrator(compiler);

        var result = await orchestrator.GenerateAsync(
            new ScriptGenerateRequest(
                "Uppercase the input",
                null,
                null),
            static (prompt, metadata, ct) =>
            {
                _ = prompt;
                _ = metadata;
                _ = ct;
                return Task.FromResult<string?>("public sealed class DraftBehavior {}");
            },
            null,
            CancellationToken.None);

        result.Attempts.Should().Be(1);
        disposed.Should().Be(1);
    }

    [Fact]
    public async Task GenerateAsync_ShouldDisposeArtifactsDuringRetry_AndKeepAppScriptCommandConstraintInPrompts()
    {
        var disposed = 0;
        var compiler = new FakeCompiler([
            new ScriptBehaviorCompilationResult(
                false,
                CreateArtifact(() => disposed++),
                ["draft-1 failed"]),
            new ScriptBehaviorCompilationResult(
                true,
                CreateArtifact(() => disposed++),
                Array.Empty<string>()),
        ]);
        var orchestrator = new ScriptGenerateOrchestrator(compiler);
        var prompts = new List<string>();

        var result = await orchestrator.GenerateAsync(
            new ScriptGenerateRequest(
                "Accept structured input with name and priority",
                null,
                null),
            (prompt, metadata, ct) =>
            {
                _ = metadata;
                _ = ct;
                prompts.Add(prompt);
                return Task.FromResult<string?>("public sealed class DraftBehavior {}");
            },
            null,
            CancellationToken.None);

        result.Attempts.Should().Be(2);
        disposed.Should().Be(2);
        prompts.Should().HaveCount(2);
        prompts[0].Should().Contain("AppScriptCommand");
        prompts[0].Should().Contain("only inbound command contract");
        prompts[1].Should().Contain("AppScriptCommand");
        prompts[1].Should().Contain("parse it from AppScriptCommand.Input");
    }

    [Fact]
    public void ScriptGeneratePromptCatalog_ShouldKeepGeneratorBoundToAppScriptCommand()
    {
        var catalog = new ScriptGeneratePromptCatalog(NullLogger<ScriptGeneratePromptCatalog>.Instance);

        catalog.SystemPrompt.Should().Contain("Use AppScriptCommand as the only inbound command contract.");
        catalog.SystemPrompt.Should().Contain("parse it from AppScriptCommand.Input");
        catalog.SystemPrompt.Should().NotContain("unless the user explicitly asks for a richer contract");
    }

    private static ScriptBehaviorArtifact CreateArtifact(Action onDispose)
    {
        var descriptor = new ScriptBehaviorDescriptor(
            typeof(Empty),
            typeof(Empty),
            Empty.Descriptor,
            Empty.Descriptor,
            "type.googleapis.com/google.protobuf.Empty",
            "type.googleapis.com/google.protobuf.Empty",
            new Dictionary<string, ScriptCommandRegistration>(),
            new Dictionary<string, ScriptSignalRegistration>(),
            new Dictionary<string, ScriptDomainEventRegistration>(),
            null,
            ByteString.Empty,
            new ScriptRuntimeSemanticsSpec());

        return new ScriptBehaviorArtifact(
            "app-script-preview",
            "draft",
            "hash",
            descriptor,
            ScriptGAgentContract.Empty,
            static () => new FakeBehavior(),
            () =>
            {
                onDispose();
                return ValueTask.CompletedTask;
            });
    }

    private sealed class FakeCompiler : IScriptBehaviorCompiler
    {
        private readonly Queue<ScriptBehaviorCompilationResult> _results;

        public FakeCompiler(IEnumerable<ScriptBehaviorCompilationResult> results)
        {
            _results = new Queue<ScriptBehaviorCompilationResult>(results);
        }

        public ScriptBehaviorCompilationResult Compile(ScriptBehaviorCompilationRequest request)
        {
            _ = request;
            if (_results.Count == 0)
                throw new InvalidOperationException("No more compile results are available.");

            return _results.Dequeue();
        }
    }

    private sealed class FakeBehavior : IScriptBehaviorBridge
    {
        public ScriptBehaviorDescriptor Descriptor => throw new NotSupportedException();

        public Task<IReadOnlyList<IMessage>> DispatchAsync(
            IMessage inbound,
            ScriptDispatchContext context,
            CancellationToken ct) => throw new NotSupportedException();

        public IMessage? ApplyDomainEvent(
            IMessage? currentState,
            IMessage domainEvent,
            ScriptFactContext context) => throw new NotSupportedException();

        public IMessage? BuildReadModel(
            IMessage? currentState,
            ScriptFactContext context) => throw new NotSupportedException();
    }
}
