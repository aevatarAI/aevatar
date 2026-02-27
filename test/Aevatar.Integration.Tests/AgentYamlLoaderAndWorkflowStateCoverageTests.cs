using Aevatar.Configuration;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

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
        state.SubWorkflowBindings.Add(new WorkflowState.Types.SubWorkflowBinding
        {
            WorkflowName = "sub_flow",
            ChildActorId = "actor-sub",
            Lifecycle = "singleton",
        });
        state.PendingSubWorkflowInvocations.Add(new WorkflowState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-1",
            ParentRunId = "run-parent",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            ChildActorId = "actor-sub",
            ChildRunId = "run-child",
            Lifecycle = "singleton",
        });

        var clone = state.Clone();
        clone.Should().BeEquivalentTo(state);

        var bytes = state.ToByteArray();
        WorkflowState parsed = WorkflowState.Parser.ParseFrom(bytes)
            ?? throw new InvalidOperationException("WorkflowState parse returned null");
        parsed.WorkflowName.Should().Be("demo");
        parsed.Version.Should().Be(7);
        parsed.Compiled.Should().BeTrue();
        parsed.TotalExecutions.Should().Be(10);
        parsed.SuccessfulExecutions.Should().Be(8);
        parsed.FailedExecutions.Should().Be(2);
        parsed.SubWorkflowBindings.Should().ContainSingle();
        parsed.PendingSubWorkflowInvocations.Should().ContainSingle();

        WorkflowStateReflection.Descriptor.Should().NotBeNull();
        WorkflowStateReflection.Descriptor.MessageTypes.Should().ContainSingle(x => x.Name == nameof(WorkflowState));
    }

    [Fact]
    public void WorkflowStateProto_ShouldCoverMergeNullUnknownFieldsAndValidationBranches()
    {
        // Field 1 + field 2 + unknown varint field(99)
        var raw = new byte[] { 10, 2, (byte)'y', (byte)'1', 18, 2, (byte)'w', (byte)'f', 0x98, 0x06, 0x01 };
        WorkflowState parsed = WorkflowState.Parser.ParseFrom(raw)
            ?? throw new InvalidOperationException("WorkflowState parse returned null");

        parsed.WorkflowYaml.Should().Be("y1");
        parsed.WorkflowName.Should().Be("wf");
        parsed.ToByteArray().Length.Should().BeGreaterThan(raw.Length - 2);

        parsed.Equals(parsed).Should().BeTrue();
        parsed.Equals((object?)null).Should().BeFalse();
        parsed!.GetHashCode().Should().NotBe(0);
        parsed.ToString().Should().Contain("workflowYaml");
        ((IMessage)parsed).Descriptor.Name.Should().Be(nameof(WorkflowState));

        var empty = new WorkflowState();
        empty.CalculateSize().Should().Be(0);
        empty.MergeFrom((WorkflowState)null!);
        empty.Should().BeEquivalentTo(new WorkflowState());

        var full = new WorkflowState
        {
            WorkflowYaml = "yaml",
            WorkflowName = "name",
            Version = 2,
            Compiled = true,
            CompilationError = "none",
            TotalExecutions = 3,
            SuccessfulExecutions = 2,
            FailedExecutions = 1,
        };
        full.SubWorkflowBindings.Add(new WorkflowState.Types.SubWorkflowBinding
        {
            WorkflowName = "sub_flow",
            ChildActorId = "actor-sub",
            Lifecycle = "singleton",
        });
        full.PendingSubWorkflowInvocations.Add(new WorkflowState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = "invoke-2",
            ParentRunId = "run-parent",
            ParentStepId = "step-b",
            WorkflowName = "sub_flow",
            ChildActorId = "actor-sub",
            ChildRunId = "run-child",
            Lifecycle = "singleton",
        });

        var merged = new WorkflowState();
        merged.MergeFrom(full);
        merged.Should().BeEquivalentTo(full);

        // Merge with default values should keep existing values in generated merge branches.
        merged.MergeFrom(new WorkflowState());
        merged.WorkflowName.Should().Be("name");
        merged.Version.Should().Be(2);

        Action setYamlNull = () => full.WorkflowYaml = null!;
        Action setNameNull = () => full.WorkflowName = null!;
        Action setErrorNull = () => full.CompilationError = null!;
        setYamlNull.Should().Throw<ArgumentNullException>();
        setNameNull.Should().Throw<ArgumentNullException>();
        setErrorNull.Should().Throw<ArgumentNullException>();

        // Touch timestamp dependency to ensure protobuf well-known types are linked.
        Timestamp.FromDateTime(DateTime.UtcNow).Should().NotBeNull();
    }
}
