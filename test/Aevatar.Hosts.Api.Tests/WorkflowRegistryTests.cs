// ─── WorkflowRegistry 测试 ───

using Aevatar.Hosts.Api.Workflows;
using FluentAssertions;

namespace Aevatar.Hosts.Api.Tests;

public class WorkflowRegistryTests
{
    [Fact]
    public void Register_And_GetYaml()
    {
        var registry = new WorkflowRegistry();
        registry.Register("test", "name: test\nsteps: []");

        registry.GetYaml("test").Should().Contain("name: test");
        registry.GetYaml("TEST").Should().NotBeNull(); // 不区分大小写
        registry.GetYaml("nonexistent").Should().BeNull();
    }

    [Fact]
    public void GetNames_ReturnsAll()
    {
        var registry = new WorkflowRegistry();
        registry.Register("alpha", "a");
        registry.Register("beta", "b");

        registry.GetNames().Should().HaveCount(2);
    }

    [Fact]
    public void LoadFromDirectory_NonExistent_ReturnsZero()
    {
        var registry = new WorkflowRegistry();
        registry.LoadFromDirectory("/nonexistent/path/12345").Should().Be(0);
    }

    [Fact]
    public void LoadFromDirectory_LoadsYamlFiles()
    {
        // 创建临时目录
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "review.yaml"), "name: review");
            File.WriteAllText(Path.Combine(tmpDir, "chat.yml"), "name: chat");
            File.WriteAllText(Path.Combine(tmpDir, "readme.txt"), "not a workflow");

            var registry = new WorkflowRegistry();
            var count = registry.LoadFromDirectory(tmpDir);

            count.Should().Be(2);
            registry.GetYaml("review").Should().Contain("review");
            registry.GetYaml("chat").Should().Contain("chat");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }
}
