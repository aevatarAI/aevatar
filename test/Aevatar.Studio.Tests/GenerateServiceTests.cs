using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Studio.Application.Scripts.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Domain.Studio.Services;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.Studio.Infrastructure.Serialization;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class GenerateServiceTests
{
    [Fact]
    public async Task WorkflowGenerateService_ShouldReturnValidatedDto_FromStreamedProvider()
    {
        var provider = new FakeLLMProvider(
            """
            name: generated
            steps:
              - id: chat
                type: llm_call
            """,
            "planning\n");
        var service = new WorkflowGenerateService(
            new AppAuthoringChatSessionFactory(provider),
            CreateWorkflowOrchestrator(),
            new WorkflowGeneratePromptCatalog(NullLogger<WorkflowGeneratePromptCatalog>.Instance));
        var reasoning = new List<string>();

        var result = await service.GenerateAsync(
            new WorkflowGenerateRequest("Create a workflow", null, [], null),
            (delta, ct) =>
            {
                _ = ct;
                reasoning.Add(delta);
                return Task.CompletedTask;
            },
            null,
            CancellationToken.None);

        result.Attempts.Should().Be(1);
        result.Yaml.Should().Contain("name: generated");
        result.Findings.Should().OnlyContain(finding => finding.Level != ValidationLevel.Error);
        reasoning.Should().Contain("planning\n");
        provider.Requests.Should().ContainSingle();
        provider.Requests[0].Messages[0].Content.Should().Contain("Author and repair Aevatar workflow YAML.");
        provider.Requests[0].Messages[^1].Content.Should().Contain("Create a workflow");
    }

    [Fact]
    public async Task ScriptGenerateService_ShouldReturnScriptDto_FromStreamedProvider()
    {
        const string source = "public sealed class DraftBehavior {}";
        var provider = new FakeLLMProvider(source, "reasoning\n");
        var compiler = new FakeScriptCompiler();
        var service = new ScriptGenerateService(
            new AppAuthoringChatSessionFactory(provider),
            new ScriptGenerateOrchestrator(compiler),
            new ScriptGeneratePromptCatalog(NullLogger<ScriptGeneratePromptCatalog>.Instance));
        var reasoning = new List<string>();

        var result = await service.GenerateAsync(
            new ScriptGenerateRequest("Create a script", null, null),
            (delta, ct) =>
            {
                _ = ct;
                reasoning.Add(delta);
                return Task.CompletedTask;
            },
            null,
            CancellationToken.None);

        result.Attempts.Should().Be(1);
        result.Source.Should().Be(source);
        result.Diagnostics.Should().BeEmpty();
        reasoning.Should().Contain("reasoning\n");
        compiler.Requests.Should().ContainSingle();
        provider.Requests.Should().ContainSingle();
        provider.Requests[0].Messages[0].Content.Should().Contain("Author and repair Aevatar script packages");
        provider.Requests[0].Messages[^1].Content.Should().Contain("Create a script");
    }

    private static WorkflowGenerateOrchestrator CreateWorkflowOrchestrator()
    {
        var profile = WorkflowCompatibilityProfile.AevatarV1;
        var editor = new WorkflowEditorService(
            new YamlWorkflowDocumentService(profile),
            new WorkflowDocumentNormalizer(profile),
            new WorkflowValidator(profile),
            new WorkflowGraphMapper(profile),
            new TextDiffService());
        return new WorkflowGenerateOrchestrator(editor);
    }

    private sealed class FakeLLMProvider(string content, string? reasoning = null) : ILLMProvider, ILLMProviderFactory
    {
        public string Name => "fake";

        public List<LLMRequest> Requests { get; } = [];

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new LLMResponse
            {
                Content = content,
            });
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            if (!string.IsNullOrEmpty(reasoning))
            {
                yield return new LLMStreamChunk
                {
                    DeltaReasoningContent = reasoning,
                };
            }

            yield return new LLMStreamChunk
            {
                DeltaContent = content,
                IsLast = true,
                FinishReason = "stop",
            };
            await Task.CompletedTask;
        }

        public ILLMProvider GetProvider(string name)
        {
            name.Should().Be(Name);
            return this;
        }

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];
    }

    private sealed class FakeScriptCompiler : IScriptBehaviorCompiler
    {
        public List<ScriptBehaviorCompilationRequest> Requests { get; } = [];

        public ScriptBehaviorCompilationResult Compile(ScriptBehaviorCompilationRequest request)
        {
            Requests.Add(request);
            return new ScriptBehaviorCompilationResult(
                true,
                null,
                Array.Empty<string>());
        }
    }
}
