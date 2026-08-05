using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Bootstrap.Tests;

public sealed class AevatarAIFeaturesWorkflowToolExecutionTests
{
    [Fact]
    public async Task AddAevatarAIFeatures_ShouldExecuteWorkflowToolThroughUnifiedAdmissionPath()
    {
        var agentTool = new RecordingAgentTool("demo_tool", """{"ok":true}""");
        var admissionLedger = new RecordingStartedAdmissionLedger();
        var services = new ServiceCollection()
            .AddSingleton<IAgentToolSource>(new StaticAgentToolSource([agentTool]))
            .AddSingleton<IAgentToolAdmissionLedger>(admissionLedger);
        VoicePresenceBootstrapTests.AddToolExecutionAuditDependencies(services);
        var configuration = new ConfigurationBuilder().Build();

        services.AddAevatarAIFeatures(
            configuration,
            options => options.EnableMEAIProviders = false);

        await using var provider = services.BuildServiceProvider();
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IAgentToolExecutionPort));
        var workflowSource = provider.GetServices<IWorkflowToolSource>()
            .Should().ContainSingle().Subject;
        var tool = (await workflowSource.GetToolsAsync())
            .Should().ContainSingle().Subject;

        var result = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            ArgumentsJson: "{}",
            RunId: "run-1",
            StepId: "step-1",
            ExecutionId: "exec-1",
            CallId: "call-1",
            ScopeId: "scope-1",
            CallerCredential: new WorkflowCallerCredential()));

        tool.Name.Should().Be("demo_tool");
        result.ResultJson.Should().Be("""{"ok":true}""");
        agentTool.ExecutionCalls.Should().Be(1);
        admissionLedger.Facts.Should().ContainSingle()
            .Which.ToolName.Should().Be("demo_tool");
    }

    private sealed class StaticAgentToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(tools);
        }
    }

    private sealed class RecordingAgentTool(string name, string resultJson) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "test tool";
        public string ParametersSchema => """{"type":"object"}""";
        public int ExecutionCalls { get; private set; }

        public AgentToolReceipt? CreateSuccessReceipt(
            string callId,
            string toolName,
            string result) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.Success,
            };

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecutionCalls++;
            return Task.FromResult(resultJson);
        }
    }

    private sealed class RecordingStartedAdmissionLedger : IAgentToolAdmissionLedger
    {
        public List<AgentToolAdmissionFact> Facts { get; } = [];

        public Task<AgentToolAdmissionResult> TryStartAsync(
            AgentToolAdmissionFact fact,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Facts.Add(fact.Clone());
            return Task.FromResult(new AgentToolAdmissionResult(AgentToolAdmissionStatus.Started));
        }
    }
}
