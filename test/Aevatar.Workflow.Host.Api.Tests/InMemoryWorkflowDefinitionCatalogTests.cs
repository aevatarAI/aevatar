// ─── InMemoryWorkflowDefinitionCatalog 测试 ───

using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Infrastructure.Workflows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Host.Api.Tests;

public class InMemoryWorkflowDefinitionCatalogTests
{
    [Fact]
    public async Task Register_And_GetYaml()
    {
        var registry = new InMemoryWorkflowDefinitionCatalog();
        registry.Upsert("test", "name: test\nsteps: []");

        (await registry.GetYamlAsync("test")).Should().Contain("name: test");
        (await registry.GetYamlAsync("TEST")).Should().NotBeNull(); // 不区分大小写
        (await registry.GetYamlAsync("nonexistent")).Should().BeNull();
    }

    [Fact]
    public async Task GetNames_ReturnsAll()
    {
        var registry = new InMemoryWorkflowDefinitionCatalog();
        registry.Upsert("alpha", "a");
        registry.Upsert("beta", "b");

        (await registry.GetNamesAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task FileLoader_NonExistentDirectory_ReturnsZero()
    {
        var registry = new InMemoryWorkflowDefinitionCatalog();
        var loader = new WorkflowDefinitionFileLoader();
        var loaded = await loader.LoadIntoAsync(
            registry,
            ["/nonexistent/path/12345"],
            NullLogger.Instance);

        loaded.Should().Be(0);
    }

    [Fact]
    public async Task FileLoader_LoadsYamlFiles()
    {
        // 创建临时目录
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "review.yaml"), "name: review");
            File.WriteAllText(Path.Combine(tmpDir, "chat.yml"), "name: chat");
            File.WriteAllText(Path.Combine(tmpDir, "readme.txt"), "not a workflow");

            var registry = new InMemoryWorkflowDefinitionCatalog();
            var loader = new WorkflowDefinitionFileLoader();
            var count = await loader.LoadIntoAsync(registry, [tmpDir], NullLogger.Instance);

            count.Should().Be(2);
            (await registry.GetYamlAsync("review")).Should().Contain("review");
            (await registry.GetYamlAsync("chat")).Should().Contain("chat");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task FileLoader_DuplicateWorkflowName_ShouldThrow()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_dup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "review.yaml"), "name: review");
            File.WriteAllText(Path.Combine(tmpDir, "review.yml"), "name: review_2");

            var registry = new InMemoryWorkflowDefinitionCatalog();
            var loader = new WorkflowDefinitionFileLoader();

            var act = () => loader.LoadIntoAsync(registry, [tmpDir], NullLogger.Instance);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Duplicate workflow definition name*");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task FileLoader_DuplicateWorkflowName_WithOverridePolicy_ShouldUseFileVersion()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_dup_override_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "direct.yaml"), "name: direct\nsteps:\n  - id: from_file\n");

            var registry = new InMemoryWorkflowDefinitionCatalog();
            registry.Upsert("direct", "name: direct\nsteps:\n  - id: built_in\n");
            var loader = new WorkflowDefinitionFileLoader();

            var count = await loader.LoadIntoAsync(
                registry,
                [tmpDir],
                NullLogger.Instance,
                seedSources: null,
                duplicatePolicy: WorkflowDefinitionDuplicatePolicy.Override);

            count.Should().Be(1);
            (await registry.GetYamlAsync("direct")).Should().Contain("from_file");
            (await registry.GetYamlAsync("direct")).Should().NotContain("built_in");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task FileLoader_DuplicateDirectoryEntries_ShouldLoadOnlyOnce()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_dup_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "brainstorm.yaml"), "name: brainstorm");
            var equivalentPath = Path.Combine(tmpDir, ".");

            var registry = new InMemoryWorkflowDefinitionCatalog();
            var loader = new WorkflowDefinitionFileLoader();

            var count = await loader.LoadIntoAsync(registry, [tmpDir, equivalentPath], NullLogger.Instance);

            count.Should().Be(1);
            (await registry.GetNamesAsync()).Should().ContainSingle().Which.Should().Be("brainstorm");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }
}
