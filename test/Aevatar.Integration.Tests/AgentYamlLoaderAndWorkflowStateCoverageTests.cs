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
    public void WorkflowStateProto_ShouldRoundtripDefinitionBindingFacts()
    {
        var state = new WorkflowState
        {
            WorkflowYaml = "name: demo\nsteps:\n  - id: s1\n    type: transform",
            WorkflowName = "demo",
            Version = 7,
            Compiled = true,
            CompilationError = string.Empty,
            TotalExecutions = 10,
            SuccessfulExecutions = 8,
            FailedExecutions = 2,
        };
        state.InlineWorkflowYamls["sub_flow"] = "name: sub_flow";

        var clone = state.Clone();
        clone.Should().BeEquivalentTo(state);

        var parsed = WorkflowState.Parser.ParseFrom(state.ToByteArray());
        parsed.WorkflowName.Should().Be("demo");
        parsed.Version.Should().Be(7);
        parsed.Compiled.Should().BeTrue();
        parsed.TotalExecutions.Should().Be(10);
        parsed.SuccessfulExecutions.Should().Be(8);
        parsed.FailedExecutions.Should().Be(2);
        parsed.InlineWorkflowYamls.Should().ContainKey("sub_flow");

        WorkflowStateReflection.Descriptor.Should().NotBeNull();
        WorkflowStateReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(WorkflowState));
    }

    [Fact]
    public void WorkflowRunStateProto_ShouldRoundtripPendingRunFacts()
    {
        var state = new WorkflowRunState
        {
            WorkflowName = "demo",
            WorkflowYaml = "name: demo",
            Compiled = true,
            RunId = "run-1",
            Status = "suspended",
            ActiveStepId = "wait-signal",
        };
        state.InlineWorkflowYamls["sub_flow"] = "name: sub_flow";
        state.Variables["input"] = "hello";
        state.PendingSignalWaits["wait-signal"] = new WorkflowPendingSignalWaitState
        {
            StepId = "wait-signal",
            SignalName = "approval",
            Input = "hello",
            Prompt = "waiting",
            TimeoutMs = 3000,
            TimeoutGeneration = 2,
            WaitToken = "wait-token-1",
        };
        state.PendingHumanGates["approval-1"] = new WorkflowPendingHumanGateState
        {
            StepId = "approval-1",
            GateType = "human_approval",
            Input = "draft",
            Prompt = "approve?",
            Variable = "user_input",
            TimeoutSeconds = 60,
            OnTimeout = "fail",
            OnReject = "continue",
            ResumeToken = "resume-token-1",
        };
        state.PendingSubWorkflows["child-run-1"] = new WorkflowPendingSubWorkflowState
        {
            InvocationId = "invoke-1",
            ParentStepId = "workflow-call-1",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = "singleton",
            ChildActorId = "child-actor-1",
            ChildRunId = "child-run-1",
            ParentRunId = "run-1",
        };

        var clone = state.Clone();
        clone.Should().BeEquivalentTo(state);

        var parsed = WorkflowRunState.Parser.ParseFrom(state.ToByteArray());
        parsed.RunId.Should().Be("run-1");
        parsed.Status.Should().Be("suspended");
        parsed.PendingSignalWaits.Should().ContainKey("wait-signal");
        parsed.PendingSignalWaits["wait-signal"].WaitToken.Should().Be("wait-token-1");
        parsed.PendingHumanGates.Should().ContainKey("approval-1");
        parsed.PendingHumanGates["approval-1"].ResumeToken.Should().Be("resume-token-1");
        parsed.PendingSubWorkflows.Should().ContainKey("child-run-1");
        parsed.PendingSubWorkflows["child-run-1"].ParentRunId.Should().Be("run-1");

        WorkflowRunStateReflection.Descriptor.Should().NotBeNull();
        WorkflowRunStateReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(WorkflowRunState));
    }

    [Fact]
    public void WorkflowStateAndRunStateProto_ShouldCoverMergeNullAndDescriptorBranches()
    {
        var definitionState = new WorkflowState
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
        definitionState.InlineWorkflowYamls["inline"] = "yaml-inline";

        var mergedDefinition = new WorkflowState();
        mergedDefinition.MergeFrom(definitionState);
        mergedDefinition.Should().BeEquivalentTo(definitionState);
        mergedDefinition.MergeFrom(new WorkflowState());
        mergedDefinition.WorkflowName.Should().Be("name");

        Action setYamlNull = () => definitionState.WorkflowYaml = null!;
        Action setNameNull = () => definitionState.WorkflowName = null!;
        Action setErrorNull = () => definitionState.CompilationError = null!;
        setYamlNull.Should().Throw<ArgumentNullException>();
        setNameNull.Should().Throw<ArgumentNullException>();
        setErrorNull.Should().Throw<ArgumentNullException>();

        var runState = new WorkflowRunState
        {
            WorkflowName = "run-demo",
            WorkflowYaml = "yaml",
            RunId = "run-2",
            Status = "active",
        };
        runState.Variables["input"] = "hello";

        var mergedRun = new WorkflowRunState();
        mergedRun.MergeFrom(runState);
        mergedRun.Should().BeEquivalentTo(runState);
        mergedRun.MergeFrom(new WorkflowRunState());
        mergedRun.RunId.Should().Be("run-2");

        Action setRunIdNull = () => runState.RunId = null!;
        Action setStatusNull = () => runState.Status = null!;
        setRunIdNull.Should().Throw<ArgumentNullException>();
        setStatusNull.Should().Throw<ArgumentNullException>();

        ((IMessage)mergedDefinition).Descriptor.Name.Should().Be(nameof(WorkflowState));
        ((IMessage)mergedRun).Descriptor.Name.Should().Be(nameof(WorkflowRunState));
    }
}
