using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.ExternalCapabilities;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowExternalCapabilityAdmissionServiceTests
{
    [Fact]
    public void AdmissionRequest_ShouldExposeOrderedWorkflowBundleFactory()
    {
        typeof(WorkflowExternalCapabilityAdmissionRequest)
            .GetMethod("FromWorkflowYamls")
            .Should().NotBeNull();
    }

    [Fact]
    public async Task AdmitAsync_ShouldNormalizeOrderedBundleByExactWorkflowName()
    {
        const string rootYaml = "name: root-workflow\nsteps: []\n";
        const string childYaml = "name: child-workflow\nsteps: []\n";
        var parser = new MappingParser(new Dictionary<string, WorkflowYamlParseResult>
        {
            [rootYaml.Trim()] = WorkflowYamlParseResult.Success("root-workflow"),
            [childYaml.Trim()] = WorkflowYamlParseResult.Success("child-workflow"),
        });
        var service = new WorkflowExternalCapabilityAdmissionService(
            parser,
            new StubReadinessPort(),
            new FixedTimeProvider());

        var plan = await service.AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest.FromWorkflowYamls(
                new ExternalWorkflowCapabilityAccessContext("scope-alpha", "caller-alpha"),
                [rootYaml, childYaml],
                "studio-member-binding",
                ExternalCapabilityExecutionMode.Interactive));

        plan.DefinitionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeDefinitionDigest(
                rootYaml.Trim(),
                new Dictionary<string, string>
                {
                    ["child-workflow"] = childYaml.Trim(),
                }));
        parser.Calls.Should().Equal(rootYaml.Trim(), childYaml.Trim());
    }

    [Fact]
    public async Task AdmitAsync_ShouldSkipExternalReads_WhenDefinitionHasNoExternalCapabilities()
    {
        var parser = new StubParser(WorkflowYamlParseResult.Success(
            "wf-alpha",
            new WorkflowAuthorizationDependencies
            {
                ServiceGrantPolicy = WorkflowServiceGrantPolicy.NotRequiredNoExternalService,
            }));
        var readiness = new StubReadinessPort();
        var service = new WorkflowExternalCapabilityAdmissionService(parser, readiness, new FixedTimeProvider());

        var plan = await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        plan.SchemaVersion.Should().Be(WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion);
        plan.ExternalCapabilities.Should().BeEmpty();
        plan.SourceStamps.Should().BeEmpty();
        plan.DefinitionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeDefinitionDigest(
                "name: wf-alpha\nsteps: []\n",
                new Dictionary<string, string>()));
        plan.AdmissionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan));
        readiness.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AdmitAsync_ShouldSealExactReadyCapabilityAndSourceEvidence()
    {
        var capability = NyxIdCapability();
        var dependencies = Dependencies(capability);
        var parser = new StubParser(WorkflowYamlParseResult.Success("wf-alpha", dependencies));
        var readiness = new StubReadinessPort(Ready(capability));
        var service = new WorkflowExternalCapabilityAdmissionService(parser, readiness, new FixedTimeProvider());

        var plan = await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        plan.ExternalCapabilities.Should().ContainSingle();
        plan.ExternalCapabilities[0].NyxIdUserService.UserServiceId.Should().Be("us-home-alpha");
        plan.SourceStamps.Should().ContainSingle();
        plan.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        plan.AdmissionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan));
        readiness.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AdmitAsync_ShouldRejectBeforeMutation_WhenAnyCapabilityIsNotReady()
    {
        var readinessResult = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.CredentialConnectionRequired,
        };
        readinessResult.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = readinessResult.Status,
            Code = "CREDENTIAL_CONNECTION_REQUIRED",
            SafeMessage = "Connect the selected service credential.",
        });
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(NyxIdCapability()))),
            new StubReadinessPort(readinessResult),
            new FixedTimeProvider());

        Func<Task> act = async () =>
            await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Status.Should()
            .Be(ExternalCapabilityReadinessStatus.CredentialConnectionRequired);
        exception.Which.Message.Should().NotContain("runtime-caller-credential");
    }

    [Fact]
    public async Task AdmitAsync_ShouldVerifyTrustedExistingPlanWithoutRepeatingExternalRead()
    {
        var capability = NyxIdCapability();
        var parser = new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability)));
        var readiness = new StubReadinessPort(Ready(capability));
        var service = new WorkflowExternalCapabilityAdmissionService(parser, readiness, new FixedTimeProvider());
        var initial = await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        var verified = await service.AdmitAsync(Request(
            "name: wf-alpha\nsteps: []\n",
            existingPlan: initial));

        verified.Should().BeEquivalentTo(initial);
        readiness.Calls.Should().Be(1);
    }

    private static WorkflowExternalCapabilityAdmissionRequest Request(
        string yaml,
        WorkflowCapabilityAdmissionPlan? existingPlan = null) =>
        new(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-alpha",
                "caller-alpha",
                "runtime-caller-credential"),
            yaml,
            new Dictionary<string, string>(),
            "scope-workflow",
            ExternalCapabilityExecutionMode.Interactive,
            existingPlan);

    private static WorkflowAuthorizationDependencies Dependencies(
        ExternalWorkflowCapabilityRef capability)
    {
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalCapabilities.Add(capability);
        return dependencies;
    }

    private static ExternalCapabilityReadiness Ready(ExternalWorkflowCapabilityRef capability) =>
        new()
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedCapability = capability,
            Sources =
            {
                new ExternalCapabilitySourceStamp
                {
                    SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
                    SourceId = "nyxid-user-services:caller",
                    ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
                    FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
                    ContentDigest = "source-digest",
                },
            },
        };

    private static ExternalWorkflowCapabilityRef NyxIdCapability() =>
        new()
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = "us-home-alpha",
                ServiceSlugSnapshot = "home-assistant",
                OperationId = "get-state",
                HttpMethod = "GET",
                PathTemplate = "/states/{entity_id}",
                ContractDigest = "operation-digest",
            },
        };

    private sealed class StubParser(WorkflowYamlParseResult result) : IWorkflowDefinitionParser
    {
        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default) => Task.FromResult(result);
    }

    private sealed class MappingParser(
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
                : throw new InvalidOperationException($"Unexpected workflow YAML: {workflowYaml}"));
        }
    }

    private sealed class StubReadinessPort(ExternalCapabilityReadiness? result = null)
        : IExternalWorkflowCapabilityReadinessPort
    {
        public int Calls { get; private set; }

        public Task<ExternalCapabilityReadiness> InspectAsync(
            InspectExternalWorkflowCapabilityReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result?.Clone() ?? throw new InvalidOperationException("Unexpected readiness read."));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public static DateTimeOffset Now { get; } =
            new(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
