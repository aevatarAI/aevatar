using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;
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
        tool.OwnerSubject.Should().Be("sender-alpha");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerCredentialIsProxyDelegation_ShouldPreserveCredentialKind()
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
                BearerToken = "delegation-alpha",
                Kind = NyxIdCallerCredentialKind.ProxyDelegation,
            }));

        tool.NyxIdAccessToken.Should().Be("delegation-alpha");
        tool.NyxIdOrgToken.Should().BeNull();
        tool.SenderNyxIdAccessToken.Should().BeNull();
        tool.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.ProxyDelegation);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerCredentialIsAgentKey_ShouldPreserveCredentialKind()
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
                BearerToken = "nyxid_ag_interactive_runtime",
                Kind = NyxIdCallerCredentialKind.AgentKey,
            }));

        tool.NyxIdAccessToken.Should().Be("nyxid_ag_interactive_runtime");
        tool.NyxIdOrgToken.Should().BeNull();
        tool.SenderNyxIdAccessToken.Should().BeNull();
        tool.SourceReadableNyxIdAccessToken.Should().BeNull();
        tool.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.AgentKey);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerHasDelegationAndSourceBearer_ShouldMapEachCredentialByPurpose()
    {
        var tool = new RecordingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleToolSource(tool)],
            new PassThroughExecutionPort());
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
                SourceReadableUserBearerToken = "source-alpha",
            }));

        tool.NyxIdAccessToken.Should().Be("delegation-alpha");
        tool.NyxIdOrgToken.Should().BeNull();
        tool.SenderNyxIdAccessToken.Should().BeNull();
        tool.SourceReadableNyxIdAccessToken.Should().Be("source-alpha");
        tool.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.ProxyDelegation);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerUsesChannelAgentKey_ShouldPreserveDurableHandleForNestedCalls()
    {
        var tool = new RecordingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleToolSource(tool)],
            new PassThroughExecutionPort());
        var workflowTool = (await adapter.GetToolsAsync()).Should().ContainSingle().Subject;
        var descriptor = new SecretReference
        {
            Ref = "sec-channel-agent-key",
            Purpose = CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            OwnerScopeKey = "scope-channel",
            Fingerprint = "fingerprint-channel",
            Version = 1,
            CreatedAtUnixMs = 1_700_000_000_000,
        };

        await workflowTool.ExecuteAsync(new WorkflowToolExecutionRequest(
            "{}",
            "run-alpha",
            "step-alpha",
            "execution-alpha",
            "call-alpha",
            "scope-alpha",
            new WorkflowCallerCredential
            {
                BearerToken = "channel-agent-key",
                Kind = NyxIdCallerCredentialKind.ProxyDelegation,
                DurableCallerCredential = new DurableCallerCredentialRef
                {
                    Ref = descriptor.Ref,
                    Purpose = descriptor.Purpose,
                    OwnerScopeKey = descriptor.OwnerScopeKey,
                    SubjectId = "key-channel",
                    SourceKind = DurableCallerCredentialSourceKind.ChannelRegistration,
                    SecretReference = descriptor.Clone(),
                },
            }));

        tool.NyxIdAccessToken.Should().Be("channel-agent-key");
        tool.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.ProxyDelegation);
        tool.CredentialSource.Should().Be(AgentToolCredentialSource.ChannelRegistration);
        tool.DurableNyxIdCredential.Should().NotBeNull();
        tool.DurableNyxIdCredential!.Ref.Should().Be(descriptor.Ref);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSupplementalSourceBearerIsNotBoundToDelegation_ShouldReject()
    {
        var tool = new RecordingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleToolSource(tool)],
            new PassThroughExecutionPort());
        var workflowTool = (await adapter.GetToolsAsync()).Should().ContainSingle().Subject;

        var act = () => workflowTool.ExecuteAsync(new WorkflowToolExecutionRequest(
            "{}",
            "run-alpha",
            "step-alpha",
            "execution-alpha",
            "call-alpha",
            "scope-alpha",
            new WorkflowCallerCredential
            {
                BearerToken = "source-alpha",
                Kind = NyxIdCallerCredentialKind.SourceReadableUserBearer,
                SourceReadableUserBearerToken = "source-beta",
            }));

        await act.Should().ThrowAsync<ArgumentException>();
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

        public string? SourceReadableNyxIdAccessToken { get; private set; }

        public string? OwnerSubject { get; private set; }

        public AgentToolCredentialSource CredentialSource { get; private set; }

        public DurableCallerCredentialRef? DurableNyxIdCredential { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            var senderBinding = AgentToolRequestContext.Current?.SenderBinding;
            BindingId = senderBinding?.BindingId;
            NyxUserId = senderBinding?.NyxUserId;
            SenderTenant = senderBinding?.SenderTenant;
            NyxIdAccessToken = AgentToolRequestContext.NyxIdAccessToken;
            NyxIdOrgToken = AgentToolRequestContext.NyxIdOrgToken;
            SenderNyxIdAccessToken = AgentToolRequestContext.SenderNyxIdAccessToken;
            SourceReadableNyxIdAccessToken = AgentToolRequestContext.SourceReadableNyxIdAccessToken;
            OwnerSubject = AgentToolRequestContext.OwnerSubject;
            NyxIdCredentialKind = AgentToolRequestContext.NyxIdCredentialKind;
            CredentialSource = AgentToolRequestContext.Current?.CredentialSource ??
                               AgentToolCredentialSource.Unspecified;
            DurableNyxIdCredential = AgentToolRequestContext.Current?.DurableNyxIdCredential?.Clone();
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
