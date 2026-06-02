using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;
using System.Text.Json;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class SkillWorkflowsWiringTests
{
    [Fact]
    public async Task OrnnRemoteSkillFetcher_ExtractsWorkflowsAndStripsThemFromAssociatedFiles()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "Translator",
                "description": "Translates",
                "files": {
                  "SKILL.md": "# Translator\nUse this.",
                  "workflows/translate.yaml": "name: translate_flow\ndescription: Translate text\nwhen_to_use: When user asks to translate\nsteps:\n  - id: do\n    type: llm_call\n",
                  "scripts/run.sh": "echo hi"
                }
              }
            }
            """);
        var client = CreateClient(handler);
        var fetcher = new OrnnRemoteSkillFetcher(client);

        var skill = await fetcher.FetchSkillAsync("token", "Translator");

        skill.Should().NotBeNull();
        skill!.Workflows.Should().ContainSingle();
        skill.Workflows![0].WorkflowId.Should().Be("translate");
        skill.Workflows[0].WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("name: translate_flow");

        // Workflow file must not also appear in AssociatedFiles.
        skill.AssociatedFiles.Should().NotBeNull();
        skill.AssociatedFiles.Should().NotContainKey("workflows/translate.yaml");
        skill.AssociatedFiles.Should().ContainKey("scripts/run.sh");
    }

    [Fact]
    public async Task OrnnRemoteSkillFetcher_FallsBackToAssetsYamlWhenNoWorkflowsDir()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "Translator",
                "files": {
                  "SKILL.md": "# Translator",
                  "assets/translate.yaml": "name: translate_asset\nsteps:\n  - id: do\n",
                  "assets/prompt.txt": "raw"
                }
              }
            }
            """);
        var client = CreateClient(handler);
        var fetcher = new OrnnRemoteSkillFetcher(client);

        var skill = await fetcher.FetchSkillAsync("token", "Translator");

        skill!.Workflows.Should().ContainSingle(w => w.WorkflowId == "translate_asset");
        skill.Workflows![0].WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("name: translate_asset");
        skill.AssociatedFiles.Should().NotContainKey("assets/translate.yaml");
        skill.AssociatedFiles.Should().ContainKey("assets/prompt.txt");
    }

    [Fact]
    public async Task UseSkillTool_RendersWorkflowsSectionWithStartWorkflowInstructions()
    {
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "translator",
            Description = "Translates text",
            Instructions = "Follow these steps.",
            Source = SkillSource.Local,
            Workflows =
            [
                new SkillWorkflowDescriptor
                {
                    WorkflowId = "translate_flow",
                    WorkflowYamls =
                    [
                        "name: translate_flow\nsteps:\n  - id: do\n    type: llm_call\n",
                    ],
                },
            ],
        });

        var tool = new UseSkillTool(catalog);
        var output = await tool.ExecuteAsync("""{"skill":"translator"}""");
        var text = ExtractText(output);

        text.Should().Contain("## aevatar_start_workflow Handoff");
        text.Should().Contain("aevatar_start_workflow");
        text.Should().Contain("workflow_yamls");
        text.Should().Contain("translate_flow");
        text.Should().Contain("```json");
        text.Should().Contain("type: llm_call");
    }

    [Fact]
    public async Task UseSkillTool_OmitsWorkflowsSectionWhenSkillHasNoWorkflows()
    {
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "plain",
            Description = "no workflows",
            Instructions = "body",
            Source = SkillSource.Local,
        });

        var tool = new UseSkillTool(catalog);
        var output = await tool.ExecuteAsync("""{"skill":"plain"}""");
        var text = ExtractText(output);

        text.Should().NotContain("## aevatar_start_workflow Handoff");
        text.Should().NotContain("aevatar_start_workflow");
    }

    [Fact]
    public void SkillDiscovery_PicksUpWorkflowsFromSkillDirectory()
    {
        using var tempDir = new TempDirectory();
        var skillDir = Path.Combine(tempDir.Path, "translator");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: translator
            description: Translates
            ---
            Body
            """);
        var workflowsDir = Path.Combine(skillDir, "workflows");
        Directory.CreateDirectory(workflowsDir);
        File.WriteAllText(Path.Combine(workflowsDir, "translate.yaml"), """
            name: translate_flow
            description: Run translation
            when_to_use: User asks to translate
            steps:
              - id: do
                type: llm_call
            """);

        var skills = new SkillDiscovery().ScanDirectory(tempDir.Path);

        skills.Should().ContainSingle();
        skills[0].Name.Should().Be("translator");
        skills[0].Workflows.Should().ContainSingle();
        skills[0].Workflows![0].WorkflowId.Should().Be("translate_flow");
        skills[0].Workflows![0].WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("name: translate_flow");
    }

    private static OrnnSkillClient CreateClient(OrnnTestHttpMessageHandler handler)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        var options = new OrnnOptions { NyxIdSlug = "ornn" };
        return new OrnnSkillClient(options, nyxClient);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private static string ExtractText(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("text").GetString() ?? string.Empty;
    }
}
