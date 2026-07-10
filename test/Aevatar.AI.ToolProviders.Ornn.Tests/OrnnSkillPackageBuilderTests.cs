using Aevatar.AI.ToolProviders.Ornn.Publishing;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnSkillPackageBuilderTests
{
    [Fact]
    public void Build_ShouldCreateDeterministicRootAndTypedSkillMarkdown()
    {
        var request = RuntimeRequest with
        {
            Tags = ["agent-tools"],
            RuntimeDependencies = ["dotnet"],
            RuntimeEnvVars = ["API_KEY"],
        };

        var (package, validation) = new OrnnSkillPackageBuilder().Build(request);

        validation.IsValid.Should().BeTrue();
        package.Should().NotBeNull();
        package!.Files.Keys.Should().Equal("runtime-skill/SKILL.md");
        package.Files["runtime-skill/SKILL.md"].Should().Contain("""
            metadata:
              category: runtime-based
              tag:
                - "agent-tools"
              output-type: text
              runtime:
                - "dotnet"
            """);
        package.Files["runtime-skill/SKILL.md"].Should().Contain("version: \"1.0\"");

        var rebuilt = new OrnnSkillPackageBuilder().Build(request).Package;
        rebuilt!.ZipBytes.Should().Equal(package.ZipBytes);
    }

    [Fact]
    public void Build_ShouldMapWorkflowScriptsReferencesAndAssetsUnderExpectedRoots()
    {
        var request = PlainRequest with
        {
            WorkflowYamls =
            [
                new OrnnSkillPublishWorkflowYaml
                {
                    WorkflowId = "approval-flow",
                    Content = "name: approval-flow\nsteps: []",
                }
            ],
            Scripts =
            [
                new OrnnSkillPublishScript
                {
                    Path = "src/Approve.cs",
                    Content = "public sealed class Approve {}",
                }
            ],
            References =
            [
                new OrnnSkillPublishFile { Path = "docs/usage.md", Content = "Use it." }
            ],
            Assets =
            [
                new OrnnSkillPublishFile { Path = "images/icon.txt", Content = "icon" }
            ],
        };

        var (package, validation) = new OrnnSkillPackageBuilder().Build(request);

        validation.IsValid.Should().BeTrue();
        package!.Files.Keys.Should().Equal(
            "plain-skill/SKILL.md",
            "plain-skill/assets/images/icon.txt",
            "plain-skill/references/docs/usage.md",
            "plain-skill/scripts/src/Approve.cs",
            "plain-skill/workflows/approval-flow.yaml");
    }

    [Theory]
    [InlineData("foo.yaml")]
    [InlineData("config/foo.yml")]
    [InlineData("deep/config/foo.YAML")]
    public void Build_ShouldRejectOrdinaryAssetYamlAtAnyDepth(string path)
    {
        var request = PlainRequest with
        {
            Assets = [new OrnnSkillPublishFile { Path = path, Content = "name: no" }],
        };

        var (package, validation) = new OrnnSkillPackageBuilder().Build(request);

        package.Should().BeNull();
        validation.Diagnostics.Should().Contain(x =>
            x.Code == "invalid_path" &&
            x.Message.Contains("ordinary assets", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("assets/already-rooted.txt")]
    [InlineData("README.md")]
    [InlineData("nested/SKILL.md")]
    [InlineData("workflows/flow.txt")]
    public void Build_ShouldRejectReservedOrEscapingAssetPaths(string path)
    {
        var request = PlainRequest with
        {
            Assets = [new OrnnSkillPublishFile { Path = path, Content = "bad" }],
        };

        var (package, validation) = new OrnnSkillPackageBuilder().Build(request);

        package.Should().BeNull();
        validation.Diagnostics.Should().Contain(x => x.Code == "invalid_path");
    }

    [Fact]
    public void Build_ShouldRejectDuplicateNormalizedPackagePaths()
    {
        var request = PlainRequest with
        {
            Assets =
            [
                new OrnnSkillPublishFile { Path = "docs/readme.txt", Content = "one" },
                new OrnnSkillPublishFile { Path = "docs/readme.txt", Content = "two" },
            ],
        };

        var (package, validation) = new OrnnSkillPackageBuilder().Build(request);

        package.Should().BeNull();
        validation.Diagnostics.Should().Contain(x => x.Code == "duplicate_path");
    }

    private static OrnnSkillPublishRequest PlainRequest => new()
    {
        Name = "plain-skill",
        Description = "Plain skill",
        Version = "1.0",
        Category = "plain",
        InstructionsMarkdown = "Do the work.",
    };

    private static OrnnSkillPublishRequest RuntimeRequest => new()
    {
        Name = "runtime-skill",
        Description = "Runtime skill",
        Version = "1.0",
        Category = "runtime-based",
        InstructionsMarkdown = "Run it.",
        OutputType = "text",
        Runtimes = ["dotnet"],
    };
}
