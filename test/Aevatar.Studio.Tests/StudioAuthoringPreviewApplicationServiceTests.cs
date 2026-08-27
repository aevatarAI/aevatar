using System.Runtime.CompilerServices;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Studio.Application.Scripts.Contracts;
using Aevatar.Studio.Application.Studio.Authoring;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Services;
using Aevatar.Studio.Infrastructure.Serialization;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RuntimeWorkflowValidator = Aevatar.Workflow.Core.Validation.WorkflowValidator;
using StudioWorkflowValidator = Aevatar.Studio.Domain.Studio.Services.WorkflowValidator;

namespace Aevatar.Studio.Tests;

public sealed class StudioAuthoringPreviewApplicationServiceTests
{
    [Fact]
    public async Task PreviewAsync_WhenWorkflowRequest_ShouldStreamReasoningProgressContentAndCompletion()
    {
        var llm = new FakeLLMStreamPort(["name: generated\nsteps:\n  - id: chat\n    type: llm_call\n    allowed_tools: []"], ["thinking"]);
        var service = CreateService(llm);

        var events = await service.PreviewAsync(
                new StudioAuthoringPreviewRequest(
                    StudioAuthoringKind.Workflow,
                    "Create a simple workflow",
                    AvailableWorkflowNames: []),
                CancellationToken.None)
            .ToListAsync();

        events.Should().Contain(e => e is StudioAuthoringPreviewEvent.ReasoningDelta);
        events.Should().Contain(e => e is StudioAuthoringPreviewEvent.Progress);
        events.Should().Contain(e => e is StudioAuthoringPreviewEvent.ContentDelta);
        events.OfType<StudioAuthoringPreviewEvent.WorkflowCompleted>().Single().Result.Yaml.Should().Contain("generated");
        llm.StreamCallCount.Should().Be(1);
    }

    [Fact]
    public async Task PreviewAsync_WhenWorkflowGenerated_ShouldEmitYamlAcceptedByPlatformParser()
    {
        var llm = new FakeLLMStreamPort([
            """
            version: "1.0"
            inputs: {}
            name: generated
            roles:
              - id: assistant
                name: Assistant
            steps:
              - id: chat
                type: llm_call
                target_role: assistant
            """,
            """
            name: generated
            roles:
              - id: assistant
                name: Assistant
            steps:
              - id: chat
                type: llm_call
                target_role: assistant
                allowed_tools: []
            """
        ], oneContentPerCall: true);
        var publicationParser = new PublicationPolicyParser();
        var service = CreateService(llm, publicationParser: publicationParser);

        var events = await service.PreviewAsync(
                new StudioAuthoringPreviewRequest(
                    StudioAuthoringKind.Workflow,
                    "Create a simple workflow",
                    AvailableWorkflowNames: []),
                CancellationToken.None)
            .ToListAsync();

        var yaml = events.OfType<StudioAuthoringPreviewEvent.WorkflowCompleted>().Single().Result.Yaml;
        yaml.Should().NotContain("version:");
        yaml.Should().NotContain("inputs:");
        var parsed = new WorkflowParser().Parse(yaml);
        parsed.Name.Should().Be("generated");
        parsed.Steps.Should().ContainSingle(step => step.Id == "chat");
        var publicationParse = await publicationParser.ParseWorkflowYamlForPublicationAsync(yaml);
        publicationParse.Succeeded.Should().BeTrue(publicationParse.Error);
        publicationParser.PublicationParseCount.Should().BeGreaterThan(0);
        llm.StreamCallCount.Should().Be(2);
    }

