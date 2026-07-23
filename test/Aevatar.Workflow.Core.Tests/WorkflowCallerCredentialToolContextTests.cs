using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowCallerCredentialToolContextTests
{
    [Fact]
    public async Task ExecuteAsync_WhenCallerAuthorityHasBindingId_ShouldExposeSenderBindingContext()
    {
        var tool = new RecordingAgentTool();
        var source = new SingleToolSource(tool);
        var adapter = new AgentWorkflowToolSourceAdapter([source]);
        var workflowTool = (await adapter.GetToolsAsync()).Should().ContainSingle().Subject;

        await workflowTool.ExecuteAsync(new WorkflowToolExecutionRequest(
            "{}",
            "run-alpha",
            "step-alpha",
            "execution-alpha",
            "call-alpha",
            "scope-alpha",
            new WorkflowCallerCredential
            {
                BearerToken = "token-alpha",
                NyxIdAuthority = new WorkflowCallerNyxIdAuthority
                {
                    Platform = " lark ",
                    Tenant = " tenant-alpha ",
                    ExternalUserId = " sender-alpha ",
                    Scope = "proxy",
                    BindingId = " bnd-owner-alpha ",
                },
            }));

        tool.BindingId.Should().Be("bnd-owner-alpha");
        tool.NyxUserId.Should().Be("sender-alpha");
        tool.SenderTenant.Should().Be("tenant-alpha");
    }

    private sealed class SingleToolSource(IAgentTool tool) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
    }

    private sealed class RecordingAgentTool : IAgentTool
    {
        public string Name => "record_context";

        public string Description => "Records tool context.";

        public string ParametersSchema => "{}";

        public string? BindingId { get; private set; }

        public string? NyxUserId { get; private set; }

        public string? SenderTenant { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            var senderBinding = AgentToolRequestContext.Current?.SenderBinding;
            BindingId = senderBinding?.BindingId;
            NyxUserId = senderBinding?.NyxUserId;
            SenderTenant = senderBinding?.SenderTenant;
            return Task.FromResult("{}");
        }
    }
}
