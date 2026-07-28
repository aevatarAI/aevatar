using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.ExternalCapabilities;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowExternalCapabilityAdmissionServiceTests
{
    [Fact]
    public void AdmissionPlanContract_ShouldUseV3CallSiteAdmissionsAsTheOnlyCurrentFactSource()
    {
        WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion.Should()
            .Be("external-capability-admission.v3");

        var create = typeof(WorkflowCapabilityAdmissionPlanIntegrity)
            .GetMethods()
            .Single(method => method.Name == nameof(WorkflowCapabilityAdmissionPlanIntegrity.Create));
        create.GetParameters()[3].ParameterType.Should()
            .Be(typeof(IEnumerable<WorkflowCapabilityInvocationAdmission>));
    }

    [Fact]
    public void ReadinessContract_ShouldInspectAnAuthorSelectorAndReturnAServerProof()
    {
        var requestConstructor = typeof(InspectExternalWorkflowCapabilityReadinessRequest)
            .GetConstructors()
            .Should().ContainSingle().Subject;

        requestConstructor.GetParameters()[1].ParameterType.Should()
            .Be(typeof(ExternalWorkflowCapabilitySelector));
        typeof(ExternalCapabilityReadiness).GetProperty(nameof(ExternalCapabilityReadiness.SelectedCapability))
            .Should().NotBeNull();
    }

    [Fact]
    public void AdmissionPlanContract_ShouldCarryTypedDurableAuthorizationOwner()
    {
        var field = WorkflowCapabilityAdmissionPlan.Descriptor.FindFieldByName(
            "durable_authorization_owner");

        field.Should().NotBeNull();
        field!.MessageType.Name.Should().Be("ExternalCapabilityAuthorizationOwner");
    }

    [Fact]
    public void NyxIdProofPolicy_ShouldParticipateInTheExistingAdmissionDigest()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var readOnly = NyxIdCapability();
        var write = readOnly.Clone();
        write.NyxIdUserService.ExecutionPolicy = new NyxIdOperationExecutionPolicy
        {
            Risk = NyxIdOperationRisk.Write,
            Approval = NyxIdOperationApproval.Required,
            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
            AllowedExecutionModes = { ExternalCapabilityExecutionMode.Interactive },
        };

        var readPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            Admissions(readOnly),
            Ready(readOnly).Sources);
        var writePlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            Admissions(write),
            Ready(write).Sources);

        writePlan.AdmissionDigest.Should().NotBe(readPlan.AdmissionDigest);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("risk_unspecified")]
    [InlineData("risk_unknown")]
    [InlineData("approval_mismatch")]
    [InlineData("owner_unspecified")]
    [InlineData("modes_empty")]
    [InlineData("modes_duplicate")]
    [InlineData("modes_without_interactive")]
    [InlineData("modes_unknown")]
    [InlineData("durable_write")]
    public void Create_ShouldRejectMalformedNyxIdExecutionPolicy(string malformedCase)
    {
        var capability = NyxIdCapability();
        var policy = capability.NyxIdUserService.ExecutionPolicy;
        switch (malformedCase)
        {
            case "missing":
                capability.NyxIdUserService.ExecutionPolicy = null;
                break;
            case "risk_unspecified":
                policy.Risk = NyxIdOperationRisk.Unspecified;
                break;
            case "risk_unknown":
                policy.Risk = (NyxIdOperationRisk)99;
                break;
            case "approval_mismatch":
                policy.Approval = NyxIdOperationApproval.Required;
                break;
            case "owner_unspecified":
                policy.EnforcementOwner = NyxIdOperationEnforcementOwner.Unspecified;
                break;
            case "modes_empty":
                policy.AllowedExecutionModes.Clear();
                break;
            case "modes_duplicate":
                policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
                break;
            case "modes_without_interactive":
                policy.AllowedExecutionModes.Clear();
                policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);
                break;
            case "modes_unknown":
                policy.AllowedExecutionModes.Add((ExternalCapabilityExecutionMode)99);
                break;
            case "durable_write":
                policy.Risk = NyxIdOperationRisk.Write;
                policy.Approval = NyxIdOperationApproval.Required;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(malformedCase));
        }

        Action act = () => WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: wf-alpha\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            Admissions(capability),
            Ready(capability).Sources);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*execution policy*");
    }

    [Fact]
    public void Create_ShouldRejectExecutionModeMissingFromNyxIdPolicy()
    {
        var capability = NyxIdCapability();
        capability.NyxIdUserService.ExecutionPolicy.AllowedExecutionModes.Clear();
        capability.NyxIdUserService.ExecutionPolicy.AllowedExecutionModes.Add(
            ExternalCapabilityExecutionMode.Interactive);

        Action act = () => WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: wf-alpha\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            Admissions(capability),
            Ready(capability).Sources);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*execution mode*execution policy*");
    }

    [Fact]
    public void AdmissionPlanContract_ShouldPreserveTheLegacyV2WireFixtureWhenV3FieldsAreEmpty()
    {
        const string legacyWireBase64 =
            "CiBleHRlcm5hbC1jYXBhYmlsaXR5LWFkbWlzc2lvbi52MhIRZGVmaW5pdGlvbi1kaWdlc3QYATJAMzg5ODkwYzIzMmI5NGEzNzU0MDQ4MjE4NGZhMDVhYjhmYjJiZjg2NTcwOTNhNmM1ODgyYThkMDM3YjE1MGFhNg==";
        var legacyWire = Convert.FromBase64String(legacyWireBase64);
        var plan = WorkflowCapabilityAdmissionPlan.Parser.ParseFrom(legacyWire);

        plan.InvocationAdmissions.Should().BeEmpty();
        plan.ToByteArray().Should().Equal(legacyWire);
        plan.AdmissionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan));

        var v3 = plan.Clone();
        v3.SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.SchemaVersion;
        WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(v3).Should()
            .NotBe(plan.AdmissionDigest);
    }

    [Fact]
    public void AdmissionServiceContract_ShouldSeparatePersistedPlanRevalidation()
    {
        typeof(IWorkflowExternalCapabilityAdmissionService)
            .GetMethod("RevalidatePersistedAsync")
            .Should().NotBeNull();
    }

    [Fact]
    public void AdmissionRequest_ShouldExposeOrderedWorkflowBundleFactory()
    {
        typeof(WorkflowExternalCapabilityAdmissionRequest)
            .GetMethod("FromWorkflowYamls")
            .Should().NotBeNull();
    }

    [Fact]
    public void PersistedAdmissionRequest_ShouldRequireExpectedExecutionMode()
    {
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: wf-alpha\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);

        var act = () => new PersistedWorkflowCapabilityAdmissionRequest(
            plan,
            "name: wf-alpha\nsteps: []\n",
            new Dictionary<string, string>(),
            "scope-workflow",
            ExternalCapabilityExecutionMode.Unspecified);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*execution mode is required*");
    }

    [Fact]
    public void AdmissionPlanIntegrity_ShouldAcceptHostConnectorSelectorWithoutOptionalContractDigest()
    {
        const string yaml = "name: connector-workflow\nsteps: []\n";
        var selector = new ExternalWorkflowCapabilitySelector
        {
            HostConnector = new HostConnectorCapabilityRef
            {
                ConnectorCapabilityRef = "connector-alpha",
                OperationId = "send-summary",
            },
        };
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [new WorkflowCapabilityInvocationAdmission
            {
                CallSiteId = "connector-workflow/send",
                Capability = new ExternalWorkflowCapabilityRef
                {
                    HostConnector = selector.HostConnector.Clone(),
                },
            }],
            [new ExternalCapabilitySourceStamp
            {
                SourceKind = ExternalCapabilitySourceKind.ConnectorCatalog,
                SourceId = "connector-catalog-fixture",
                ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
                FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
                ContentDigest = "connector-catalog-digest",
            }]);

        var act = () => WorkflowCapabilityAdmissionPlanIntegrity.ValidateOrThrow(
            plan,
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [new ExternalToolInvocationSpec
            {
                CallSiteId = "connector-workflow/send",
                ToolName = "connector_call",
                Selector = selector,
            }]);

        act.Should().NotThrow();
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
        plan.InvocationAdmissions.Should().BeEmpty();
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
    public async Task AdmitAsync_ShouldPreserveTypedAuthoringFailureFromParser()
    {
        var parserReadiness = new ExternalCapabilityReadiness
        {
            Status = ExternalCapabilityReadinessStatus.ContractDrift,
            SelectedSelector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdOperation = new NyxIdOperationSelector
                {
                    UserServiceId = "us-alpha",
                    OperationId = "get-resource",
                },
            },
            Blockers =
            {
                new ExternalCapabilityBlocker
                {
                    Status = ExternalCapabilityReadinessStatus.ContractDrift,
                    Code = "NYXID_OPERATION_ARGUMENT_INVALID",
                    SafeMessage = "The NyxID operation runtime arguments are invalid.",
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
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Invalid("internal parser detail", parserReadiness)),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.ExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Interactive);
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_OPERATION_ARGUMENT_INVALID");
        exception.Which.Message.Should().NotContain("internal parser detail");
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

        plan.ExternalCapabilities.Should().BeEmpty();
        plan.InvocationAdmissions.Should().ContainSingle();
        plan.InvocationAdmissions[0].CallSiteId.Should().Be(CallSiteId);
        plan.InvocationAdmissions[0].Capability.NyxIdUserService.UserServiceId.Should().Be("us-home-alpha");
        plan.SourceStamps.Select(static source => source.SourceKind).Should().BeEquivalentTo([
            ExternalCapabilitySourceKind.NyxIdUserServices,
            ExternalCapabilitySourceKind.NyxIdOpenApi,
        ]);
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
    public async Task AdmitAsync_ShouldRejectReadyProofForDifferentExecutionMode()
    {
        var capability = NyxIdCapability();
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(Ready(capability)),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.AdmitAsync(Request(
            "name: wf-alpha\nsteps: []\n",
            executionMode: ExternalCapabilityExecutionMode.Durable));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("READINESS_EXECUTION_MODE_MISMATCH");
    }

    [Fact]
    public async Task AdmitAsync_ShouldRejectReadyProofForDifferentSelector()
    {
        var capability = NyxIdCapability();
        var different = NyxIdCapability();
        different.NyxIdUserService.UserServiceId = "us-home-beta";
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(Ready(
                different,
                ExternalCapabilityExecutionMode.Durable,
                includeDurableCatalog: true)),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.AdmitAsync(Request(
            "name: wf-alpha\nsteps: []\n",
            executionMode: ExternalCapabilityExecutionMode.Durable));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("READINESS_SELECTOR_PROOF_MISMATCH");
    }

    [Fact]
    public async Task AdmitAsync_ShouldRejectDurableReadyProofWithoutCatalogSource()
    {
        var capability = NyxIdCapability();
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(Ready(capability, ExternalCapabilityExecutionMode.Durable)),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.AdmitAsync(Request(
            "name: wf-alpha\nsteps: []\n",
            executionMode: ExternalCapabilityExecutionMode.Durable));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("DURABLE_AUTHORIZATION_SOURCE_REQUIRED");
    }

    [Fact]
    public async Task AdmitAsync_ShouldRejectReadyProofWithoutExactNyxIdOpenApiSource()
    {
        var capability = NyxIdCapability();
        var readiness = Ready(capability);
        var openApi = readiness.Sources.Single(static source =>
            source.SourceKind == ExternalCapabilitySourceKind.NyxIdOpenApi);
        readiness.Sources.Remove(openApi);
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(readiness),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("READINESS_SOURCE_REQUIRED");
    }

    [Fact]
    public async Task AdmitAsync_ShouldRejectDurableCatalogSourceForDifferentCaller()
    {
        var capability = NyxIdCapability();
        var readiness = Ready(
            capability,
            ExternalCapabilityExecutionMode.Durable,
            includeDurableCatalog: true);
        readiness.Sources.Single(static source =>
            source.SourceKind == ExternalCapabilitySourceKind.DurableAuthorizationCatalog).SourceId =
            CatalogSourceId("caller-beta");
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(readiness),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.AdmitAsync(Request(
            "name: wf-alpha\nsteps: []\n",
            executionMode: ExternalCapabilityExecutionMode.Durable));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("DURABLE_AUTHORIZATION_SOURCE_MISMATCH");
    }

    [Fact]
    public async Task AdmitAsync_ShouldSealVerifiedDurableAuthorizationOwner()
    {
        var capability = NyxIdCapability();
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(Ready(
                capability,
                ExternalCapabilityExecutionMode.Durable,
                includeDurableCatalog: true)),
            new FixedTimeProvider());

        var plan = await service.AdmitAsync(Request(
            "name: wf-alpha\nsteps: []\n",
            executionMode: ExternalCapabilityExecutionMode.Durable));

        plan.DurableAuthorizationOwner.Should().BeEquivalentTo(
            new ExternalCapabilityAuthorizationOwner
            {
                Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
                OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            });
        plan.AdmissionDigest.Should().Be(
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan));
    }

    [Fact]
    public async Task AdmitAsync_ShouldRejectExpiredReadyProofBeforeSealingPlan()
    {
        var capability = NyxIdCapability();
        var readiness = Ready(capability);
        foreach (var source in readiness.Sources)
        {
            source.ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(-2));
            source.FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(-1));
        }
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(readiness),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("ADMISSION_SOURCE_STALE");
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldVerifyPlanWithoutRepeatingExternalRead()
    {
        var capability = NyxIdCapability();
        var parser = new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability)));
        var readiness = new StubReadinessPort(Ready(capability));
        var service = new WorkflowExternalCapabilityAdmissionService(parser, readiness, new FixedTimeProvider());
        var initial = await service.AdmitAsync(Request("name: wf-alpha\nsteps: []\n"));

        var verified = await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                initial,
                "name: wf-alpha\nsteps: []\n",
                new Dictionary<string, string>(),
                "scope-workflow",
                ExternalCapabilityExecutionMode.Interactive));

        verified.Should().BeEquivalentTo(initial);
        readiness.Calls.Should().Be(1);
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldReturnTypedRebindReadiness_ForLegacyPlan()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var legacyPlan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.LegacySchemaVersion,
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        };
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha")),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                legacyPlan,
                yaml,
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Interactive));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Status.Should()
            .Be(ExternalCapabilityReadinessStatus.AdmissionRebindRequired);
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be(WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode);
        exception.Which.Readiness.Remediations.Should().ContainSingle().Which.ActionKind.Should()
            .Be(ExternalCapabilityRemediationActionKind.RebindWorkflow);
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldClassifyLegacySchemaBeforeParsingLegacyAuthoring()
    {
        var legacyPlan = new WorkflowCapabilityAdmissionPlan
        {
            SchemaVersion = WorkflowCapabilityAdmissionPlanIntegrity.LegacySchemaVersion,
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        };
        var parser = new StubParser(WorkflowYamlParseResult.Invalid("legacy proof fields are invalid"));
        var service = new WorkflowExternalCapabilityAdmissionService(
            parser,
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                legacyPlan,
                "name: legacy\nsteps: []\n",
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Interactive));

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be(WorkflowCapabilityAdmissionPlanIntegrity.RebindRequiredCode);
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldRejectPlanForDifferentExpectedExecutionMode()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var existingPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha")),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existingPlan,
                yaml,
                new Dictionary<string, string>(),
                "scheduled_workflow_replay",
                ExternalCapabilityExecutionMode.Durable));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*execution mode*");
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldAcceptDurablePlanWithoutTransientCaller()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var capability = NyxIdCapability();
        var parser = new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability)));
        var readiness = new StubReadinessPort(Ready(
            capability,
            ExternalCapabilityExecutionMode.Durable,
            includeDurableCatalog: true));
        var service = new WorkflowExternalCapabilityAdmissionService(parser, readiness, new FixedTimeProvider());
        var initial = await service.AdmitAsync(Request(
            yaml,
            executionMode: ExternalCapabilityExecutionMode.Durable));

        var verified = await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                initial,
                yaml,
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Durable));

        verified.Should().BeEquivalentTo(initial);
        readiness.Calls.Should().Be(1);
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldRejectDurableNyxIdPlanWithoutCatalogSource()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var capability = NyxIdCapability();
        var existingPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            Admissions(capability),
            Ready(capability).Sources,
            DurableOwner());
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existingPlan,
                yaml,
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Durable));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*durable authorization catalog source*");
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldRejectPlanForDifferentCatalogOwner()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var capability = NyxIdCapability();
        var readiness = Ready(
            capability,
            ExternalCapabilityExecutionMode.Durable,
            includeDurableCatalog: true);
        readiness.Sources.Single(static source =>
            source.SourceKind == ExternalCapabilitySourceKind.DurableAuthorizationCatalog).SourceId =
            CatalogSourceId("caller-beta");
        var existingPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            yaml,
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            Admissions(capability),
            readiness.Sources,
            DurableOwner());
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existingPlan,
                yaml,
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Durable));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*durable authorization catalog source does not match the persisted owner*");
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldRejectDurableNyxIdPlanWithoutOwner()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var capability = NyxIdCapability();
        var existingPlan = DurablePlan(capability, owner: null);
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existingPlan,
                yaml,
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Durable));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*durable authorization owner is invalid*");
    }

    [Theory]
    [InlineData("aevatar", ExternalCapabilityAuthorizationOwnerKind.Personal, "caller-alpha")]
    [InlineData("nyxid", ExternalCapabilityAuthorizationOwnerKind.Organization, "caller-alpha")]
    [InlineData("nyxid", ExternalCapabilityAuthorizationOwnerKind.Personal, "")]
    [InlineData("nyxid", ExternalCapabilityAuthorizationOwnerKind.Personal, "   ")]
    [InlineData("nyxid", ExternalCapabilityAuthorizationOwnerKind.Personal, " caller-alpha")]
    public async Task RevalidatePersistedAsync_ShouldRejectMalformedDurableAuthorizationOwner(
        string authority,
        ExternalCapabilityAuthorizationOwnerKind ownerKind,
        string ownerSubject)
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var capability = NyxIdCapability();
        var existingPlan = DurablePlan(
            capability,
            new ExternalCapabilityAuthorizationOwner
            {
                Authority = authority,
                OwnerKind = ownerKind,
                OwnerSubject = ownerSubject,
            });
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existingPlan,
                yaml,
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Durable));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*durable authorization owner is invalid*");
    }

    [Fact]
    public async Task RevalidatePersistedAsync_ShouldRejectChangedOwnerAfterAdmissionDigestIsRecomputed()
    {
        const string yaml = "name: wf-alpha\nsteps: []\n";
        var capability = NyxIdCapability();
        var existingPlan = DurablePlan(capability, DurableOwner());
        var originalAdmissionDigest = existingPlan.AdmissionDigest;
        existingPlan.DurableAuthorizationOwner = new ExternalCapabilityAuthorizationOwner
        {
            Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
            OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
            OwnerSubject = "caller-beta",
        };
        existingPlan.AdmissionDigest =
            WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(existingPlan);
        var service = new WorkflowExternalCapabilityAdmissionService(
            new StubParser(WorkflowYamlParseResult.Success("wf-alpha", Dependencies(capability))),
            new StubReadinessPort(),
            new FixedTimeProvider());

        Func<Task> act = async () => await service.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existingPlan,
                yaml,
                new Dictionary<string, string>(),
                "service_revision_prepare",
                ExternalCapabilityExecutionMode.Durable));

        existingPlan.AdmissionDigest.Should().NotBe(originalAdmissionDigest);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*durable authorization catalog source does not match the persisted owner*");
    }

    private static WorkflowExternalCapabilityAdmissionRequest Request(
        string yaml,
        ExternalCapabilityExecutionMode executionMode = ExternalCapabilityExecutionMode.Interactive) =>
        new(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-alpha",
                "caller-alpha",
                "runtime-caller-credential"),
            yaml,
            new Dictionary<string, string>(),
            "scope-workflow",
            executionMode);

    private static ExternalCapabilityAuthorizationOwner DurableOwner() => new()
    {
        Authority = WorkflowCapabilityAdmissionPlanIntegrity.NyxIdAuthority,
        OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
        OwnerSubject = "caller-alpha",
    };

    private static WorkflowCapabilityAdmissionPlan DurablePlan(
        ExternalWorkflowCapabilityRef capability,
        ExternalCapabilityAuthorizationOwner? owner) =>
        WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: wf-alpha\nsteps: []\n",
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable,
            Admissions(capability),
            Ready(
                capability,
                ExternalCapabilityExecutionMode.Durable,
                includeDurableCatalog: true).Sources,
            owner);

    private const string CallSiteId = "wf-alpha/read-state";

    private static WorkflowAuthorizationDependencies Dependencies(
        ExternalWorkflowCapabilityRef capability)
    {
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = CallSiteId,
            ToolName = "nyxid_proxy",
            Selector = Selector(capability),
        });
        return dependencies;
    }

    private static ExternalWorkflowCapabilitySelector Selector(
        ExternalWorkflowCapabilityRef capability) =>
        new()
        {
            NyxIdOperation = new NyxIdOperationSelector
            {
                UserServiceId = capability.NyxIdUserService.UserServiceId,
                OperationId = capability.NyxIdUserService.OperationId,
            },
        };

    private static WorkflowCapabilityInvocationAdmission[] Admissions(
        ExternalWorkflowCapabilityRef capability) =>
        [new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = CallSiteId,
            Capability = capability,
        }];

    private static ExternalCapabilityReadiness Ready(
        ExternalWorkflowCapabilityRef capability,
        ExternalCapabilityExecutionMode executionMode = ExternalCapabilityExecutionMode.Interactive,
        bool includeDurableCatalog = false)
    {
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedSelector = Selector(capability),
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
                new ExternalCapabilitySourceStamp
                {
                    SourceKind = ExternalCapabilitySourceKind.NyxIdOpenApi,
                    SourceId = capability.NyxIdUserService.UserServiceId,
                    ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
                    FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
                    ContentDigest = "openapi-digest",
                },
            },
        };
        if (includeDurableCatalog)
        {
            readiness.Sources.Add(new ExternalCapabilitySourceStamp
            {
                SourceKind = ExternalCapabilitySourceKind.DurableAuthorizationCatalog,
                SourceId = CatalogSourceId("caller-alpha"),
                SourceVersion = 17,
                ObservedAt = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now),
                FreshUntil = Timestamp.FromDateTimeOffset(FixedTimeProvider.Now.AddMinutes(5)),
                ContentDigest = "catalog-digest",
            });
        }
        return readiness;
    }

    private static string CatalogSourceId(string callerId) =>
        NyxIdAuthorizationCatalogActorIds.Build(new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = callerId,
        });

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
                ExecutionPolicy = new NyxIdOperationExecutionPolicy
                {
                    Risk = NyxIdOperationRisk.ReadOnly,
                    Approval = NyxIdOperationApproval.None,
                    EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                    AllowedExecutionModes =
                    {
                        ExternalCapabilityExecutionMode.Interactive,
                        ExternalCapabilityExecutionMode.Durable,
                    },
                },
            },
        };

    private sealed class StubParser(WorkflowYamlParseResult result) : IWorkflowDefinitionParser
    {
        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default) => Task.FromResult(result);

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            Task.FromResult(ToBundleResult(result, inlineWorkflowDocuments.FirstOrDefault()?.Yaml ?? string.Empty));
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

        public async Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default)
        {
            if (inlineWorkflowDocuments.Count == 0)
                return WorkflowInlineYamlBundleParseResult.Invalid("workflowYamls is required.");

            var document = inlineWorkflowDocuments[0];
            var parseResult = await ParseWorkflowYamlAsync(document.Yaml, ct);
            return ToBundleResult(parseResult, document.Yaml);
        }
    }

    private static WorkflowInlineYamlBundleParseResult ToBundleResult(
        WorkflowYamlParseResult parseResult,
        string workflowYaml) =>
        parseResult.Succeeded
            ? WorkflowInlineYamlBundleParseResult.Success(
                parseResult.WorkflowName,
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [parseResult.WorkflowName] = workflowYaml,
                })
            : WorkflowInlineYamlBundleParseResult.Invalid(parseResult.Error, parseResult.ExternalCapabilityReadiness);

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
