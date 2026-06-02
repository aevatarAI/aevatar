using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class SkillWorkflowExtractorTests
{
    [Fact]
    public void ExtractFromFiles_PrefersWorkflowsDirectory()
    {
        var files = new Dictionary<string, string>
        {
            ["workflows/qa.yaml"] = """
                name: qa_flow
                description: Answers a question
                when_to_use: When user asks a factual question
                steps:
                  - id: answer
                    type: llm_call
                """,
            ["assets/notes.yaml"] = """
                name: misc
                steps:
                  - id: ignored
                """,
        };

        var result = new SkillWorkflowExtractor().ExtractFromFiles(files);

        result.Workflows.Should().ContainSingle();
        var workflow = result.Workflows[0];
        workflow.WorkflowId.Should().Be("qa_flow");
        workflow.WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("steps:");
        result.RemainingFiles.Should().ContainKey("assets/notes.yaml");
    }

    [Fact]
    public void ExtractFromFiles_FallsBackToAssetsWhenWorkflowsAbsent()
    {
        var files = new Dictionary<string, string>
        {
            ["assets/qa.yaml"] = """
                name: qa_flow
                steps:
                  - id: answer
                """,
            ["assets/prompt.txt"] = "not a workflow",
        };

        var result = new SkillWorkflowExtractor().ExtractFromFiles(files);

        result.Workflows.Should().ContainSingle(w => w.WorkflowId == "qa_flow");
        result.Workflows[0].WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("name: qa_flow");
        result.RemainingFiles.Should().ContainKey("assets/prompt.txt");
        result.RemainingFiles!.ContainsKey("assets/qa.yaml").Should().BeFalse();
    }

    [Fact]
    public void ExtractFromFiles_SkipsAssetsYamlWithoutWorkflowShape()
    {
        var files = new Dictionary<string, string>
        {
            ["assets/no_steps.yaml"] = """
                name: just_metadata
                description: not a workflow
                """,
            ["assets/no_name.yaml"] = """
                description: missing name
                steps:
                  - id: x
                """,
        };

        var result = new SkillWorkflowExtractor().ExtractFromFiles(files);

        result.Workflows.Should().BeEmpty();
        result.RemainingFiles.Should().ContainKey("assets/no_steps.yaml");
        result.RemainingFiles.Should().ContainKey("assets/no_name.yaml");
    }

    [Fact]
    public void ExtractFromFiles_WorkflowsDirectoryDoesNotRequireSteps()
    {
        // Lenient inside workflows/: if user puts it there, trust the labelling.
        var files = new Dictionary<string, string>
        {
            ["workflows/draft.yaml"] = """
                name: draft_workflow
                description: still being authored
                """,
        };

        var result = new SkillWorkflowExtractor().ExtractFromFiles(files);

        result.Workflows.Should().ContainSingle(w => w.WorkflowId == "draft_workflow");
    }

    [Fact]
    public void ExtractFromFiles_IgnoresNonYamlInWorkflowsDirectory()
    {
        var files = new Dictionary<string, string>
        {
            ["workflows/README.md"] = "docs only",
            ["workflows/qa.yml"] = """
                name: qa
                steps:
                  - id: answer
                """,
        };

        var result = new SkillWorkflowExtractor().ExtractFromFiles(files);

        result.Workflows.Should().ContainSingle(w => w.WorkflowId == "qa");
        result.RemainingFiles.Should().ContainKey("workflows/README.md");
    }

    [Fact]
    public void ExtractFromFiles_EmptyOrNullInputReturnsEmpty()
    {
        var extractor = new SkillWorkflowExtractor();
        extractor.ExtractFromFiles(null).Workflows.Should().BeEmpty();
        extractor.ExtractFromFiles(new Dictionary<string, string>()).Workflows.Should().BeEmpty();
    }

    [Fact]
    public void ExtractFromDirectory_ReadsWorkflowsSubdir()
    {
        using var tempDir = new TempDirectory();
        var workflowsDir = Path.Combine(tempDir.Path, "workflows");
        Directory.CreateDirectory(workflowsDir);
        File.WriteAllText(
            Path.Combine(workflowsDir, "qa.yaml"),
            """
            name: qa_local
            steps:
              - id: answer
                type: llm_call
            """);

        var workflows = new SkillWorkflowExtractor().ExtractFromDirectory(tempDir.Path);

        workflows.Should().ContainSingle(w => w.WorkflowId == "qa_local");
        workflows[0].WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("name: qa_local");
    }

    [Fact]
    public void ExtractFromDirectory_FallsBackToAssetsWithWorkflowShape()
    {
        using var tempDir = new TempDirectory();
        var assetsDir = Path.Combine(tempDir.Path, "assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(
            Path.Combine(assetsDir, "qa.yaml"),
            """
            name: qa_asset
            steps:
              - id: answer
            """);
        File.WriteAllText(
            Path.Combine(assetsDir, "prompts.yaml"),
            "greeting: hello\n");

        var workflows = new SkillWorkflowExtractor().ExtractFromDirectory(tempDir.Path);

        workflows.Should().ContainSingle(w => w.WorkflowId == "qa_asset");
        workflows[0].WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("name: qa_asset");
    }

    [Fact]
    public void ExtractFromDirectory_ReturnsEmptyWhenNeitherDirectoryExists()
    {
        using var tempDir = new TempDirectory();
        new SkillWorkflowExtractor().ExtractFromDirectory(tempDir.Path).Should().BeEmpty();
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
}
