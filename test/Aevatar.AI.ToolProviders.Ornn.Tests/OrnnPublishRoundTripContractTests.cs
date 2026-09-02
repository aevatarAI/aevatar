using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnPublishRoundTripContractTests
{
    [Fact]
    public async Task PublishedWorkflowYaml_ShouldRoundTripThroughWorkflowsRootAsWorkflowDescriptor()
    {
        var workflowYaml = "name: approval-flow\nsteps: []";
        var request = new OrnnSkillPublishRequest
        {
            Name = "approval-skill",
            Description = "Approval skill",
            Version = "1.0",
            Category = "plain",
            InstructionsMarkdown = "Run approval.",
            WorkflowYamls =
            [
                new OrnnSkillPublishWorkflowYaml
                {
                    WorkflowId = "approval-flow",
                    Content = workflowYaml,
                }
            ],
        };
        var built = new OrnnSkillPackageBuilder().Build(request).Package!;
        built.Files.Should().ContainKey("approval-skill/workflows/approval-flow.yaml");
        built.Files.Should().NotContainKey("approval-skill/assets/approval-flow.yaml");

        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "approval-skill",
                "description": "Approval skill",
                "files": {
                  "SKILL.md": "---\nname: approval-skill\n---\nRun approval.",
                  "workflows/approval-flow.yaml": "name: approval-flow\nsteps: []",
                  "assets/readme.txt": "ordinary asset"
                }
              }
            }
            """);
        var fetcher = new OrnnRemoteSkillFetcher(CreateClient(handler));

        var skill = await fetcher.FetchSkillAsync("token", "approval-skill");

        skill.Should().NotBeNull();
        var workflow = skill!.Workflows.Should().ContainSingle().Subject;
        workflow.WorkflowId.Should().Be("approval-flow");
        workflow.WorkflowYamls.Should().Equal(workflowYaml);
        skill.AssociatedFiles.Should().ContainKey("assets/readme.txt");
        skill.AssociatedFiles.Should().NotContainKey("workflows/approval-flow.yaml");
    }

    [Fact]
    public async Task LegacyAssetWorkflowYaml_ShouldRemainReadCompatibleWithoutBeingPublishPath()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "legacy-approval-skill",
                "description": "Approval skill",
                "files": {
                  "SKILL.md": "---\nname: legacy-approval-skill\n---\nRun approval.",
                  "assets/approval-flow.yaml": "name: approval-flow\nsteps: []",
                  "assets/readme.txt": "ordinary asset"
                }
              }
            }
            """);
        var fetcher = new OrnnRemoteSkillFetcher(CreateClient(handler));

        var skill = await fetcher.FetchSkillAsync("token", "legacy-approval-skill");

        skill.Should().NotBeNull();
        var workflow = skill!.Workflows.Should().ContainSingle().Subject;
        workflow.WorkflowId.Should().Be("approval-flow");
        workflow.WorkflowYamls.Should().Equal("name: approval-flow\nsteps: []");
        skill.AssociatedFiles.Should().ContainKey("assets/readme.txt");
        skill.AssociatedFiles.Should().NotContainKey("assets/approval-flow.yaml");
    }

    [Fact]
    public void OrdinaryAssetYaml_ShouldRemainImpossibleInPublishV1()
    {
        var request = new OrnnSkillPublishRequest
        {
            Name = "approval-skill",
            Description = "Approval skill",
            Version = "1.0",
            Category = "plain",
            InstructionsMarkdown = "Run approval.",
            Assets =
            [
                new OrnnSkillPublishFile
                {
                    Path = "approval-flow.yaml",
                    Content = "name: approval-flow\nsteps: []",
                }
            ],
        };

        var result = new OrnnSkillPackageBuilder().Build(request);

        result.Package.Should().BeNull();
        result.Validation.Diagnostics.Should().Contain(x => x.Code == "invalid_path");
    }

    private static OrnnSkillClient CreateClient(OrnnTestHttpMessageHandler handler)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        return new OrnnSkillClient(new OrnnOptions { NyxIdSlug = "ornn" }, nyxClient);
    }
}
