using Aevatar.AI.Abstractions;
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
        var adapter = new AgentWorkflowToolSourceAdapter([source], new PassThroughExecutionPort());
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

    private sealed class PassThroughExecutionPort : IAgentToolExecutionPort
    {
        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            string resultJson;
            using (AgentToolContextScope.Push(request.ExecutionContext))
                resultJson = await request.Tool.ExecuteAsync(request.ArgumentsJson, ct);
            return new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                resultJson,
                new AgentToolReceipt
                {
                    CallId = request.ExecutionContext.Request.CallId ?? string.Empty,
                    ToolName = request.Tool.Name,
                    Status = AgentToolReceiptStatus.Success,
                    ResultJson = resultJson,
                },
                IsMutation: false,
                FailureCode: string.Empty,
                SafeMessage: string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: true,
                Retryable: false,
                AuditCompleted: true);
        }
    }
}