    [Fact]
    public async Task PreviewAsync_WhenWorkflowGeneratedWithoutAllowedTools_ShouldRepairToPublicationReadyYaml()
    {
        var llm = new FakeLLMStreamPort([
            """
            name: generated
            steps:
              - id: chat
                type: llm_call
            """,
            """
            name: generated
            steps:
              - id: chat
                type: llm_call
                allowed_tools: []
            """
        ], oneContentPerCall: true);
        var publicationParser = new PublicationPolicyParser();
        var service = CreateService(llm, publicationParser: publicationParser);

        var events = await service.PreviewAsync(
                new StudioAuthoringPreviewRequest(
                    StudioAuthoringKind.Workflow,
                    "Create a simple workflow",
                    AvailableWorkflowNames: []),
                CancellationToken.None)
            .ToListAsync();

        var yaml = events.OfType<StudioAuthoringPreviewEvent.WorkflowCompleted>().Single().Result.Yaml;
        yaml.Should().Contain("allowed_tools: []");
        var publicationParse = await publicationParser.ParseWorkflowYamlForPublicationAsync(yaml);
        publicationParse.Succeeded.Should().BeTrue(publicationParse.Error);
        publicationParser.PublicationParseCount.Should().Be(3);
        llm.StreamCallCount.Should().Be(2);
    }

