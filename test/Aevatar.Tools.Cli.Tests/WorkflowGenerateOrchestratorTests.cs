using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Services;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.Studio.Infrastructure.Serialization;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public class WorkflowGenerateOrchestratorTests
{
    [Fact]
    public async Task GenerateAsync_ShouldRetryUntilYamlIsValid()
    {
        var orchestrator = CreateOrchestrator();
        var responses = new Queue<string>([
            "name: broken\nsteps:\n  - id: only_step\n    type: llm_call\n    next: missing_step",
            "name: repaired\nsteps:\n  - id: only_step\n    type: llm_call"
        ]);
        var prompts = new List<string>();
        var progress = new List<WorkflowGenerateProgress>();

        var result = await orchestrator.GenerateAsync(
            new WorkflowGenerateRequest(
                "Create a simple workflow",
                null,
                [],
                null),
            (prompt, metadata, ct) =>
            {
                _ = metadata;
                _ = ct;
                prompts.Add(prompt);
                return Task.FromResult<string?>(responses.Dequeue());
            },
            (update, ct) =>
            {
                _ = ct;
                progress.Add(update);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        result.Attempts.Should().Be(2);
        result.Yaml.Should().Contain("name: repaired");
        result.Yaml.Should().Contain("id: only_step");
        prompts.Should().HaveCount(2);
        prompts[1].Should().Contain("Validation findings:");
        prompts[1].Should().Contain("missing_step");
        progress.Select(item => item.Stage).Should().ContainInOrder(
            WorkflowGenerateProgressStage.GeneratingDraft,
            WorkflowGenerateProgressStage.ValidatingDraft,
            WorkflowGenerateProgressStage.RepairingDraft,
            WorkflowGenerateProgressStage.GeneratingDraft,
            WorkflowGenerateProgressStage.ValidatingDraft,
            WorkflowGenerateProgressStage.Completed);
    }

    [Fact]
    public async Task GenerateAsync_WhenNoValidYamlIsProduced_ShouldFail()
    {
        var orchestrator = CreateOrchestrator();

        var act = () => orchestrator.GenerateAsync(
            new WorkflowGenerateRequest(
                "Create a workflow",
                null,
                [],
                null),
            (prompt, metadata, ct) =>
            {
                _ = prompt;
                _ = metadata;
                _ = ct;
                return Task.FromResult<string?>("not yaml at all");
            },
            null,
            CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*valid workflow YAML*");
    }

    [Fact]
    public async Task GenerateAsync_ShouldNormalizeAiHallucinatedStepFieldsBeforeFailing()
    {
        var orchestrator = CreateOrchestrator();

        var result = await orchestrator.GenerateAsync(
            new WorkflowGenerateRequest(
                "Create the simplest possible chat workflow",
                null,
                [],
                null),
            (prompt, metadata, ct) =>
            {
                _ = prompt;
                _ = metadata;
                _ = ct;
                return Task.FromResult<string?>(
                    """
                    name: ai-draft
                    steps:
                      - id: chat
                        type: llm_call
                        model: gpt-5.4
                        messages:
                          - role: user
                            content: hello
                        params:
                          prompt_prefix: "Reply clearly."
                    """);
            },
            null,
            CancellationToken.None);

        result.Attempts.Should().Be(1);
        result.Yaml.Should().Contain("name: ai-draft");
        result.Yaml.Should().Contain("id: chat");
        result.Yaml.Should().Contain("type: llm_call");
        result.Yaml.Should().NotContain("model:");
        result.Yaml.Should().NotContain("messages:");
        result.Yaml.Should().NotContain("params:");
    }

    [Theory]
    [InlineData("```yaml\nname: sample\nsteps:\n  - id: a\n    type: llm_call\n```", "name: sample")]
    [InlineData("name: sample\nsteps:\n  - id: a\n    type: llm_call", "name: sample")]
    public void TryExtractYamlCandidate_ShouldSupportFencedAndRawYaml(string content, string expectedPrefix)
    {
        var success = WorkflowGenerateOrchestrator.TryExtractYamlCandidate(content, out var yaml);

        success.Should().BeTrue();
        yaml.Should().StartWith(expectedPrefix);
    }

    private static WorkflowGenerateOrchestrator CreateOrchestrator()
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
}
