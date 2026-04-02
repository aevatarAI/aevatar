using Aevatar.AI.Infrastructure.Local.Adapters;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.Infrastructure.Local.Tests;

public class LocalWorkflowDefinitionCommandAdapterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalWorkflowDefinitionCommandAdapter _adapter;

    public LocalWorkflowDefinitionCommandAdapterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aevatar-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _adapter = new LocalWorkflowDefinitionCommandAdapter(new AlwaysSucceedsValidator(), _tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task CreateAsync_WritesFile_WhenValid()
    {
        var yaml = "name: test-wf\nsteps:\n  - id: s1\n    action: echo";
        var result = await _adapter.CreateAsync("test-wf", yaml);

        result.Success.Should().BeTrue();
        result.Name.Should().Be("test-wf");
        result.RevisionId.Should().NotBeNullOrEmpty();
        result.Yaml.Should().Be(yaml);
        result.Diagnostics.Should().BeEmpty();

        var filePath = Path.Combine(_tempDir, "test-wf.yaml");
        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_Fails_WhenAlreadyExists()
    {
        var yaml = "name: dup-wf\nsteps:\n  - id: s1";
        await _adapter.CreateAsync("dup-wf", yaml);

        var result = await _adapter.CreateAsync("dup-wf", yaml);

        result.Success.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle()
            .Which.Message.Should().Contain("already exists");
    }

    [Fact]
    public async Task CreateAsync_Fails_WhenYamlInvalid()
    {
        var failValidator = new AlwaysFailsValidator("Step s1 is missing an action");
        var adapter = new LocalWorkflowDefinitionCommandAdapter(failValidator, _tempDir);

        var result = await adapter.CreateAsync("bad-wf", "invalid yaml content");

        result.Success.Should().BeFalse();
        result.Diagnostics.Should().NotBeEmpty();
        result.Diagnostics[0].Message.Should().Contain("missing an action");
    }

    [Fact]
    public async Task GetDefinitionAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _adapter.GetDefinitionAsync("no-such-workflow");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetDefinitionAsync_ReturnsSnapshot()
    {
        var yaml = "name: readable-wf\nsteps:\n  - id: s1";
        await _adapter.CreateAsync("readable-wf", yaml);

        var snapshot = await _adapter.GetDefinitionAsync("readable-wf");

        snapshot.Should().NotBeNull();
        snapshot!.Name.Should().Be("readable-wf");
        snapshot.Yaml.Should().Be(yaml);
        snapshot.RevisionId.Should().NotBeNullOrEmpty();
        snapshot.LastModified.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ListDefinitionsAsync_ReturnsAll()
    {
        await _adapter.CreateAsync("list-wf-one", "name: list-wf-one\nsteps:\n  - id: s1");
        await _adapter.CreateAsync("list-wf-two", "name: list-wf-two\nsteps:\n  - id: s1\n  - id: s2");

        var defs = await _adapter.ListDefinitionsAsync();

        defs.Should().HaveCount(2);
        defs.Select(d => d.Name).Should().Contain("list-wf-one");
        defs.Select(d => d.Name).Should().Contain("list-wf-two");
    }

    [Fact]
    public async Task UpdateAsync_Succeeds_WithCorrectRevision()
    {
        var yaml1 = "name: upd-wf\nsteps:\n  - id: s1";
        var createResult = await _adapter.CreateAsync("upd-wf", yaml1);
        var revision = createResult.RevisionId!;

        var yaml2 = "name: upd-wf\nsteps:\n  - id: s1\n  - id: s2";
        var updateResult = await _adapter.UpdateAsync("upd-wf", yaml2, revision);

        updateResult.Success.Should().BeTrue();
        updateResult.RevisionId.Should().NotBe(revision);
        updateResult.Yaml.Should().Be(yaml2);

        var snapshot = await _adapter.GetDefinitionAsync("upd-wf");
        snapshot!.Yaml.Should().Be(yaml2);
    }

    [Fact]
    public async Task UpdateAsync_Fails_WithWrongRevision()
    {
        var yaml = "name: rev-wf\nsteps:\n  - id: s1";
        await _adapter.CreateAsync("rev-wf", yaml);

        var result = await _adapter.UpdateAsync("rev-wf", "name: rev-wf\nsteps:\n  - id: s2", "wrong-revision");

        result.Success.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle()
            .Which.Message.Should().Contain("Revision conflict");
    }

    #region Test validators

    private sealed class AlwaysSucceedsValidator : IWorkflowYamlValidator
    {
        public WorkflowYamlValidationResult Validate(string yaml)
        {
            // Extract a name from the YAML (naive parse for testing)
            string? name = null;
            int stepCount = 0;
            foreach (var line in yaml.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("name:"))
                    name = trimmed["name:".Length..].Trim();
                if (trimmed.StartsWith("- id:"))
                    stepCount++;
            }

            return new WorkflowYamlValidationResult(
                Success: true,
                NormalizedName: name,
                NormalizedYaml: yaml,
                StepCount: stepCount,
                RoleCount: 1,
                Description: null,
                Diagnostics: []);
        }
    }

    private sealed class AlwaysFailsValidator : IWorkflowYamlValidator
    {
        private readonly string _errorMessage;

        public AlwaysFailsValidator(string errorMessage)
        {
            _errorMessage = errorMessage;
        }

        public WorkflowYamlValidationResult Validate(string yaml)
        {
            return new WorkflowYamlValidationResult(
                Success: false,
                NormalizedName: null,
                NormalizedYaml: null,
                StepCount: 0,
                RoleCount: 0,
                Description: null,
                Diagnostics: [new WorkflowYamlDiagnostic("error", _errorMessage)]);
        }
    }

    #endregion
}
