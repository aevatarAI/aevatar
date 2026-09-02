using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Authoring;
using Aevatar.Studio.Infrastructure.Authoring;
using Aevatar.Workflow.Abstractions.Workflows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class ChatRuntimeStudioAuthoringLLMStreamPortTests
{
    [Fact]
    public async Task StreamAsync_WhenProviderStreamsContentAndReasoning_ShouldMapTypedChunks()
    {
        var provider = new SplitStreamingProviderFactory(
            [new LLMStreamChunk { DeltaReasoningContent = "thinking" }, new LLMStreamChunk { DeltaContent = "draft" }]);
        var port = CreatePort(provider);

        var chunks = await port.StreamAsync(
                new StudioAuthoringLLMRequest(
                    StudioAuthoringKind.Workflow,
                    "write workflow",
                    "request-workflow",
                    new Dictionary<string, string> { ["scope"] = "test" }),
                CancellationToken.None)
            .ToListAsync();

        chunks.Select(chunk => chunk.DeltaReasoningContent).Should().Contain("thinking");
        chunks.Select(chunk => chunk.DeltaContent).Should().Contain("draft");
        provider.StreamCallCount.Should().Be(1);
        provider.LastRequestId.Should().Be("request-workflow");
        provider.LastMetadata.Should().ContainKey("scope");
    }

    [Theory]
    [InlineData(StudioAuthoringKind.Workflow, "workflow YAML")]
    [InlineData(StudioAuthoringKind.Script, "script packages")]
    public async Task StreamAsync_ShouldSelectPromptByAuthoringKind(
        StudioAuthoringKind kind,
        string expectedPromptText)
    {
        var provider = new SplitStreamingProviderFactory([new LLMStreamChunk { DeltaContent = "ok" }]);
        var port = CreatePort(provider);

        _ = await port.StreamAsync(
                new StudioAuthoringLLMRequest(kind, "prompt", $"request-{kind}", null),
                CancellationToken.None)
            .ToListAsync();

        provider.LastSystemPrompt.Should().Contain(expectedPromptText);
    }

    [Fact]
    public async Task StreamAsync_WhenWorkflowAuthoring_ShouldConstrainPromptToCanonicalRootSchema()
    {
        var provider = new SplitStreamingProviderFactory([new LLMStreamChunk { DeltaContent = "ok" }]);
        var port = CreatePort(provider);

        _ = await port.StreamAsync(
                new StudioAuthoringLLMRequest(
                    StudioAuthoringKind.Workflow,
                    "prompt",
                    "request-workflow-schema",
                    null),
                CancellationToken.None)
            .ToListAsync();

        provider.LastSystemPrompt.Should().Contain(
            $"The only supported top-level fields are: {WorkflowYamlRootSchema.FormatAuthorableRootFields()}.");
        provider.LastSystemPrompt.Should().Contain(
            $"The only supported top-level fields are {WorkflowYamlRootSchema.FormatAuthorableRootFields()}.");
        provider.LastSystemPrompt.Should().Contain(
            $"Do not emit top-level fields from other workflow dialects, including {WorkflowYamlRootSchema.FormatUnsupportedDialectRootFields()}.");
        provider.LastSystemPrompt.Should().NotContain("{{workflow_authorable_root_fields}}");
        provider.LastSystemPrompt.Should().NotContain("{{workflow_unsupported_dialect_root_fields}}");
        provider.LastSystemPrompt.Should().NotContain("The only supported top-level fields are `name`, `description`, `configuration`, `roles`, and `steps`.");
        provider.LastSystemPrompt.Should().NotContain("including `version`, `inputs`, `outputs`, `triggers`, `on`, `env`, or `jobs`.");
        provider.LastSystemPrompt.Should().NotContain("stream_buffer_capacity");
        provider.LastSystemPrompt.Should().NotContain("agent_type: RoleGAgent");
        provider.LastSystemPrompt.Should().NotContain("agent_id: role");
    }

    private static ChatRuntimeStudioAuthoringLLMStreamPort CreatePort(SplitStreamingProviderFactory provider) =>
        new(
            provider,
            new WorkflowAuthoringPromptCatalog(NullLogger<WorkflowAuthoringPromptCatalog>.Instance),
            new ScriptAuthoringPromptCatalog(NullLogger<ScriptAuthoringPromptCatalog>.Instance));

    private sealed class SplitStreamingProviderFactory(IReadOnlyList<LLMStreamChunk> chunks)
        : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "studio-test-provider";

        public int StreamCallCount { get; private set; }

        public string? LastRequestId { get; private set; }

        public string? LastSystemPrompt { get; private set; }

        public IReadOnlyDictionary<string, string>? LastMetadata { get; private set; }

        public ILLMProvider GetProvider(string name)
        {
            _ = name;
            return this;
        }

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequestId = request.RequestId;
            LastSystemPrompt = request.Messages?.FirstOrDefault()?.Content;
            LastMetadata = request.Metadata;
            StreamCallCount++;
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
            }

            yield return new LLMStreamChunk
            {
                IsLast = true,
                FinishReason = "stop",
            };

            await Task.CompletedTask;
        }
    }
}
