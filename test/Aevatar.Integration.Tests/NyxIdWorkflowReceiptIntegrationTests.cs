using System.Net;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;

namespace Aevatar.Integration.Tests;

public sealed class NyxIdWorkflowReceiptIntegrationTests
{
    [Fact]
    public async Task ExplicitRequestSuccess_ShouldCompleteWorkflowToolWithExactServiceReceipt()
    {
        var handler = new SuccessHandler();
        var proxy = new NyxIdProxyTool(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
                new HttpClient(handler)),
            managedWorkflowAdmissionMode: NyxIdManagedWorkflowAdmissionMode.Enforce);
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleToolSource(proxy)],
            new PassThroughExecutionPort());
        var tool = (await adapter.GetToolsAsync()).Single(candidate => candidate.Name == "nyxid_proxy");

        var result = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
            ArgumentsJson: "{}",
            RunId: "run-alpha",
            StepId: "request-alpha",
            ExecutionId: "execution-alpha",
            CallId: "call-alpha",
            ScopeId: "scope-alpha",
            CallerCredential: new WorkflowCallerCredential
            {
                BearerToken = "delegation-alpha",
                Kind = NyxIdCallerCredentialKind.ProxyDelegation,
            },
            RuntimeContext: new WorkflowToolRuntimeContext(
                "run-actor-alpha",
                "run-alpha",
                "request-alpha",
                "run-alpha",
                1),
            InvocationAdmission: ExplicitRequestAdmission()));

        handler.RequestCount.Should().Be(1);
        result.Failure.Should().BeNull();
        result.ResultJson.Should().Be("{\"ok\":true}");
    }

    private static WorkflowCapabilityInvocationAdmission ExplicitRequestAdmission()
    {
        const string callSiteId = "workflow-alpha/request-alpha";
        const string serviceSlug = "service-alpha";
        var request = new NyxIdRequestSelector
        {
            UserServiceId = "usvc-alpha",
            Method = NyxIdRequestMethod.Get,
            PathTemplate = "/api/resources",
            BodyMode = NyxIdRequestBodyMode.None,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };
        var requestDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeNyxIdRequestContractDigest(request);
        var grant = new NyxIdExplicitRequestGrant
        {
            WorkflowId = "workflow-alpha",
            RevisionId = "revision-alpha",
            CallSiteId = callSiteId,
            RequestContractDigest = requestDigest,
            GrantorAuthority = NyxIdExplicitRequestGrantorAuthority.AevatarWorkflowBinder,
            GrantorOwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            GrantorOwnerSubject = "binder-alpha",
            Risk = NyxIdOperationRisk.ReadOnly,
            AllowedExecutionModes = { ExternalCapabilityExecutionMode.Interactive },
        };
        return new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = callSiteId,
            Capability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
                {
                    Request = request,
                    ServiceSlugSnapshot = serviceSlug,
                    ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                        .ComputeNyxIdExplicitRequestProofDigest(requestDigest, serviceSlug),
                    ExplicitRequestGrantDigest = WorkflowCapabilityAdmissionPlanIntegrity
                        .ComputeNyxIdExplicitRequestGrantDigest(grant),
                    ExecutionPolicy = new NyxIdOperationExecutionPolicy
                    {
                        Risk = NyxIdOperationRisk.ReadOnly,
                        Approval = NyxIdOperationApproval.None,
                        EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                        AllowedExecutionModes = { ExternalCapabilityExecutionMode.Interactive },
                    },
                },
            },
            NyxIdExplicitRequestGrant = grant,
        };
    }

    private sealed class SingleToolSource(IAgentTool tool) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
    }

    private sealed class PassThroughExecutionPort : IAgentToolExecutionPort
    {
        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            AgentToolTerminalOutcome terminalOutcome;
            using (AgentToolContextScope.Push(request.ExecutionContext))
            {
                terminalOutcome = await request.Tool.ExecuteWithOutcomeAsync(
                    request.ExecutionContext.Request.CallId ?? string.Empty,
                    request.Tool.Name,
                    request.ArgumentsJson,
                    ct);
            }

            var receipt = terminalOutcome.Receipt ?? new AgentToolReceipt
            {
                CallId = request.ExecutionContext.Request.CallId ?? string.Empty,
                ToolName = request.Tool.Name,
                Status = AgentToolReceiptStatus.Unspecified,
                ResultJson = terminalOutcome.ResultJson,
            };
            return new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                terminalOutcome.ResultJson,
                receipt,
                IsMutation: !request.Tool.IsReadOnly,
                FailureCode: string.Empty,
                SafeMessage: string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: true,
                Retryable: false,
                AuditCompleted: true);
        }
    }

    private sealed class SuccessHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}"),
            });
        }
    }
}