    [Fact]
    public async Task PreviewAsync_WhenScriptRequest_ShouldReturnPackageAndCurrentFile()
    {
        var llm = new FakeLLMStreamPort([
            """
            {
              "currentFilePath": "Behavior.cs",
              "scriptPackage": {
                "csharpSources": [{ "path": "Behavior.cs", "content": "public sealed class DraftBehavior {}" }],
                "protoFiles": [],
                "entryBehaviorTypeName": "DraftBehavior",
                "entrySourcePath": "Behavior.cs"
              }
            }
            """
        ]);
        var service = CreateService(llm, new FakeCompiler(CreateSuccessResult()));

        var events = await service.PreviewAsync(
                new StudioAuthoringPreviewRequest(
                    StudioAuthoringKind.Script,
                    "Create a script"),
                CancellationToken.None)
            .ToListAsync();

        var completed = events.OfType<StudioAuthoringPreviewEvent.ScriptCompleted>().Single();
        completed.Result.CurrentFilePath.Should().Be("Behavior.cs");
        completed.Result.Package.Should().NotBeNull();
        completed.Result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task PreviewAsync_WhenPromptIsBlank_ShouldFailBeforeLLM()
    {
        var llm = new FakeLLMStreamPort(["unused"]);
        var service = CreateService(llm);

        var act = () => service.PreviewAsync(
                new StudioAuthoringPreviewRequest(StudioAuthoringKind.Workflow, "   "),
                CancellationToken.None)
            .ToListAsync().AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*prompt is required*");
        llm.StreamCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PreviewAsync_WhenTwoRequestsRunTogether_ShouldNotSerializeWithProcessGate()
    {
        var llm = new BlockingLLMStreamPort();
        var service = CreateService(llm);

        var first = service.PreviewAsync(
                new StudioAuthoringPreviewRequest(
                    StudioAuthoringKind.Workflow,
                    "Create first workflow",
                    AvailableWorkflowNames: []),
                CancellationToken.None)
            .ToListAsync().AsTask();
        var second = service.PreviewAsync(
                new StudioAuthoringPreviewRequest(
                    StudioAuthoringKind.Workflow,
                    "Create second workflow",
                    AvailableWorkflowNames: []),
                CancellationToken.None)
            .ToListAsync().AsTask();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await llm.BothStarted.Task.WaitAsync(timeout.Token);
        llm.Release.SetResult();
        _ = await Task.WhenAll(first, second);

        llm.MaxConcurrent.Should().BeGreaterThan(1);
    }

    private static StudioAuthoringPreviewApplicationService CreateService(
        IStudioAuthoringLLMStreamPort llm,
        IScriptBehaviorCompiler? compiler = null,
        IWorkflowDefinitionParser? publicationParser = null)
    {
        var profile = WorkflowCompatibilityProfile.AevatarV1;
        var editor = new WorkflowEditorService(
            new YamlWorkflowDocumentService(profile),
            new WorkflowDocumentNormalizer(profile),
            new StudioWorkflowValidator(profile),
            new WorkflowGraphMapper(profile),
            new TextDiffService());

        return new StudioAuthoringPreviewApplicationService(
            llm,
            new WorkflowAuthoringPreviewGenerator(
                editor,
                publicationParser ?? new PublicationPolicyParser()),
            new ScriptAuthoringPreviewGenerator(compiler ?? new FakeCompiler(CreateSuccessResult())));
    }

    private static ScriptBehaviorCompilationResult CreateSuccessResult() =>
        new(
            true,
            null,
            Array.Empty<string>());

    private sealed class PublicationPolicyParser : IWorkflowDefinitionParser
    {
        private readonly WorkflowParser _parser = new();

        public int PublicationParseCount { get; private set; }

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default) =>
            ParseCore(workflowYaml, requireExplicitLlmAgentToolScopes: false, ct);

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlForPublicationAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            PublicationParseCount++;
            return ParseCore(workflowYaml, requireExplicitLlmAgentToolScopes: true, ct);
        }

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        private Task<WorkflowYamlParseResult> ParseCore(
            string workflowYaml,
            bool requireExplicitLlmAgentToolScopes,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var workflow = _parser.Parse(workflowYaml);
                var errors = RuntimeWorkflowValidator.Validate(
                    workflow,
                    new RuntimeWorkflowValidator.WorkflowValidationOptions
                    {
                        RequireExplicitLlmAgentToolScopes = requireExplicitLlmAgentToolScopes,
                    },
                    availableWorkflowNames: null);
                return Task.FromResult(errors.Count == 0
                    ? WorkflowYamlParseResult.Success(
                        workflow.Name,
                        WorkflowAuthorizationDependencyEvaluator.Evaluate(workflow))
                    : WorkflowYamlParseResult.Invalid(string.Join("; ", errors)));
            }
            catch (Exception exception)
            {
                return Task.FromResult(WorkflowYamlParseResult.Invalid(exception.Message));
            }
        }
    }

    private sealed class FakeLLMStreamPort(
        IReadOnlyList<string> contentChunks,
        IReadOnlyList<string>? reasoningChunks = null,
        bool oneContentPerCall = false)
        : IStudioAuthoringLLMStreamPort
    {
        public int StreamCallCount { get; private set; }

        public async IAsyncEnumerable<StudioAuthoringLLMChunk> StreamAsync(
            StudioAuthoringLLMRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            request.RequestId.Should().NotBeNullOrWhiteSpace();
            StreamCallCount++;
            foreach (var reasoning in reasoningChunks ?? [])
                yield return new StudioAuthoringLLMChunk(null, reasoning);

            var chunks = oneContentPerCall
                ? contentChunks.Skip(Math.Min(StreamCallCount - 1, contentChunks.Count - 1)).Take(1)
                : contentChunks;
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return new StudioAuthoringLLMChunk(chunk, null);
            }

            await Task.CompletedTask;
        }
    }

    private sealed class BlockingLLMStreamPort : IStudioAuthoringLLMStreamPort
    {
        private int _current;
        private int _started;

        public TaskCompletionSource BothStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxConcurrent { get; private set; }

        public async IAsyncEnumerable<StudioAuthoringLLMChunk> StreamAsync(
            StudioAuthoringLLMRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            _ = request;
            var current = Interlocked.Increment(ref _current);
            MaxConcurrent = Math.Max(MaxConcurrent, current);
            if (Interlocked.Increment(ref _started) == 2)
                BothStarted.TrySetResult();

            try
            {
                await Release.Task.WaitAsync(ct);
                yield return new StudioAuthoringLLMChunk(
                    "name: generated\nsteps:\n  - id: chat\n    type: llm_call\n    allowed_tools: []",
                    null);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    private sealed class FakeCompiler(ScriptBehaviorCompilationResult result) : IScriptBehaviorCompiler
    {
        public ScriptBehaviorCompilationResult Compile(ScriptBehaviorCompilationRequest request)
        {
            request.Package.CSharpSources.Should().NotBeEmpty();
            return result;
        }
    }
}
