using System.Text.Json;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.ExternalCapabilities;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowExplicitRequestPreviewServiceTests
{
    [Fact]
    public async Task PreviewAsync_WhenRevisionIdIsMissing_ShouldAllocateOpaqueServerRevisionIdentity()
    {
        var selector = RequestSelector("/records/{id}", NyxIdRequestMethod.Get, bodyRequired: false);
        selector.BodyMode = NyxIdRequestBodyMode.None;
        var parser = new StubParser(new ExternalToolInvocationSpec
        {
            CallSiteId = "wf-alpha/read-record",
            ToolName = "nyxid_proxy",
            Selector = new ExternalWorkflowCapabilitySelector { NyxIdRequest = selector },
        });
        var service = new WorkflowExplicitRequestPreviewService(
            parser,
            new RecordingReadinessPort(CanonicalReadiness(
                selector,
                "slug-preview-secret",
                NyxIdOperationRisk.ReadOnly)));

        var result = await service.PreviewAsync(new WorkflowExplicitRequestPreviewRequest(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-alpha",
                "authenticated-owner-alpha",
                NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                    "bearer-preview-secret")),
            "name: wf-alpha",
            null,
            ExternalCapabilityExecutionMode.Interactive,
            WorkflowId: "wf-alpha",
            RevisionId: null));

        result.WorkflowId.Should().Be("wf-alpha");
        result.RevisionId.Should().StartWith("rev-");
        result.RevisionId.Should().NotBe(result.WorkflowId);
    }

    [Fact]
    public async Task PreviewAsync_ShouldReturnOnlyCanonicalConfirmationFieldsWithoutCreatingGrant()
    {
        const string bearer = "bearer-preview-secret";
        const string serviceSlug = "slug-preview-secret";
        var authored = RequestSelector("/draft/{id}", NyxIdRequestMethod.Post, bodyRequired: false);
        var canonical = RequestSelector("/canonical/{id}", NyxIdRequestMethod.Post, bodyRequired: true);
        var parser = new StubParser(new ExternalToolInvocationSpec
        {
            CallSiteId = "wf-alpha/request-alpha",
            ToolName = "nyxid_proxy",
            Selector = new ExternalWorkflowCapabilitySelector { NyxIdRequest = authored },
        });
        var readiness = new RecordingReadinessPort(CanonicalReadiness(
            canonical,
            serviceSlug,
            NyxIdOperationRisk.Write));
        var service = new WorkflowExplicitRequestPreviewService(parser, readiness);
        var access = new ExternalWorkflowCapabilityAccessContext(
            "scope-alpha",
            "authenticated-owner-alpha",
            NyxIdCallerCredentialSelection.SourceReadableUserBearer(bearer));

        var result = await service.PreviewAsync(new WorkflowExplicitRequestPreviewRequest(
            access,
            "name: wf-alpha",
            null,
            ExternalCapabilityExecutionMode.Interactive,
            WorkflowId: "wf-alpha",
            RevisionId: "rev-alpha"));

        readiness.Access.Should().BeSameAs(access);
        readiness.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        var item = result.Items.Should().ContainSingle().Which;
        item.CallSiteId.Should().Be("wf-alpha/request-alpha");
        item.RequestContractDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeNyxIdRequestContractDigest(canonical));
        item.UserServiceId.Should().Be("usvc-alpha");
        item.Method.Should().Be(NyxIdRequestMethod.Post);
        item.PathTemplate.Should().Be("/canonical/{id}");
        item.BodyMode.Should().Be(NyxIdRequestBodyMode.Json);
        item.BodyRequired.Should().BeTrue();
        item.ResponseMode.Should().Be(NyxIdRequestResponseMode.Text);
        item.EffectiveRisk.Should().Be(NyxIdOperationRisk.Write);
        item.ApprovalRequired.Should().BeFalse();
        item.ApprovalEnforcement.Should().Be(WorkflowExplicitRequestApprovalEnforcement.None);
        item.AllowedExecutionModes.Should().Equal(ExternalCapabilityExecutionMode.Interactive);

        var serialized = JsonSerializer.Serialize(result);
        serialized.Should().NotContain(bearer);
        serialized.Should().NotContain(serviceSlug);
        var serializedLower = serialized.ToLowerInvariant();
        serializedLower.Should().NotContain("endpointid");
        serializedLower.Should().NotContain("grant");
        typeof(WorkflowExplicitRequestPreviewItem).GetProperties().Select(static property => property.Name)
            .Should().Equal(
                "CallSiteId",
                "RequestContractDigest",
                "UserServiceId",
                "Method",
                "PathTemplate",
                "BodyMode",
                "BodyRequired",
                "ResponseMode",
                "EffectiveRisk",
                "ApprovalRequired",
                "ApprovalEnforcement",
                "AllowedExecutionModes");
    }

    [Fact]
    public async Task PreviewAsync_ForReadOnlyRequest_ShouldReportNoApprovalEnforcement()
    {
        var selector = RequestSelector("/records/{id}", NyxIdRequestMethod.Get, bodyRequired: false);
        selector.BodyMode = NyxIdRequestBodyMode.None;
        var parser = new StubParser(new ExternalToolInvocationSpec
        {
            CallSiteId = "wf-alpha/read-record",
            ToolName = "nyxid_proxy",
            Selector = new ExternalWorkflowCapabilitySelector { NyxIdRequest = selector },
        });
        var service = new WorkflowExplicitRequestPreviewService(
            parser,
            new RecordingReadinessPort(CanonicalReadiness(
                selector,
                "slug-preview-secret",
                NyxIdOperationRisk.ReadOnly)));

        var result = await service.PreviewAsync(new WorkflowExplicitRequestPreviewRequest(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-alpha",
                "authenticated-owner-alpha",
                NyxIdCallerCredentialSelection.SourceReadableUserBearer("bearer-preview-secret")),
            "name: wf-alpha",
            null,
            ExternalCapabilityExecutionMode.Interactive,
            WorkflowId: "wf-alpha",
            RevisionId: "rev-alpha"));

        var item = result.Items.Should().ContainSingle().Which;
        item.ApprovalRequired.Should().BeFalse();
        item.ApprovalEnforcement.Should().Be(WorkflowExplicitRequestApprovalEnforcement.None);
    }

    [Fact]
    public async Task PreviewAsync_ForInteractiveReadOnlyRequest_ShouldExposeOnlyCurrentGrantMode()
    {
        var selector = RequestSelector("/records/{id}", NyxIdRequestMethod.Get, bodyRequired: false);
        selector.BodyMode = NyxIdRequestBodyMode.None;
        var parser = new StubParser(new ExternalToolInvocationSpec
        {
            CallSiteId = "wf-alpha/request-alpha",
            ToolName = "nyxid_proxy",
            Selector = new ExternalWorkflowCapabilitySelector { NyxIdRequest = selector },
        });
        var service = new WorkflowExplicitRequestPreviewService(
            parser,
            new RecordingReadinessPort(CanonicalReadiness(
                selector,
                "slug-preview-secret",
                NyxIdOperationRisk.ReadOnly)));

        var result = await service.PreviewAsync(new WorkflowExplicitRequestPreviewRequest(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-alpha",
                "authenticated-owner-alpha",
                NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                    "bearer-preview-secret")),
            "name: wf-alpha",
            null,
            ExternalCapabilityExecutionMode.Interactive,
            WorkflowId: "wf-alpha",
            RevisionId: "rev-alpha"));

        result.Items.Should().ContainSingle().Which.AllowedExecutionModes.Should()
            .Equal(ExternalCapabilityExecutionMode.Interactive);
    }

    [Theory]
    [InlineData(NyxIdRequestMethod.Post, NyxIdOperationRisk.Write)]
    [InlineData(NyxIdRequestMethod.Delete, NyxIdOperationRisk.Destructive)]
    public async Task PreviewAsync_ForDurableMutatingRequest_ShouldExposeDurableMode(
        NyxIdRequestMethod method,
        NyxIdOperationRisk risk)
    {
        var selector = RequestSelector("/records/{id}", method, bodyRequired: method != NyxIdRequestMethod.Delete);
        if (method == NyxIdRequestMethod.Delete)
            selector.BodyMode = NyxIdRequestBodyMode.None;

        var parser = new StubParser(new ExternalToolInvocationSpec
        {
            CallSiteId = "wf-alpha/request-alpha",
            ToolName = "nyxid_proxy",
            Selector = new ExternalWorkflowCapabilitySelector { NyxIdRequest = selector },
        });
        var service = new WorkflowExplicitRequestPreviewService(
            parser,
            new RecordingReadinessPort(CanonicalReadiness(
                selector,
                "slug-preview-secret",
                risk)));

        var result = await service.PreviewAsync(new WorkflowExplicitRequestPreviewRequest(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-alpha",
                "authenticated-owner-alpha",
                NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                    "bearer-preview-secret")),
            "name: wf-alpha",
            null,
            ExternalCapabilityExecutionMode.Durable,
            WorkflowId: "wf-alpha",
            RevisionId: "rev-alpha"));

        result.Items.Should().ContainSingle().Which.AllowedExecutionModes.Should().Equal(
            ExternalCapabilityExecutionMode.Interactive,
            ExternalCapabilityExecutionMode.Durable);
    }

    [Fact]
    public async Task PreviewAsync_WhenOnlyDurableCatalogIsMissing_ShouldStillReturnReviewableContract()
    {
        var selector = RequestSelector("/records/{id}", NyxIdRequestMethod.Get, bodyRequired: false);
        selector.BodyMode = NyxIdRequestBodyMode.None;
        var parser = new StubParser(new ExternalToolInvocationSpec
        {
            CallSiteId = "wf-alpha/request-alpha",
            ToolName = "nyxid_proxy",
            Selector = new ExternalWorkflowCapabilitySelector { NyxIdRequest = selector },
        });
        var readiness = CanonicalReadiness(selector, "slug-preview-secret", NyxIdOperationRisk.ReadOnly);
        readiness.ExecutionMode = ExternalCapabilityExecutionMode.Durable;
        readiness.Status = ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable;
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
            Code = "DURABLE_AUTHORIZATION_UNAVAILABLE",
            SafeMessage = "The current catalog does not prove this durable grant.",
        });
        var service = new WorkflowExplicitRequestPreviewService(
            parser,
            new RecordingReadinessPort(readiness));

        var result = await service.PreviewAsync(new WorkflowExplicitRequestPreviewRequest(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-alpha",
                "authenticated-owner-alpha",
                NyxIdCallerCredentialSelection.SourceReadableUserBearer("bearer-preview-secret")),
            "name: wf-alpha",
            null,
            ExternalCapabilityExecutionMode.Durable,
            WorkflowId: "wf-alpha",
            RevisionId: "rev-alpha"));

        result.Items.Should().ContainSingle().Which.AllowedExecutionModes.Should().Equal(
            ExternalCapabilityExecutionMode.Interactive,
            ExternalCapabilityExecutionMode.Durable);
    }

    [Fact]
    public async Task PreviewAsync_WhenDurableCatalogAndAnotherRequirementAreMissing_ShouldReject()
    {
        var selector = RequestSelector("/records/{id}", NyxIdRequestMethod.Get, bodyRequired: false);
        selector.BodyMode = NyxIdRequestBodyMode.None;
        var readiness = CanonicalReadiness(selector, "slug-preview-secret", NyxIdOperationRisk.ReadOnly);
        readiness.ExecutionMode = ExternalCapabilityExecutionMode.Durable;
        readiness.Status = ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable;
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
            Code = "DURABLE_AUTHORIZATION_UNAVAILABLE",
            SafeMessage = "The current catalog does not prove this durable grant.",
        });
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = ExternalCapabilityReadinessStatus.ContractDrift,
            Code = "CONTRACT_DRIFT",
            SafeMessage = "The request contract changed.",
        });
        var service = new WorkflowExplicitRequestPreviewService(
            new StubParser(new ExternalToolInvocationSpec
            {
                CallSiteId = "wf-alpha/request-alpha",
                ToolName = "nyxid_proxy",
                Selector = new ExternalWorkflowCapabilitySelector { NyxIdRequest = selector },
            }),
            new RecordingReadinessPort(readiness));

        var action = () => service.PreviewAsync(new WorkflowExplicitRequestPreviewRequest(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-alpha",
                "authenticated-owner-alpha",
                NyxIdCallerCredentialSelection.SourceReadableUserBearer("bearer-preview-secret")),
            "name: wf-alpha",
            null,
            ExternalCapabilityExecutionMode.Durable,
            WorkflowId: "wf-alpha",
            RevisionId: "rev-alpha"));

        await action.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
    }

    [Fact]
    public async Task PreviewAsync_WhenLlmCallOmitsAllowedTools_ShouldRejectWithPublicationReadinessSemantics()
    {
        var service = new WorkflowExplicitRequestPreviewService(
            new PublicationPolicyParser(),
            new ThrowingReadinessPort());

        var action = () => service.PreviewAsync(new WorkflowExplicitRequestPreviewRequest(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-alpha",
                "authenticated-owner-alpha",
                NyxIdCallerCredentialSelection.SourceReadableUserBearer("bearer-preview-secret")),
            """
            name: main
            roles:
              - id: assistant
                name: Assistant
            steps:
              - id: reply
                type: llm_call
                target_role: assistant
            """,
            null,
            ExternalCapabilityExecutionMode.Interactive,
            WorkflowId: "wf-alpha",
            RevisionId: "rev-alpha"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must declare an explicit allowed_tools scope*");
    }

    [Fact]
    public async Task PreviewAsync_WhenLlmCallDeclaresEmptyAllowedTools_ShouldAcceptPublicationReadiness()
    {
        var service = new WorkflowExplicitRequestPreviewService(
            new PublicationPolicyParser(),
            new ThrowingReadinessPort());

        var result = await service.PreviewAsync(new WorkflowExplicitRequestPreviewRequest(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-alpha",
                "authenticated-owner-alpha",
                NyxIdCallerCredentialSelection.SourceReadableUserBearer("bearer-preview-secret")),
            """
            name: main
            roles:
              - id: assistant
                name: Assistant
            steps:
              - id: reply
                type: llm_call
                target_role: assistant
                allowed_tools: []
            """,
            null,
            ExternalCapabilityExecutionMode.Interactive,
            WorkflowId: "wf-alpha",
            RevisionId: "rev-alpha"));

        result.Items.Should().BeEmpty();
    }

    private static NyxIdRequestSelector RequestSelector(
        string pathTemplate,
        NyxIdRequestMethod method,
        bool bodyRequired) =>
        new()
        {
            UserServiceId = "usvc-alpha",
            Method = method,
            PathTemplate = pathTemplate,
            BodyMode = NyxIdRequestBodyMode.Json,
            BodyRequired = bodyRequired,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };

    private static ExternalCapabilityReadiness CanonicalReadiness(
        NyxIdRequestSelector canonical,
        string serviceSlug,
        NyxIdOperationRisk risk)
    {
        var policy = new NyxIdOperationExecutionPolicy
        {
            Risk = risk,
            Approval = NyxIdOperationApproval.None,
            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
        };
        policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
        policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);
        return new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedCapability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
                {
                    Request = canonical,
                    ServiceSlugSnapshot = serviceSlug,
                    ContractDigest = "proof-secret",
                    ExecutionPolicy = policy,
                },
            },
        };
    }

    private sealed class StubParser(params ExternalToolInvocationSpec[] invocations) : IWorkflowDefinitionParser
    {
        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            var dependencies = new WorkflowAuthorizationDependencies();
            dependencies.ExternalInvocations.Add(invocations.Select(static invocation => invocation.Clone()));
            return Task.FromResult(WorkflowYamlParseResult.Success("wf-alpha", dependencies));
        }

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class PublicationPolicyParser : IWorkflowDefinitionParser
    {
        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default) =>
            ParseCore(workflowYaml, requireExplicitLlmAgentToolScopes: false);

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlForPublicationAsync(
            string workflowYaml,
            CancellationToken ct = default) =>
            ParseCore(workflowYaml, requireExplicitLlmAgentToolScopes: true);

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        private static Task<WorkflowYamlParseResult> ParseCore(
            string workflowYaml,
            bool requireExplicitLlmAgentToolScopes)
        {
            try
            {
                var workflow = new WorkflowParser().Parse(workflowYaml);
                var errors = WorkflowValidator.Validate(
                    workflow,
                    new WorkflowValidator.WorkflowValidationOptions
                    {
                        RequireExplicitLlmAgentToolScopes = requireExplicitLlmAgentToolScopes,
                    },
                    availableWorkflowNames: null);
                return Task.FromResult(errors.Count == 0
                    ? WorkflowYamlParseResult.Success(
                        workflow.Name,
                        WorkflowAuthorizationDependencyEvaluator.Evaluate(workflow))
                    : WorkflowYamlParseResult.Invalid(string.Join("; ", errors)));
            }
            catch (Exception ex)
            {
                return Task.FromResult(WorkflowYamlParseResult.Invalid(ex.Message));
            }
        }
    }

    private sealed class ThrowingReadinessPort : IExternalWorkflowCapabilityReadinessPort
    {
        public Task<ExternalCapabilityReadiness> InspectAsync(
            InspectExternalWorkflowCapabilityReadinessRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Readiness should not be inspected for this test.");
    }

    private sealed class RecordingReadinessPort(ExternalCapabilityReadiness result) :
        IExternalWorkflowCapabilityReadinessPort
    {
        public ExternalWorkflowCapabilityAccessContext? Access { get; private set; }
        public ExternalCapabilityExecutionMode ExecutionMode { get; private set; }

        public Task<ExternalCapabilityReadiness> InspectAsync(
            InspectExternalWorkflowCapabilityReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            Access = request.Access;
            ExecutionMode = request.ExecutionMode;
            return Task.FromResult(result.Clone());
        }
    }
}
