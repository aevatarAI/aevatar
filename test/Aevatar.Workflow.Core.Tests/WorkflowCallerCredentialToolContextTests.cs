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
                Kind = NyxIdCallerCredentialKind.SourceReadableUserBearer,
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
        tool.NyxIdAccessToken.Should().Be("token-alpha");
        tool.NyxIdOrgToken.Should().Be("token-alpha");
        tool.SenderNyxIdAccessToken.Should().Be("token-alpha");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerCredentialIsProxyDelegation_ShouldPreserveCredentialKind()
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
                BearerToken = "delegation-alpha",
                Kind = NyxIdCallerCredentialKind.ProxyDelegation,
            }));

        tool.NyxIdAccessToken.Should().Be("delegation-alpha");
        tool.NyxIdOrgToken.Should().BeNull();
        tool.SenderNyxIdAccessToken.Should().BeNull();
        tool.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.ProxyDelegation);
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

        public AgentToolNyxIdCredentialKind NyxIdCredentialKind { get; private set; }

        public string? NyxIdAccessToken { get; private set; }

        public string? NyxIdOrgToken { get; private set; }

        public string? SenderNyxIdAccessToken { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            var senderBinding = AgentToolRequestContext.Current?.SenderBinding;
            BindingId = senderBinding?.BindingId;
            NyxUserId = senderBinding?.NyxUserId;
            SenderTenant = senderBinding?.SenderTenant;
            NyxIdAccessToken = AgentToolRequestContext.NyxIdAccessToken;
            NyxIdOrgToken = AgentToolRequestContext.NyxIdOrgToken;
            SenderNyxIdAccessToken = AgentToolRequestContext.SenderNyxIdAccessToken;
            NyxIdCredentialKind = AgentToolRequestContext.NyxIdCredentialKind;
            return Task.FromResult("{}");
        }
    }
}
