using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
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
    public void AdmissionPlanContract_ShouldCarryTypedDurableAuthorizationOwner()
    {
        var field = WorkflowCapabilityAdmissionPlan.Descriptor.FindFieldByName(
            "durable_authorization_owner");

        field.Should().NotBeNull();
        field!.MessageType.Name.Should().Be("ExternalCapabilityAuthorizationOwner");
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
    public async Task AdmitAsync_ShouldRejectReadyProofForDifferentCapability()
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
            .Be("READINESS_CAPABILITY_MISMATCH");
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
            [capability],
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
            [capability],
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
            [capability],
            Ready(
                capability,
                ExternalCapabilityExecutionMode.Durable,
                includeDurableCatalog: true).Sources,
            owner);

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

    private static ExternalCapabilityReadiness Ready(
        ExternalWorkflowCapabilityRef capability,
        ExternalCapabilityExecutionMode executionMode = ExternalCapabilityExecutionMode.Interactive,
        bool includeDurableCatalog = false)
    {
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
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
