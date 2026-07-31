using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.ExternalCapabilities;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

internal static class StudioExplicitRequestAdmissionTestKit
{
    public const string CallerId = "caller-alpha";
    public const string CallerBearer = "studio-transient-bearer-secret";
    public const string OrganizationBearer = "studio-organization-bearer-secret";
    public const string WorkflowYaml = """
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

    public static StudioWorkflowCapabilityAdmissionTestService CreateAdmissionService(
        NyxIdOperationRisk currentRisk = NyxIdOperationRisk.ReadOnly)
    {
        var readiness = new ExternalWorkflowCapabilityReadinessService(
            [new ExplicitRequestSource(currentRisk)]);
        var inner = new WorkflowExternalCapabilityAdmissionService(
            new RealWorkflowDefinitionParser(),
            readiness,
            new FixedTimeProvider());
        return new StudioWorkflowCapabilityAdmissionTestService(inner);
    }

    public static WorkflowCapabilityAdmissionContext Context(
        IEnumerable<NyxIdExplicitRequestConfirmation>? confirmations = null,
        ExternalCapabilityExecutionMode executionMode = ExternalCapabilityExecutionMode.Interactive,
        WorkflowCapabilityAdmissionPlan? existingPlan = null) =>
        new(
            CallerId,
            NyxIdCallerCredentialSelection.SourceReadableUserBearer(CallerBearer),
            OrganizationBearer,
            executionMode: executionMode,
            existingPlan: existingPlan,
            explicitRequestConfirmations: confirmations);

    public static NyxIdExplicitRequestConfirmation MatchingConfirmation(
        string workflowId,
        string revisionId) =>
        new()
        {
            CallSiteId = "wf-alpha/request-alpha",
            RequestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdRequestContractDigest(Selector()),
            AttestedRisk = NyxIdOperationRisk.ReadOnly,
            WorkflowId = workflowId,
            RevisionId = revisionId,
        };

    public static IReadOnlyList<NyxIdExplicitRequestConfirmation> Confirmations(
        string scenario,
        string workflowId,
        string revisionId)
    {
        var confirmation = MatchingConfirmation(workflowId, revisionId);
        switch (scenario)
        {
            case "missing":
                return [];
            case "unknown":
                confirmation.CallSiteId = "wf-unknown/request-unknown";
                return [confirmation];
            case "duplicate":
                return [confirmation, confirmation.Clone()];
            case "stale_digest":
                confirmation.RequestContractDigest = "stale-request-contract-digest";
                return [confirmation];
            case "stale_risk":
                confirmation.AttestedRisk = NyxIdOperationRisk.Write;
                return [confirmation];
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    public static NyxIdRequestSelector Selector() =>
        new()
        {
            UserServiceId = "usvc-alpha",
            Method = NyxIdRequestMethod.Get,
            PathTemplate = "/api/resources/{resource_id}",
            QueryParameters = { "page_size" },
            HeaderParameters = { "If-Match" },
            BodyMode = NyxIdRequestBodyMode.None,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };

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

    private sealed class ExplicitRequestSource(NyxIdOperationRisk currentRisk) :
        IExternalWorkflowCapabilitySource
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
                    ServiceSlugSnapshot = "svc-catalog-alpha",
                    ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                        .ComputeNyxIdExplicitRequestProofDigest(requestDigest, "svc-catalog-alpha"),
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
                SourceId = $"nyxid-keys:caller:{CallerId}",
                SourceVersion = 23,
                ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
                FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
                ContentDigest = "keys-digest-alpha",
            });
            result.Sources.Add(new ExternalCapabilitySourceStamp
            {
                SourceKind = ExternalCapabilitySourceKind.DurableAuthorizationCatalog,
                SourceId = NyxIdAuthorizationCatalogActorIds.Build(new AuthorizationOwnerIdentity
                {
                    Authority = NyxIdAuthorizationAuthorities.NyxId,
                    OwnerKind = AuthorizationOwnerKind.Personal,
                    OwnerSubject = CallerId,
                }),
                SourceVersion = 23,
                ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
                FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
                ContentDigest = "catalog-digest-alpha",
            });
            return Task.FromResult(result);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public static readonly DateTimeOffset Now =
            new(2026, 7, 30, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
