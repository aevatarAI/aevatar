using System.Security.Cryptography;
using System.Text;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.ExternalCapabilities;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowExplicitRequestAdmissionTests
{
    private const string WorkflowYaml = """
        name: wf-alpha
        steps:
          - id: request-alpha
            type: tool_call
            capability:
              nyxid_request:
                user_service_id: usvc-alpha
                method: GET
                path_template: /api/resources/{resource_id}
                query_parameters: [page_size]
                header_parameters: [If-Match]
                body_mode: none
                response_mode: text
            parameters:
              tool: nyxid_proxy
              arguments: '{}'
        """;

    private const string PublishedOperationWorkflowYaml = """
        name: wf-published
        steps:
          - id: request-published
            type: tool_call
            capability:
              nyxid_operation:
                user_service_id: usvc-published
                endpoint_id: endpoint-list
            parameters:
              tool: nyxid_proxy
              arguments: '{}'
        """;

    private const string MultiExplicitWorkflowYaml = """
        name: wf-alpha
        steps:
          - id: request-alpha
            type: tool_call
            capability:
              nyxid_request:
                user_service_id: usvc-alpha
                method: GET
                path_template: /api/resources/{resource_id}
                query_parameters: [page_size]
                header_parameters: [If-Match]
                body_mode: none
                response_mode: text
            parameters:
              tool: nyxid_proxy
              arguments: '{}'
          - id: request-beta
            type: tool_call
            capability:
              nyxid_request:
                user_service_id: usvc-alpha
                method: GET
                path_template: /api/resources/{resource_id}
                query_parameters: [page_size]
                header_parameters: [If-Match]
                body_mode: none
                response_mode: text
            parameters:
              tool: nyxid_proxy
              arguments: '{}'
        """;

    [Fact]
    public void ConfirmationContract_ShouldExposeOnlyCallerAttestationFields()
    {
        NyxIdExplicitRequestConfirmation.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().Equal("call_site_id", "request_contract_digest", "attested_risk");
    }

    [Fact]
    public async Task AdmitAsync_WithRealParserAndReadinessButNoConfirmation_ShouldRequireExplicitGrant()
    {
        var service = CreateService();

        Func<Task> act = async () => await service.AdmitAsync(Request());

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.ContractDrift);
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED");
    }

    [Fact]
    public async Task AdmitAsync_WithTwoExplicitRequestsButOnlyFirstConfirmed_ShouldRequireSecondGrant()
    {
        var service = CreateService();

        Func<Task> act = async () => await service.AdmitAsync(Request(
            MultiExplicitWorkflowYaml,
            ExternalCapabilityExecutionMode.Interactive,
            [MatchingConfirmation()]));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.ContractDrift);
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED");
    }

    [Fact]
    public async Task AdmitAsync_WithMatchingConfirmation_ShouldMaterializeBinderOwnedGrantWithoutBearer()
    {
        var service = CreateService();
        var confirmation = MatchingConfirmation();

        var plan = await service.AdmitAsync(Request([confirmation]));

        var admission = plan.InvocationAdmissions.Should().ContainSingle().Subject;
        admission.CallSiteId.Should().Be("wf-alpha/request-alpha");
        admission.NyxIdExplicitRequestGrant.Should().NotBeNull();
        var grant = admission.NyxIdExplicitRequestGrant;
        grant.CallSiteId.Should().Be("wf-alpha/request-alpha");
        grant.RequestContractDigest.Should().Be(confirmation.RequestContractDigest);
        grant.GrantorAuthority.Should().Be(NyxIdExplicitRequestGrantorAuthority.AevatarWorkflowBinder);
        grant.GrantorOwnerKind.Should().Be(ExternalCapabilityAuthorizationOwnerKind.Personal);
        grant.GrantorOwnerSubject.Should().Be("binder-alpha");
        grant.Risk.Should().Be(NyxIdOperationRisk.ReadOnly);
        grant.AllowedExecutionModes.Should().Equal(ExternalCapabilityExecutionMode.Interactive);
        admission.Capability.NyxIdUserRequest.ExplicitRequestGrantDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdExplicitRequestGrantDigest(grant));
        Encoding.UTF8.GetString(plan.ToByteArray()).Should().NotContain("transient-bearer");
        plan.ToString().Should().NotContain("transient-bearer");
        var bearerDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("transient-bearer")));
        Encoding.UTF8.GetString(plan.ToByteArray()).Should().NotContain(bearerDigest);
        plan.ToString().Should().NotContain(bearerDigest);
    }

    [Theory]
    [InlineData("call_site", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH")]
    [InlineData("digest", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_DIGEST_MISMATCH")]
    [InlineData("risk", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_RISK_MISMATCH")]
    public async Task AdmitAsync_WithStaleOrMismatchedConfirmation_ShouldReturnTypedContractDrift(
        string mismatch,
        string expectedCode)
    {
        var service = CreateService();
        var confirmation = MatchingConfirmation();
        switch (mismatch)
        {
            case "call_site":
                confirmation.CallSiteId = "wf-beta/request-alpha";
                break;
            case "digest":
                confirmation.RequestContractDigest = "stale-request-digest";
                break;
            case "risk":
                confirmation.AttestedRisk = NyxIdOperationRisk.Write;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mismatch));
        }

        Func<Task> act = async () => await service.AdmitAsync(Request([confirmation]));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.ContractDrift);
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should().Be(expectedCode);
    }

    [Fact]
    public async Task AdmitAsync_WithPublishedOperation_ShouldNotRequireExplicitRequestConfirmation()
    {
        var service = CreateService(new PublishedOperationSource());

        var plan = await service.AdmitAsync(new WorkflowExternalCapabilityAdmissionRequest(
            Access(),
            PublishedOperationWorkflowYaml,
            new Dictionary<string, string>(),
            "scope_workflow_save_and_bind",
            ExternalCapabilityExecutionMode.Interactive));

        var admission = plan.InvocationAdmissions.Should().ContainSingle().Subject;
        admission.Capability.CapabilityCase.Should()
            .Be(ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService);
        admission.NyxIdExplicitRequestGrant.Should().BeNull();
    }

    [Theory]
    [InlineData("POST", NyxIdOperationRisk.Write)]
    [InlineData("PUT", NyxIdOperationRisk.Write)]
    [InlineData("PATCH", NyxIdOperationRisk.Write)]
    [InlineData("DELETE", NyxIdOperationRisk.Destructive)]
    public async Task AdmitAsync_WithDurableApprovalRequiredExplicitRequest_ShouldRequireInteractive(
        string method,
        NyxIdOperationRisk risk)
    {
        var service = CreateService();
        var workflowYaml = MutatingWorkflowYaml(method);
        var confirmation = new NyxIdExplicitRequestConfirmation
        {
            CallSiteId = "wf-mutating/request-mutating",
            RequestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdRequestContractDigest(MutatingSelector(method)),
            AttestedRisk = risk,
        };

        Func<Task> act = async () => await service.AdmitAsync(Request(
            workflowYaml,
            ExternalCapabilityExecutionMode.Durable,
            [confirmation]));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.ContractDrift);
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_EXPLICIT_REQUEST_INTERACTIVE_REQUIRED");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task AdmitAsync_WithDurableReadOnlyExplicitRequest_ShouldContinueToCatalogAdmission(
        string method)
    {
        var service = CreateService();
        var workflowYaml = SafeWorkflowYaml(method);
        var confirmation = new NyxIdExplicitRequestConfirmation
        {
            CallSiteId = "wf-safe/request-safe",
            RequestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdRequestContractDigest(SafeSelector(method)),
            AttestedRisk = NyxIdOperationRisk.ReadOnly,
        };

        Func<Task> act = async () => await service.AdmitAsync(Request(
            workflowYaml,
            ExternalCapabilityExecutionMode.Durable,
            [confirmation]));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Status.Should()
            .Be(ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable);
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("DURABLE_AUTHORIZATION_SOURCE_REQUIRED");
    }

    [Theory]
    [InlineData("GET", NyxIdOperationRisk.Write)]
    [InlineData("GET", NyxIdOperationRisk.Destructive)]
    [InlineData("HEAD", NyxIdOperationRisk.Write)]
    [InlineData("HEAD", NyxIdOperationRisk.Destructive)]
    [InlineData("OPTIONS", NyxIdOperationRisk.Write)]
    [InlineData("OPTIONS", NyxIdOperationRisk.Destructive)]
    public async Task AdmitAsync_WithDurableElevatedRiskSafeRequest_ShouldRequireInteractive(
        string method,
        NyxIdOperationRisk currentRisk)
    {
        var service = CreateService(new ExplicitRequestSource(currentRisk));
        var workflowYaml = SafeWorkflowYaml(method);

        Func<Task> act = async () => await service.AdmitAsync(Request(
            workflowYaml,
            ExternalCapabilityExecutionMode.Durable,
            [SafeConfirmation(method, currentRisk)]));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.ContractDrift);
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_EXPLICIT_REQUEST_INTERACTIVE_REQUIRED");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task AdmitAsync_WithStaleReadOnlyConfirmationForElevatedSafeRequest_ShouldReturnRiskMismatch(
        string method)
    {
        var service = CreateService(new ExplicitRequestSource(NyxIdOperationRisk.Write));
        var workflowYaml = SafeWorkflowYaml(method);

        Func<Task> act = async () => await service.AdmitAsync(Request(
            workflowYaml,
            ExternalCapabilityExecutionMode.Durable,
            [SafeConfirmation(method, NyxIdOperationRisk.ReadOnly)]));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.ContractDrift);
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_EXPLICIT_REQUEST_CONFIRMATION_RISK_MISMATCH");
    }

    [Fact]
    public void FromWorkflowYamls_ShouldCloneExplicitRequestConfirmations()
    {
        var confirmation = MatchingConfirmation();

        var request = WorkflowExternalCapabilityAdmissionRequest.FromWorkflowYamls(
            Access(),
            [WorkflowYaml],
            "studio_member_binding_run",
            ExternalCapabilityExecutionMode.Interactive,
            [confirmation]);
        confirmation.RequestContractDigest = "mutated-after-construction";

        request.ExplicitRequestConfirmations.Should().ContainSingle().Which.RequestContractDigest.Should()
            .Be(WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdRequestContractDigest(Selector()));
    }

    private static WorkflowExternalCapabilityAdmissionService CreateService(
        IExternalWorkflowCapabilitySource? source = null)
    {
        var readiness = new ExternalWorkflowCapabilityReadinessService(
            [source ?? new ExplicitRequestSource()]);
        return new WorkflowExternalCapabilityAdmissionService(
            new RealWorkflowDefinitionParser(),
            readiness,
            new FixedTimeProvider());
    }

    private static WorkflowExternalCapabilityAdmissionRequest Request(
        IReadOnlyList<NyxIdExplicitRequestConfirmation>? confirmations = null) =>
        Request(WorkflowYaml, ExternalCapabilityExecutionMode.Interactive, confirmations);

    private static WorkflowExternalCapabilityAdmissionRequest Request(
        string workflowYaml,
        ExternalCapabilityExecutionMode executionMode,
        IReadOnlyList<NyxIdExplicitRequestConfirmation>? confirmations = null) =>
        new(
            Access(),
            workflowYaml,
            new Dictionary<string, string>(),
            "scope_workflow_save_and_bind",
            executionMode,
            confirmations);

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new("scope-alpha", "  binder-alpha  ", "transient-bearer");

    private static NyxIdExplicitRequestConfirmation MatchingConfirmation() =>
        new()
        {
            CallSiteId = "wf-alpha/request-alpha",
            RequestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdRequestContractDigest(Selector()),
            AttestedRisk = NyxIdOperationRisk.ReadOnly,
        };

    private static NyxIdRequestSelector Selector()
    {
        var selector = new NyxIdRequestSelector
        {
            UserServiceId = "usvc-alpha",
            Method = NyxIdRequestMethod.Get,
            PathTemplate = "/api/resources/{resource_id}",
            BodyMode = NyxIdRequestBodyMode.None,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };
        selector.QueryParameters.Add("page_size");
        selector.HeaderParameters.Add("If-Match");
        return selector;
    }

    private static string MutatingWorkflowYaml(string method) => $$"""
        name: wf-mutating
        steps:
          - id: request-mutating
            type: tool_call
            capability:
              nyxid_request:
                user_service_id: usvc-alpha
                method: {{method}}
                path_template: /api/resources
                body_mode: json
                body_required: true
                response_mode: text
            parameters:
              tool: nyxid_proxy
              arguments: '{}'
        """;

    private static NyxIdRequestSelector MutatingSelector(string method) =>
        new()
        {
            UserServiceId = "usvc-alpha",
            Method = method switch
            {
                "POST" => NyxIdRequestMethod.Post,
                "PUT" => NyxIdRequestMethod.Put,
                "PATCH" => NyxIdRequestMethod.Patch,
                "DELETE" => NyxIdRequestMethod.Delete,
                _ => throw new ArgumentOutOfRangeException(nameof(method)),
            },
            PathTemplate = "/api/resources",
            BodyMode = NyxIdRequestBodyMode.Json,
            BodyRequired = true,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };

    private static string SafeWorkflowYaml(string method) => $$"""
        name: wf-safe
        steps:
          - id: request-safe
            type: tool_call
            capability:
              nyxid_request:
                user_service_id: usvc-alpha
                method: {{method}}
                path_template: /api/resources
                body_mode: none
                response_mode: text
            parameters:
              tool: nyxid_proxy
              arguments: '{}'
        """;

    private static NyxIdRequestSelector SafeSelector(string method) =>
        new()
        {
            UserServiceId = "usvc-alpha",
            Method = method switch
            {
                "GET" => NyxIdRequestMethod.Get,
                "HEAD" => NyxIdRequestMethod.Head,
                "OPTIONS" => NyxIdRequestMethod.Options,
                _ => throw new ArgumentOutOfRangeException(nameof(method)),
            },
            PathTemplate = "/api/resources",
            BodyMode = NyxIdRequestBodyMode.None,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };

    private static NyxIdExplicitRequestConfirmation SafeConfirmation(
        string method,
        NyxIdOperationRisk risk) =>
        new()
        {
            CallSiteId = "wf-safe/request-safe",
            RequestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdRequestContractDigest(SafeSelector(method)),
            AttestedRisk = risk,
        };

    private sealed class ExplicitRequestSource(
        NyxIdOperationRisk currentRisk = NyxIdOperationRisk.ReadOnly) : IExternalWorkflowCapabilitySource
    {
        public ExternalWorkflowCapabilitySelector.SelectorOneofCase SelectorKind =>
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest;

        public Task<ExternalWorkflowCapabilityDiscoveryResult> ListAsync(
            ExternalWorkflowCapabilityAccessContext access,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalWorkflowCapabilityDiscoveryResult());

        public Task<ExternalCapabilityReadiness> InspectAsync(
            ExternalWorkflowCapabilityAccessContext access,
            ExternalWorkflowCapabilitySelector selector,
            ExternalCapabilityExecutionMode executionMode,
            CancellationToken cancellationToken = default)
        {
            var request = selector.NyxIdRequest.Clone();
            var requestDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdRequestContractDigest(request);
            var capability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
                {
                    Request = request,
                    ServiceSlugSnapshot = "svc-alpha",
                    ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                        .ComputeNyxIdExplicitRequestProofDigest(requestDigest, "svc-alpha"),
                    ExecutionPolicy = new NyxIdOperationExecutionPolicy
                    {
                        Risk = currentRisk,
                        Approval = currentRisk == NyxIdOperationRisk.ReadOnly
                            ? NyxIdOperationApproval.None
                            : NyxIdOperationApproval.Required,
                        EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                        AllowedExecutionModes =
                        {
                            ExternalCapabilityExecutionMode.Interactive,
                            ExternalCapabilityExecutionMode.Durable,
                        },
                    },
                },
            };
            var result = new ExternalCapabilityReadiness
            {
                ExecutionMode = executionMode,
                Status = ExternalCapabilityReadinessStatus.Ready,
                SelectedSelector = selector.Clone(),
                SelectedCapability = capability,
            };
            result.Sources.Add(new ExternalCapabilitySourceStamp
            {
                SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
                SourceId = "nyxid-keys:caller:binder-alpha",
                ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
                FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
                ContentDigest = "keys-digest",
            });
            return Task.FromResult(result);
        }
    }

    private sealed class PublishedOperationSource : IExternalWorkflowCapabilitySource
    {
        public ExternalWorkflowCapabilitySelector.SelectorOneofCase SelectorKind =>
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation;

        public Task<ExternalWorkflowCapabilityDiscoveryResult> ListAsync(
            ExternalWorkflowCapabilityAccessContext access,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalWorkflowCapabilityDiscoveryResult());

        public Task<ExternalCapabilityReadiness> InspectAsync(
            ExternalWorkflowCapabilityAccessContext access,
            ExternalWorkflowCapabilitySelector selector,
            ExternalCapabilityExecutionMode executionMode,
            CancellationToken cancellationToken = default)
        {
            var capability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserService = new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = selector.NyxIdOperation.UserServiceId,
                    ServiceSlugSnapshot = "svc-published",
                    EndpointId = selector.NyxIdOperation.EndpointId,
                    HttpMethod = "GET",
                    PathTemplate = "/api/resources",
                    ContractDigest = "published-contract-digest",
                    ExecutionPolicy = new NyxIdOperationExecutionPolicy
                    {
                        Risk = NyxIdOperationRisk.ReadOnly,
                        Approval = NyxIdOperationApproval.None,
                        EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                        AllowedExecutionModes = { ExternalCapabilityExecutionMode.Interactive },
                    },
                },
            };
            var result = new ExternalCapabilityReadiness
            {
                ExecutionMode = executionMode,
                Status = ExternalCapabilityReadinessStatus.Ready,
                SelectedSelector = selector.Clone(),
                SelectedCapability = capability,
            };
            result.Sources.Add(new ExternalCapabilitySourceStamp
            {
                SourceKind = ExternalCapabilitySourceKind.NyxIdMcpConfig,
                SourceId = "nyxid-mcp:caller:binder-alpha",
                ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
                FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
                ContentDigest = "mcp-config-digest",
            });
            return Task.FromResult(result);
        }
    }

    private sealed class RealWorkflowDefinitionParser : IWorkflowDefinitionParser
    {
        private readonly WorkflowParser _parser = new();

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var workflow = _parser.Parse(workflowYaml);
                return Task.FromResult(WorkflowYamlParseResult.Success(
                    workflow.Name,
                    WorkflowAuthorizationDependencyEvaluator.Evaluate(workflow)));
            }
            catch (WorkflowExternalCapabilityValidationException exception)
            {
                return Task.FromResult(WorkflowYamlParseResult.Invalid(
                    exception.Message,
                    exception.Readiness));
            }
            catch (Exception exception)
            {
                return Task.FromResult(WorkflowYamlParseResult.Invalid(exception.Message));
            }
        }

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public static readonly DateTimeOffset Now =
            new(2026, 7, 30, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
