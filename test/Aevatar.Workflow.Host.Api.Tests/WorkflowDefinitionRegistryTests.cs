// ─── WorkflowDefinitionRegistry 测试 ───

using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Infrastructure.Workflows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowDefinitionRegistryTests
{
    [Fact]
    public void Register_And_GetYaml()
    {
        var registry = new WorkflowDefinitionRegistry();
        registry.Register("test", "name: test\nsteps: []");

        registry.GetYaml("test").Should().Contain("name: test");
        registry.GetYaml("TEST").Should().NotBeNull(); // 不区分大小写
        registry.GetYaml("nonexistent").Should().BeNull();
        registry.GetDefinition("test")!.DefinitionActorId.Should().Be(WorkflowDefinitionActorId.Format("test"));
    }

    [Fact]
    public void GetNames_ReturnsAll()
    {
        var registry = new WorkflowDefinitionRegistry();
        registry.Register("alpha", "a");
        registry.Register("beta", "b");

        registry.GetNames().Should().HaveCount(2);
    }

    [Fact]
    public void FileLoader_NonExistentDirectory_ReturnsZero()
    {
        var registry = new WorkflowDefinitionRegistry();
        var loader = new WorkflowDefinitionFileLoader();
        var loaded = loader.LoadInto(
            registry,
            ["/nonexistent/path/12345"],
            NullLogger.Instance);

        loaded.Should().Be(0);
    }

    [Fact]
    public void FileLoader_LoadsYamlFiles()
    {
        // 创建临时目录
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "review.yaml"), "name: review");
            File.WriteAllText(Path.Combine(tmpDir, "chat.yml"), "name: chat");
            File.WriteAllText(Path.Combine(tmpDir, "readme.txt"), "not a workflow");

            var registry = new WorkflowDefinitionRegistry();
            var loader = new WorkflowDefinitionFileLoader();
            var count = loader.LoadInto(registry, [tmpDir], NullLogger.Instance);

            count.Should().Be(2);
            registry.GetYaml("review").Should().Contain("review");
            registry.GetYaml("chat").Should().Contain("chat");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void FileLoader_DuplicateWorkflowName_ShouldThrow()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_dup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "review.yaml"), "name: review");
            File.WriteAllText(Path.Combine(tmpDir, "review.yml"), "name: review_2");

            var registry = new WorkflowDefinitionRegistry();
            var loader = new WorkflowDefinitionFileLoader();

            Action act = () => loader.LoadInto(registry, [tmpDir], NullLogger.Instance);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Duplicate workflow definition name*");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void FileLoader_DuplicateWorkflowName_WithOverridePolicy_ShouldUseFileVersion()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_dup_override_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "direct.yaml"), "name: direct\nsteps:\n  - id: from_file\n");

            var registry = new WorkflowDefinitionRegistry();
            registry.Register("direct", "name: direct\nsteps:\n  - id: built_in\n");
            var loader = new WorkflowDefinitionFileLoader();

            var count = loader.LoadInto(
                registry,
                [tmpDir],
                NullLogger.Instance,
                WorkflowDefinitionDuplicatePolicy.Override);

            count.Should().Be(1);
            registry.GetYaml("direct").Should().Contain("from_file");
            registry.GetYaml("direct").Should().NotContain("built_in");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void FileLoader_DuplicateDirectoryEntries_ShouldLoadOnlyOnce()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_dup_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "brainstorm.yaml"), "name: brainstorm");
            var equivalentPath = Path.Combine(tmpDir, ".");

            var registry = new WorkflowDefinitionRegistry();
            var loader = new WorkflowDefinitionFileLoader();

            var count = loader.LoadInto(registry, [tmpDir, equivalentPath], NullLogger.Instance);

            count.Should().Be(1);
            registry.GetNames().Should().ContainSingle().Which.Should().Be("brainstorm");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void BuiltInAutoYaml_ShouldContainFewShotWorkflowExamples()
    {
        WorkflowDefinitionRegistry.BuiltInAutoYaml.Should().Contain("Example valid workflow YAML (simple deterministic flow):");
        WorkflowDefinitionRegistry.BuiltInAutoYaml.Should().Contain("name: normalize_text");
        WorkflowDefinitionRegistry.BuiltInAutoYaml.Should().Contain("name: review_summary");
        WorkflowDefinitionRegistry.BuiltInAutoYaml.Should().Contain("Do not invent extra fields.");
    }

    [Fact]
    public void BuiltInAutoYaml_ShouldUseRouteTokenSwitchWithoutSubstringHeuristics()
    {
        WorkflowDefinitionRegistry.BuiltInAutoYaml.Should().Contain("id: classify_route");
        WorkflowDefinitionRegistry.BuiltInAutoYaml.Should().Contain("id: route_intent");
        WorkflowDefinitionRegistry.BuiltInAutoYaml.Should().Contain("type: switch");
        WorkflowDefinitionRegistry.BuiltInAutoYaml.Should().Contain("\"workflow\": prepare_workflow_request");
        WorkflowDefinitionRegistry.BuiltInAutoYaml.Should().Contain("\"direct\": prepare_direct_response");
        WorkflowDefinitionRegistry.BuiltInAutoYaml.Should().NotContain("condition: \"```y\"");
    }

    [Fact]
    public void CreateBuiltInAutoYaml_ShouldKeepRouteTokenFlow()
    {
        var autoYaml = WorkflowDefinitionRegistry.CreateBuiltInAutoYaml();

        autoYaml.Should().Contain("id: classify_route");
        autoYaml.Should().Contain("next: route_intent");
        autoYaml.Should().NotContain("condition: \"```y\"");
    }
}
