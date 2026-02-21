using Aevatar.Configuration;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Integration.Tests;

public class AgentYamlLoaderAndWorkflowStateCoverageTests
{
    [Fact]
    public void AgentYamlLoader_ShouldLoadAndDiscoverAgentsAndWorkflows()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"aevatar-agent-loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);

        var previousHome = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
        Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, tempHome);

        try
        {
            Directory.CreateDirectory(AevatarPaths.Agents);
            Directory.CreateDirectory(AevatarPaths.Workflows);

            var agentYamlPath = Path.Combine(AevatarPaths.Agents, "writer.yaml");
            var agentYmlPath = Path.Combine(AevatarPaths.Agents, "reviewer.yml");
            var workflowYamlPath = Path.Combine(AevatarPaths.Workflows, "pipeline.yaml");
            var workflowYmlPath = Path.Combine(AevatarPaths.Workflows, "maker.yml");

            File.WriteAllText(agentYamlPath, "name: writer\n");
            File.WriteAllText(agentYmlPath, "name: reviewer\n");
            File.WriteAllText(workflowYamlPath, "name: pipeline\n");
            File.WriteAllText(workflowYmlPath, "name: maker\n");

            AevatarAgentYamlLoader.LoadAgentYaml("writer").Should().Contain("writer");
            AevatarAgentYamlLoader.LoadWorkflowYaml("pipeline").Should().Contain("pipeline");
            AevatarAgentYamlLoader.LoadAgentYaml("missing").Should().BeNull();
            AevatarAgentYamlLoader.LoadWorkflowYaml("missing").Should().BeNull();

            var agents = AevatarAgentYamlLoader.DiscoverAgents();
            agents.Should().HaveCount(2);
            agents.Select(x => x.AgentId).Should().Contain(["writer", "reviewer"]);
            agents.Select(x => x.FilePath).Should().Contain([agentYamlPath, agentYmlPath]);

            var workflows = AevatarAgentYamlLoader.DiscoverWorkflows();
            workflows.Should().HaveCount(2);
            workflows.Select(x => x.WorkflowName).Should().Contain(["pipeline", "maker"]);
            workflows.Select(x => x.FilePath).Should().Contain([workflowYamlPath, workflowYmlPath]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, previousHome);
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void WorkflowStateProto_ShouldRoundtripCloneAndExposeDescriptor()
    {
        var state = new WorkflowState
        {
            WorkflowYaml = "steps:\n- id: s1",
            WorkflowName = "demo",
            Version = 7,
            Compiled = true,
            CompilationError = "",
            TotalExecutions = 10,
            SuccessfulExecutions = 8,
            FailedExecutions = 2,
        };

        var clone = state.Clone();
        clone.Should().BeEquivalentTo(state);

        var bytes = state.ToByteArray();
        var parsed = WorkflowState.Parser.ParseFrom(bytes);
        parsed.WorkflowName.Should().Be("demo");
        parsed.Version.Should().Be(7);
        parsed.Compiled.Should().BeTrue();
        parsed.TotalExecutions.Should().Be(10);
        parsed.SuccessfulExecutions.Should().Be(8);
        parsed.FailedExecutions.Should().Be(2);

        WorkflowStateReflection.Descriptor.Should().NotBeNull();
        WorkflowStateReflection.Descriptor.MessageTypes.Should().ContainSingle(x => x.Name == nameof(WorkflowState));
    }
}
