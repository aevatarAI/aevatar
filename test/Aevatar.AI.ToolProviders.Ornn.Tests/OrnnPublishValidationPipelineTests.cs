using Aevatar.AI.ToolProviders.Ornn.Publishing;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnPublishValidationPipelineTests
{
    [Fact]
    public async Task ValidateAsync_ShouldPassPlainPackageWithoutExecutableValidators()
    {
        var result = await new OrnnSkillPublishValidationPipeline().ValidateAsync(PlainRequest);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_ShouldFailClosedWhenWorkflowValidatorIsMissing()
    {
        var request = PlainRequest with
        {
            WorkflowYamls =
            [
                new OrnnSkillPublishWorkflowYaml { WorkflowId = "flow", Content = "name: flow\nsteps: []" }
            ],
        };

        var result = await new OrnnSkillPublishValidationPipeline().ValidateAsync(request);

        result.Diagnostics.Should().Contain(x =>
            x.Code == "missing_asset_validator" &&
            x.Message.Contains("workflow_yaml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateAsync_ShouldFailClosedWhenScriptValidatorIsMissing()
    {
        var request = PlainRequest with
        {
            Scripts = [new OrnnSkillPublishScript { Path = "main.cs", Content = "class C {}" }],
        };

        var result = await new OrnnSkillPublishValidationPipeline().ValidateAsync(request);

        result.Diagnostics.Should().Contain(x =>
            x.Code == "missing_asset_validator" &&
            x.Message.Contains("script", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateAsync_ShouldAggregateValidatorDiagnostics()
    {
        var request = PlainRequest with
        {
            WorkflowYamls =
            [
                new OrnnSkillPublishWorkflowYaml { WorkflowId = "flow", Content = "name: flow\nsteps: []" }
            ],
            Scripts = [new OrnnSkillPublishScript { Path = "main.cs", Content = "class C {}" }],
        };
        var pipeline = new OrnnSkillPublishValidationPipeline(
        [
            new StubValidator(OrnnSkillPublishValidationPipeline.WorkflowYamlAssetKind, "workflow_error"),
            new StubValidator(OrnnSkillPublishValidationPipeline.ScriptAssetKind, "script_error"),
        ]);

        var result = await pipeline.ValidateAsync(request);

        result.Diagnostics.Select(x => x.Code).Should().Equal("workflow_error", "script_error");
    }

    private static OrnnSkillPublishRequest PlainRequest => new()
    {
        Name = "plain-skill",
        Description = "Plain skill",
        Version = "1.0",
        Category = "plain",
        InstructionsMarkdown = "Do the work.",
    };

    private sealed class StubValidator(string assetKind, string code) : IOrnnSkillPublishAssetValidator
    {
        public string AssetKind { get; } = assetKind;

        public Task<IReadOnlyList<OrnnSkillPublishDiagnostic>> ValidateAsync(
            OrnnSkillPublishRequest request,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrnnSkillPublishDiagnostic>>(
            [
                new OrnnSkillPublishDiagnostic(code, code)
            ]);
    }
}
