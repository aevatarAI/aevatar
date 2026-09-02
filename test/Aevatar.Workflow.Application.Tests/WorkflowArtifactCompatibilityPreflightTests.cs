using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.DependencyInjection;
using Aevatar.Workflow.Application.ExternalCapabilities;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowArtifactCompatibilityPreflightTests
{
    private const string RootYaml = "name: wf-alpha\nsteps: []\n";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-01T08:00:00Z");

    [Fact]
    public async Task ValidateAsync_WithoutExternalInvocationsOrPlan_ShouldParseRootAndDistinctInlineYamls()
    {
        const string firstInline = "name: child-alpha\nsteps: []\n";
        const string secondInline = "name: child-beta\nsteps: []\n";
        var parser = new RecordingParser(new Dictionary<string, WorkflowYamlParseResult>
        {
            [RootYaml] = WorkflowYamlParseResult.Success("wf-alpha"),
            [firstInline] = WorkflowYamlParseResult.Success("child-alpha"),
            [secondInline] = WorkflowYamlParseResult.Success("child-beta"),
        });
        var preflight = new WorkflowArtifactCompatibilityPreflight(parser);
        var request = Request(
            plan: null,
            inlineWorkflowYamls: new Dictionary<string, string>
            {
                ["child-alpha"] = firstInline,
                ["child-alpha-alias"] = firstInline,
                ["child-beta"] = secondInline,
            });

        var act = () => preflight.ValidateAsync(request);

        await act.Should().NotThrowAsync();
        parser.Calls.Should().BeEquivalentTo([RootYaml, firstInline, secondInline]);
        parser.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task ValidateAsync_WithoutExternalInvocationsAndMatchingEmptyPlan_ShouldAccept()
    {
        var parser = ParserFor(WorkflowYamlParseResult.Success("wf-alpha"));
        var preflight = new WorkflowArtifactCompatibilityPreflight(parser);
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            RootYaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);

        var act = () => preflight.ValidateAsync(Request(plan));

        await act.Should().NotThrowAsync();
        parser.Calls.Should().ContainSingle().Which.Should().Be(RootYaml);
    }

    [Fact]
    public async Task ValidateAsync_WithExternalInvocationAndAbsentPlan_ShouldRequireRebind()
    {
        var parser = ParserFor(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(HostInvocation())));
        var preflight = new WorkflowArtifactCompatibilityPreflight(parser);

        var act = () => preflight.ValidateAsync(Request(plan: null));

        await AssertFailureAsync(
            act,
            WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode,
            "Saved workflow and capability admission no longer match.",
            ExternalCapabilityReadinessStatus.AdmissionRebindRequired);
    }

    [Theory]
    [InlineData(WorkflowCapabilityAdmissionPlanIntegrity.LegacySchemaVersion)]
    [InlineData(WorkflowCapabilityAdmissionPlanIntegrity.OpenApiSchemaVersion)]
    public async Task ValidateAsync_WithLegacyPlan_ShouldRequireRebind(string schemaVersion)
    {
        var invocation = HostInvocation();
        var parser = ParserFor(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(invocation)));
        var plan = HostPlan(invocation);
        plan.SchemaVersion = schemaVersion;
        var preflight = new WorkflowArtifactCompatibilityPreflight(parser);

        var act = () => preflight.ValidateAsync(Request(plan));

        await AssertFailureAsync(
            act,
            WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode,
            "Saved workflow and capability admission no longer match.",
            ExternalCapabilityReadinessStatus.AdmissionRebindRequired);
    }

    [Fact]
    public async Task ValidateAsync_WithMismatchedV4Plan_ShouldRequireRebind()
    {
        var invocation = HostInvocation();
        var parser = ParserFor(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(invocation)));
        var plan = HostPlan(invocation);
        plan.DefinitionDigest = "digest-other";
        plan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan);
        var preflight = new WorkflowArtifactCompatibilityPreflight(parser);

        var act = () => preflight.ValidateAsync(Request(plan));

        await AssertFailureAsync(
            act,
            WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode,
            "Saved workflow and capability admission no longer match.",
            ExternalCapabilityReadinessStatus.AdmissionRebindRequired);
    }

    [Fact]
    public async Task ValidateAsync_WithInvalidRootYaml_ShouldReturnSafeTypedParserFailure()
    {
        var parser = ParserFor(WorkflowYamlParseResult.Invalid("secret parser detail"));
        var preflight = new WorkflowArtifactCompatibilityPreflight(parser);

        var act = () => preflight.ValidateAsync(Request(plan: null));

        var exception = await AssertFailureAsync(
            act,
            "WORKFLOW_DEFINITION_INVALID",
            "Workflow definition is invalid.",
            ExternalCapabilityReadinessStatus.ContractDrift);
        exception.Message.Should().NotContain("secret parser detail");
        exception.Message.Should().NotContain(RootYaml);
    }

    [Fact]
    public async Task ValidateAsync_WithTypedInlineAuthoringFailure_ShouldPreserveParserCode()
    {
        const string inlineYaml = "name: child-alpha\nsteps: []\n";
        var readiness = new ExternalCapabilityReadiness
        {
            Status = ExternalCapabilityReadinessStatus.ContractDrift,
            Blockers =
            {
                new ExternalCapabilityBlocker
                {
                    Status = ExternalCapabilityReadinessStatus.ContractDrift,
                    Code = "NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED",
                    SafeMessage = "Workflow uses a retired NyxID tool contract.",
                },
            },
            Remediations =
            {
                new ExternalCapabilityRemediation
                {
                    ActionKind = ExternalCapabilityRemediationActionKind.RebindWorkflow,
                    Label = "Update and rebind workflow",
                },
            },
        };
        var parser = new RecordingParser(new Dictionary<string, WorkflowYamlParseResult>
        {
            [RootYaml] = WorkflowYamlParseResult.Success("wf-alpha"),
            [inlineYaml] = WorkflowYamlParseResult.Invalid("internal detail", readiness),
        });
        var preflight = new WorkflowArtifactCompatibilityPreflight(parser);

        var act = () => preflight.ValidateAsync(Request(
            plan: null,
            inlineWorkflowYamls: new Dictionary<string, string> { ["child-alpha"] = inlineYaml }));

        var exception = await AssertFailureAsync(
            act,
            "NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED",
            "Workflow uses a retired NyxID tool contract.",
            ExternalCapabilityReadinessStatus.ContractDrift);
        exception.Readiness.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        parser.Calls.Should().Equal(RootYaml, inlineYaml);
    }

    [Fact]
    public async Task ValidateAsync_WithUnspecifiedMode_ShouldRejectBeforeParsing()
    {
        var parser = ParserFor(WorkflowYamlParseResult.Success("wf-alpha"));
        var preflight = new WorkflowArtifactCompatibilityPreflight(parser);
        var request = Request(plan: null) with
        {
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Unspecified,
        };

        var act = () => preflight.ValidateAsync(request);

        await AssertFailureAsync(
            act,
            WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode,
            "Saved workflow and capability admission no longer match.",
            ExternalCapabilityReadinessStatus.AdmissionRebindRequired);
        parser.Calls.Should().BeEmpty();
    }

    [Fact]
    public void AddWorkflowApplication_ShouldRegisterSinglePreflightImplementation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowDefinitionParser>(
            ParserFor(WorkflowYamlParseResult.Success("wf-alpha")));
        services.AddWorkflowApplication();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IWorkflowArtifactCompatibilityPreflight>().Should().ContainSingle()
            .Which.Should().BeOfType<WorkflowArtifactCompatibilityPreflight>();
    }

    [Fact]
    public void AdmissionException_WithUnknownBlockerCode_ShouldExposeFallbackSafeEvidence()
    {
        var exception = new WorkflowExternalCapabilityAdmissionException(new ExternalCapabilityReadiness
        {
            Status = ExternalCapabilityReadinessStatus.ContractDrift,
            Blockers =
            {
                new ExternalCapabilityBlocker
                {
                    Status = ExternalCapabilityReadinessStatus.ContractDrift,
                    Code = "UNTRUSTED_UPSTREAM_CODE",
                    SafeMessage = " ",
                },
            },
        });

        exception.StableCode.Should().Be("WORKFLOW_ADMISSION_REJECTED");
        exception.SafeMessage.Should().Be("Workflow admission was rejected.");
    }

    private static async Task<WorkflowExternalCapabilityAdmissionException> AssertFailureAsync(
        Func<Task> act,
        string expectedCode,
        string expectedSafeMessage,
        ExternalCapabilityReadinessStatus expectedStatus)
    {
        var assertion = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        var exception = assertion.Which;
        exception.Readiness.Status.Should().Be(expectedStatus);
        var blocker = exception.Readiness.Blockers.Should().ContainSingle().Which;
        blocker.Code.Should().Be(expectedCode);
        blocker.SafeMessage.Should().Be(expectedSafeMessage);
        exception.Readiness.Remediations.Should().ContainSingle().Which.ActionKind.Should()
            .Be(ExternalCapabilityRemediationActionKind.RebindWorkflow);
        return exception;
    }

    private static WorkflowArtifactCompatibilityRequest Request(
        WorkflowCapabilityAdmissionPlan? plan,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null) =>
        new(
            RootYaml,
            inlineWorkflowYamls ?? new Dictionary<string, string>(),
            plan,
            ExternalCapabilityExecutionMode.Interactive,
            "wf-alpha",
            "rev-alpha");

    private static RecordingParser ParserFor(WorkflowYamlParseResult result) =>
        new(new Dictionary<string, WorkflowYamlParseResult> { [RootYaml] = result });

    private static WorkflowCapabilityAdmissionPlan HostPlan(ExternalToolInvocationSpec invocation) =>
        WorkflowCapabilityAdmissionPlanIntegrity.Create(
            RootYaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = invocation.CallSiteId,
                Capability = new ExternalWorkflowCapabilityRef
                {
                    HostConnector = invocation.Selector.HostConnector.Clone(),
                },
            }],
            [new ExternalCapabilitySourceStamp
            {
                SourceKind = ExternalCapabilitySourceKind.ConnectorCatalog,
                SourceId = "connector-catalog-alpha",
                ObservedAt = Timestamp.FromDateTimeOffset(Now),
                FreshUntil = Timestamp.FromDateTimeOffset(Now.AddMinutes(5)),
                ContentDigest = "connector-digest",
            }]);

    private static WorkflowAuthorizationDependencies Dependencies(ExternalToolInvocationSpec invocation)
    {
        var dependencies = new WorkflowAuthorizationDependencies();
        dependencies.ExternalInvocations.Add(invocation);
        return dependencies;
    }

    private static ExternalToolInvocationSpec HostInvocation() => new()
    {
        CallSiteId = "wf-alpha/send",
        ToolName = "connector_call",
        Selector = new ExternalWorkflowCapabilitySelector
        {
            HostConnector = new HostConnectorCapabilityRef
            {
                ConnectorCapabilityRef = "connector-alpha",
                OperationId = "send-summary",
                ContractDigest = "connector-contract-alpha",
            },
        },
    };

    private sealed class RecordingParser(
        IReadOnlyDictionary<string, WorkflowYamlParseResult> results) : IWorkflowDefinitionParser
    {
        public List<string> Calls { get; } = [];

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            Calls.Add(workflowYaml);
            return Task.FromResult(results.TryGetValue(workflowYaml, out var result)
                ? result
                : WorkflowYamlParseResult.Invalid("Unexpected workflow YAML."));
        }

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Artifact preflight must parse each distinct YAML directly.");
    }
}
