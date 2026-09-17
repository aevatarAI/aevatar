using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.ExternalCapabilities;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowDefinitionParserExternalCapabilityTests
{
    [Fact]
    public async Task DefinitionParser_ForPublication_ShouldRejectLlmWithoutExplicitToolScope()
    {
        const string workflowYaml =
            "name: main\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant";
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var legacyResult = await parser.ParseWorkflowYamlAsync(workflowYaml);
        var publicationResult = await parser.ParseWorkflowYamlForPublicationAsync(workflowYaml);

        legacyResult.Succeeded.Should().BeTrue(legacyResult.Error);
        publicationResult.Succeeded.Should().BeFalse();
        publicationResult.Error.Should().Contain("must declare an explicit allowed_tools scope");
    }

    [Fact]
    public async Task DefinitionParser_ForPublication_ShouldAcceptExplicitRestrictedEmptyToolScope()
    {
        const string workflowYaml =
            "name: main\nroles:\n  - id: assistant\n    name: Assistant\n    allowed_tools: []\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant";
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseWorkflowYamlForPublicationAsync(workflowYaml);

        result.Succeeded.Should().BeTrue(result.Error);
    }

    [Theory]
    [MemberData(nameof(InvalidNyxIdAuthoringCases))]
    public async Task DefinitionParser_ShouldReturnTypedReadiness_ForInvalidNyxIdAuthoring(
        string workflowYaml,
        ExternalCapabilityReadinessStatus expectedStatus,
        string expectedCode,
        ExternalCapabilityRemediationActionKind expectedRemediation)
    {
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseWorkflowYamlAsync(workflowYaml);

        result.Succeeded.Should().BeFalse();
        result.ExternalCapabilityReadiness.Should().NotBeNull();
        result.ExternalCapabilityReadiness!.Status.Should().Be(expectedStatus);
        result.ExternalCapabilityReadiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be(expectedCode);
        result.ExternalCapabilityReadiness.Remediations.Should().ContainSingle().Which.ActionKind.Should()
            .Be(expectedRemediation);
        result.ExternalCapabilityReadiness.ToString().Should().NotContain("caller-controlled-digest");
    }

    [Fact]
    public async Task DefinitionParser_ShouldParseInlineWorkflowBundle_WhenDocumentsAreDistinct()
    {
        const string entryYaml = "name: main\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant";
        const string childYaml = "name: child\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant";
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseInlineWorkflowBundleAsync(
            [
                new WorkflowChatInlineYamlDocument(string.Empty, entryYaml),
                new WorkflowChatInlineYamlDocument(string.Empty, childYaml),
            ]);

        result.Succeeded.Should().BeTrue();
        result.EntryWorkflowName.Should().Be("main");
        result.EntryWorkflowYaml.Should().Be(entryYaml);
        result.WorkflowYamlsByName.Should().ContainKey("main");
        result.WorkflowYamlsByName.Should().ContainKey("child");
    }

    [Fact]
    public async Task DefinitionParser_ShouldRejectInlineWorkflowBundle_WhenNamesAreDuplicated()
    {
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseInlineWorkflowBundleAsync(
            [
                new WorkflowChatInlineYamlDocument(string.Empty, "name: main\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant"),
                new WorkflowChatInlineYamlDocument(string.Empty, "name: main\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant"),
            ]);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("Duplicate workflow name 'main' in workflowYamls.");
    }

    [Fact]
    public async Task DefinitionParser_ShouldRejectInlineWorkflowBundle_WhenDocumentNameMismatchesYamlName()
    {
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseInlineWorkflowBundleAsync(
            [new WorkflowChatInlineYamlDocument("requested", "name: actual\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant")]);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("workflowYamls[0] document name 'requested' does not match workflow name 'actual'.");
    }

    [Fact]
    public async Task DefinitionParser_ShouldPreserveReadiness_WhenInlineWorkflowBundleCapabilityIsInvalid()
    {
        const string workflowYaml =
            """
            name: legacy-proof
            steps:
              - id: call
                type: tool_call
                capability:
                  nyxid_operation:
                    user_service_id: us-alpha
                    endpoint_id: get-resource
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"contract_digest":"caller-controlled-digest"}'
            """;
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseInlineWorkflowBundleAsync(
            [new WorkflowChatInlineYamlDocument(string.Empty, workflowYaml)]);

        result.Succeeded.Should().BeFalse();
        result.ExternalCapabilityReadiness.Should().NotBeNull();
        result.ExternalCapabilityReadiness!.Status.Should().Be(ExternalCapabilityReadinessStatus.ContractDrift);
        result.ExternalCapabilityReadiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED");
    }

    public static TheoryData<string, ExternalCapabilityReadinessStatus, string,
        ExternalCapabilityRemediationActionKind> InvalidNyxIdAuthoringCases =>
        new()
        {
            {
                """
                name: legacy-proof
                steps:
                  - id: call
                    type: tool_call
                    capability:
                      nyxid_operation:
                        user_service_id: us-alpha
                        endpoint_id: get-resource
                    parameters:
                      tool: nyxid_proxy
                      arguments: '{"contract_digest":"caller-controlled-digest"}'
                """,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED",
                ExternalCapabilityRemediationActionKind.RebindWorkflow
            },
            {
                """
                name: dynamic-selector
                steps:
                  - id: call
                    type: tool_call
                    capability:
                      nyxid_operation:
                        user_service_id: ${input}
                        endpoint_id: get-resource
                    parameters:
                      tool: nyxid_proxy
                      arguments: '{"query":{}}'
                """,
                ExternalCapabilityReadinessStatus.OperationSelectionRequired,
                "NYXID_OPERATION_SELECTION_REQUIRED",
                ExternalCapabilityRemediationActionKind.SelectOperation
            },
            {
                """
                name: invalid-arguments
                steps:
                  - id: call
                    type: tool_call
                    capability:
                      nyxid_operation:
                        user_service_id: us-alpha
                        endpoint_id: get-resource
                    parameters:
                      tool: nyxid_proxy
                      arguments: '{"unsupported_slot":{}}'
                """,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "NYXID_OPERATION_ARGUMENT_INVALID",
                ExternalCapabilityRemediationActionKind.RebindWorkflow
            },
        };

    [Theory]
    [InlineData("resource-status-explicit-request.yaml", "resource-status-read", "/v1/resources/status", NyxIdRequestResponseMode.Text, null)]
    [InlineData("document-file-artifact-explicit-request.yaml", "document-file-artifact", "/v1/documents/{document_id}/file", NyxIdRequestResponseMode.FileArtifact, "document_id")]
    public async Task ExplicitRequestFixture_ShouldParseAdmitBindAndPreserveTheGrantedRequestContract(
        string fixtureName,
        string expectedWorkflowName,
        string expectedPathTemplate,
        NyxIdRequestResponseMode expectedResponseMode,
        string? requiredPathParameter)
    {
        const string memberId = "m-alpha";
        const string workflowId = "wf-alpha";
        const string revisionId = "rev-alpha";
        const string publishedServiceId = "svc-alpha";
        const string userServiceId = "usvc-alpha";
        new[] { memberId, workflowId, revisionId, publishedServiceId, userServiceId }
            .Distinct(StringComparer.Ordinal).Should().HaveCount(5);
        var workflowYaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "test",
            "Aevatar.Workflow.Host.Api.Tests",
            "Fixtures",
            fixtureName));
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var parsed = await parser.ParseWorkflowYamlAsync(workflowYaml);

        parsed.Succeeded.Should().BeTrue(parsed.Error);
        parsed.WorkflowName.Should().Be(expectedWorkflowName);
        var invocation = parsed.AuthorizationDependencies!.ExternalInvocations.Should().ContainSingle().Subject;
        invocation.Selector.SelectorCase.Should()
            .Be(ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest);
        invocation.Selector.NyxIdRequest.UserServiceId.Should().Be(userServiceId);
        invocation.Selector.NyxIdRequest.PathTemplate.Should().Be(expectedPathTemplate);
        invocation.Selector.NyxIdRequest.ResponseMode.Should().Be(expectedResponseMode);
        var workflow = new Aevatar.Workflow.Core.Primitives.WorkflowParser().Parse(workflowYaml);
        var serializedArguments = workflow.Steps.Should().ContainSingle().Subject.Parameters["arguments"]
            .Should().BeOfType<string>().Subject;
        using var arguments = JsonDocument.Parse(serializedArguments);
        if (requiredPathParameter is null)
        {
            arguments.RootElement.TryGetProperty("path_params", out _).Should().BeFalse();
        }
        else
        {
            arguments.RootElement.GetProperty("path_params")
                .GetProperty(requiredPathParameter).GetString().Should()
                .Be($"${{input.{requiredPathParameter}}}");
        }

        var requestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeNyxIdRequestContractDigest(invocation.Selector.NyxIdRequest);
        var admissionService = new WorkflowExternalCapabilityAdmissionService(
            parser,
            new ExternalWorkflowCapabilityReadinessService([new FixtureExplicitRequestSource()]),
            new FixtureTimeProvider());
        var plan = await admissionService.AdmitAsync(new WorkflowExternalCapabilityAdmissionRequest(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-alpha",
                "binder-alpha",
                NyxIdCallerCredentialSelection.SourceReadableUserBearer("fixture-bearer")),
            workflowYaml,
            new Dictionary<string, string>(),
            "host_fixture",
            ExternalCapabilityExecutionMode.Interactive,
            [new NyxIdExplicitRequestConfirmation
            {
                CallSiteId = invocation.CallSiteId,
                RequestContractDigest = requestContractDigest,
                AttestedRisk = NyxIdOperationRisk.ReadOnly,
                WorkflowId = workflowId,
                RevisionId = revisionId,
            }],
            workflowId,
            revisionId));

        var admission = plan.InvocationAdmissions.Should().ContainSingle().Subject;
        admission.NyxIdExplicitRequestGrant.Should().NotBeNull();
        admission.NyxIdExplicitRequestGrant.RequestContractDigest.Should().Be(requestContractDigest);
        admission.NyxIdExplicitRequestGrant.GrantorOwnerSubject.Should().Be("binder-alpha");
        admission.Capability.NyxIdUserRequest.Request.PathTemplate.Should().Be(expectedPathTemplate);
        admission.Capability.NyxIdUserRequest.Request.ResponseMode.Should().Be(expectedResponseMode);

        var dispatch = new CapturingDispatchPort();
        var port = new WorkflowRunActorPort(
            new NoopActorRuntime(),
            dispatch,
            new NoopWorkflowActorBindingReader(),
            new AcceptingArtifactCompatibilityPreflight(),
            [new WorkflowCoreModulePack()],
            logger: NullLogger<WorkflowRunActorPort>.Instance);
        await port.BindWorkflowDefinitionAsync(
            $"workflow-definition:{workflowId}",
            workflowYaml,
            parsed.WorkflowName,
            inlineWorkflowYamls: null,
            scopeId: null,
            sourceKind: "host_fixture",
            capabilityAdmissionPlan: plan,
            workflowId: workflowId,
            revisionId: revisionId,
            expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive);

        dispatch.ActorId.Should().Be($"workflow-definition:{workflowId}");
        var bind = dispatch.Envelope.Payload!.Unpack<BindWorkflowDefinitionEvent>();
        bind.CapabilityAdmissionPlan.Should().BeEquivalentTo(plan);
        WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
            bind.CapabilityAdmissionPlan,
            workflowYaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            parsed.AuthorizationDependencies!.ExternalInvocations,
            workflowId,
            revisionId);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class FixtureExplicitRequestSource : IExternalWorkflowCapabilitySource
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
            var requestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeNyxIdRequestContractDigest(request);
            var result = new ExternalCapabilityReadiness
            {
                ExecutionMode = executionMode,
                Status = ExternalCapabilityReadinessStatus.Ready,
                SelectedSelector = selector.Clone(),
                SelectedCapability = new ExternalWorkflowCapabilityRef
                {
                    NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
                    {
                        Request = request,
                        ServiceSlugSnapshot = "fixture-route-alpha",
                        ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                            .ComputeNyxIdExplicitRequestProofDigest(requestContractDigest, "fixture-route-alpha"),
                        ExecutionPolicy = new NyxIdOperationExecutionPolicy
                        {
                            Risk = NyxIdOperationRisk.ReadOnly,
                            Approval = NyxIdOperationApproval.None,
                            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                            AllowedExecutionModes = { ExternalCapabilityExecutionMode.Interactive },
                        },
                    },
                },
            };
            result.Sources.Add(new ExternalCapabilitySourceStamp
            {
                SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
                SourceId = "fixture-user-services:binder-alpha",
                ObservedAt = Timestamp.FromDateTimeOffset(FixtureTimeProvider.Now),
                FreshUntil = Timestamp.FromDateTimeOffset(FixtureTimeProvider.Now.AddMinutes(5)),
                ContentDigest = "fixture-user-services-digest",
            });
            return Task.FromResult(result);
        }
    }

    private sealed class FixtureTimeProvider : TimeProvider
    {
        public static readonly DateTimeOffset Now = new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingDispatchPort : Aevatar.Foundation.Abstractions.IActorDispatchPort
    {
        public string ActorId { get; private set; } = string.Empty;
        public EventEnvelope Envelope { get; private set; } = new();

        public Task<Aevatar.Foundation.Abstractions.DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ActorId = actorId;
            Envelope = envelope.Clone();
            return Task.FromResult(Aevatar.Foundation.Abstractions.DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class NoopActorRuntime : Aevatar.Foundation.Abstractions.IActorRuntime
    {
        public Task<Aevatar.Foundation.Abstractions.IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : Aevatar.Foundation.Abstractions.IAgent =>
            throw new NotSupportedException();

        public Task<Aevatar.Foundation.Abstractions.IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Aevatar.Foundation.Abstractions.IActor?> GetAsync(string id) =>
            Task.FromResult<Aevatar.Foundation.Abstractions.IActor?>(null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult<WorkflowActorBinding?>(null);
    }

    private sealed class AcceptingArtifactCompatibilityPreflight : IWorkflowArtifactCompatibilityPreflight
    {
        public Task ValidateAsync(
            WorkflowArtifactCompatibilityRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
