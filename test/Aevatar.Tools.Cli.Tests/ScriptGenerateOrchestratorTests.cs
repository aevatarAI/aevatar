using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Studio.Application.Scripts.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
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
        prompts[0].Should().Contain("scriptPackage");
        prompts[1].Should().Contain("AppScriptCommand");
        prompts[1].Should().Contain("parse it from AppScriptCommand.Input");
    }

    [Fact]
    public async Task GenerateAsync_ShouldAcceptFullPackageJson_AndCompileReturnedPackage()
    {
        ScriptBehaviorCompilationRequest? capturedRequest = null;
        var compiler = new FakeCompiler([
            (
                CreateSuccessResult(),
                (Action<ScriptBehaviorCompilationRequest>?)(request => capturedRequest = request)
            ),
        ]);
        var orchestrator = new ScriptGenerateOrchestrator(compiler);

        var result = await orchestrator.GenerateAsync(
            new ScriptGenerateRequest(
                "Split the behavior into helper files",
                null,
                null),
            static (prompt, metadata, ct) =>
            {
                _ = prompt;
                _ = metadata;
                _ = ct;
                return Task.FromResult<string?>(
                    """
                    {
                      "currentFilePath": "Behavior.cs",
                      "scriptPackage": {
                        "csharpSources": [
                          { "path": "Behavior.cs", "content": "public sealed class DraftBehavior {}" },
                          { "path": "Helper.cs", "content": "internal static class Helper {}" }
                        ],
                        "protoFiles": [],
                        "entryBehaviorTypeName": "DraftBehavior",
                        "entrySourcePath": "Behavior.cs"
                      }
                    }
                    """);
            },
            null,
            CancellationToken.None);

        result.Package.Should().NotBeNull();
        result.Package!.CsharpSources.Should().HaveCount(2);
        result.CurrentFilePath.Should().Be("Behavior.cs");
        result.Source.Should().Contain("DraftBehavior");
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Package.CSharpSources.Select(static file => file.Path)
            .Should().Contain(new[] { "Behavior.cs", "Helper.cs" });
    }

    [Fact]
    public async Task GenerateAsync_ShouldMergeCurrentFileEditsIntoExistingPackage_WhenAiReturnsSourceOnly()
    {
        ScriptBehaviorCompilationRequest? capturedRequest = null;
        var compiler = new FakeCompiler([
            (
                CreateSuccessResult(),
                (Action<ScriptBehaviorCompilationRequest>?)(request => capturedRequest = request)
            ),
        ]);
        var orchestrator = new ScriptGenerateOrchestrator(compiler);

        var result = await orchestrator.GenerateAsync(
            new ScriptGenerateRequest(
                "Only update the behavior file",
                "public sealed class DraftBehavior { }",
                null,
                new AppScriptPackage(
                    [
                        new AppScriptPackageFile("Behavior.cs", "public sealed class DraftBehavior { }"),
                        new AppScriptPackageFile("Helper.cs", "internal static class Helper { }"),
                    ],
                    [],
                    "DraftBehavior",
                    "Behavior.cs"),
                "Behavior.cs"),
            static (prompt, metadata, ct) =>
            {
                _ = prompt;
                _ = metadata;
                _ = ct;
                return Task.FromResult<string?>("public sealed class DraftBehavior { public const int Version = 2; }");
            },
            null,
            CancellationToken.None);

        result.Package.Should().NotBeNull();
        result.Package!.CsharpSources.Should().HaveCount(2);
        result.Package.CsharpSources.Single(file => file.Path == "Helper.cs").Content.Should().Contain("Helper");
        result.Package.CsharpSources.Single(file => file.Path == "Behavior.cs").Content.Should().Contain("Version = 2");
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Package.CSharpSources.Should().HaveCount(2);
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

    private static ScriptBehaviorCompilationResult CreateSuccessResult()
    {
        return new ScriptBehaviorCompilationResult(
            true,
            CreateArtifact(() => { }),
            Array.Empty<string>());
    }

    private sealed class FakeCompiler : IScriptBehaviorCompiler
    {
        private readonly Queue<ScriptBehaviorCompilationResult> _results;
        private readonly Queue<Action<ScriptBehaviorCompilationRequest>?> _hooks = new();

        public FakeCompiler(IEnumerable<ScriptBehaviorCompilationResult> results)
        {
            _results = new Queue<ScriptBehaviorCompilationResult>(results);
        }

        public FakeCompiler(IEnumerable<(ScriptBehaviorCompilationResult Result, Action<ScriptBehaviorCompilationRequest>? Hook)> results)
        {
            _results = new Queue<ScriptBehaviorCompilationResult>();
            foreach (var item in results)
            {
                _results.Enqueue(item.Result);
                _hooks.Enqueue(item.Hook);
            }
        }

        public ScriptBehaviorCompilationResult Compile(ScriptBehaviorCompilationRequest request)
        {
            if (_hooks.Count > 0)
                _hooks.Dequeue()?.Invoke(request);
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
