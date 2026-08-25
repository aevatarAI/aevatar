using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Workflows;
using Aevatar.Workflow.Projection.DependencyInjection;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class FileBackedWorkflowCatalogAdmissionTests
{
    [Fact]
    public async Task MaterializeAsync_ShouldAdmitBeforeCreatingDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        var observations = new RecordingWorkflowDefinitionBindObservationRuntime();
        var dispatch = new RecordingActorDispatchPort(observations);
        var admission = new RecordingWorkflowCapabilityAdmissionService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatch);
        services.AddSingleton<IWorkflowDefinitionBindObservationScopeLeasePreparationPort>(observations);
        services.AddSingleton<IWorkflowDefinitionBindObservationProjectionPort>(observations);
        services.AddSingleton<IWorkflowExternalCapabilityAdmissionService>(admission);
        services.AddWorkflowDefinitionFileSource();
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<FileBackedWorkflowCatalogPort>().MaterializeAsync(
        [
            new WorkflowDefinitionRegistration(
                "repo_install",
                "name: repo_install",
                "workflow-definition:repo_install",
                ExternalCapabilityExecutionMode.Interactive,
                "repo"),
        ]);

        admission.Request.Should().NotBeNull();
        admission.Request!.WorkflowYaml.Should().Be("name: repo_install");
        admission.Request.SourceKind.Should().Be("repo");
        admission.Request.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        runtime.Created.Should().ContainSingle(item =>
            item.ActorId == "workflow-definition:repo_install" &&
            item.AgentType == typeof(WorkflowGAgent));
        var bind = dispatch.Envelopes.Should().ContainSingle().Which.Envelope.Payload!
            .Unpack<BindWorkflowDefinitionEvent>();
        bind.WorkflowName.Should().Be("repo_install");
        bind.WorkflowYaml.Should().Be("name: repo_install");
        bind.HasScopeId.Should().BeTrue();
        bind.ScopeId.Should().BeEmpty();
        bind.SourceKind.Should().Be("repo");
        bind.CapabilityAdmissionPlan.AdmissionDigest.Should().Be("startup-admission-digest");
        bind.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        bind.CapabilityAdmissionPlan.ExecutionMode.Should().Be(bind.ExpectedExecutionMode);
    }

    [Fact]
    public async Task MaterializeAsync_ShouldReuseExactCommittedDefinitionBinding()
    {
        const string actorId = "workflow-definition:committed-alpha";
        const string workflowName = "committed-alpha";
        const string workflowYaml = "name: committed-alpha";
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);
        var runtime = new RecordingActorRuntime();
        var observations = new RecordingWorkflowDefinitionBindObservationRuntime();
        var dispatch = new RecordingActorDispatchPort(observations);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatch);
        services.AddSingleton<IWorkflowDefinitionBindObservationScopeLeasePreparationPort>(observations);
        services.AddSingleton<IWorkflowDefinitionBindObservationProjectionPort>(observations);
        services.AddSingleton<IWorkflowExternalCapabilityAdmissionService>(
            new RecordingWorkflowCapabilityAdmissionService(responsePlan: plan));
        services.AddSingleton<IWorkflowActorBindingReader>(new StaticWorkflowActorBindingReader(
            new WorkflowActorBinding(
                WorkflowActorKind.Definition,
                actorId,
                actorId,
                string.Empty,
                workflowName,
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive,
                SourceVersion: 7,
                SourceEventId: "event-alpha",
                SourceKind: "builtin",
                CapabilityAdmissionPlan: plan.Clone(),
                CatalogPublicationContractVersion: WorkflowCatalogPublicationContracts.CurrentVersion)));
        services.AddSingleton<IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string>>(
            new StaticWorkflowCatalogDocumentReader(BuildCatalogDocument(actorId, workflowName, 7)));
        services.AddWorkflowDefinitionFileSource();
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<FileBackedWorkflowCatalogPort>().MaterializeAsync(
        [
            new WorkflowDefinitionRegistration(
                workflowName,
                workflowYaml,
                actorId,
                ExternalCapabilityExecutionMode.Interactive,
                "builtin"),
        ]);

        runtime.Created.Should().BeEmpty();
        dispatch.Envelopes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("workflow-catalog-publication/v0")]
    public async Task MaterializeAsync_ShouldRebind_WhenCommittedDefinitionBindingCatalogContractIsOld(
        string catalogPublicationContractVersion)
    {
        const string actorId = "workflow-definition:catalog-contract-alpha";
        const string workflowName = "catalog-contract-alpha";
        const string workflowYaml = "name: catalog-contract-alpha";
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);
        var runtime = new RecordingActorRuntime();
        var observations = new RecordingWorkflowDefinitionBindObservationRuntime();
        var dispatch = new RecordingActorDispatchPort(observations);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatch);
        services.AddSingleton<IWorkflowDefinitionBindObservationScopeLeasePreparationPort>(observations);
        services.AddSingleton<IWorkflowDefinitionBindObservationProjectionPort>(observations);
        services.AddSingleton<IWorkflowExternalCapabilityAdmissionService>(
            new RecordingWorkflowCapabilityAdmissionService(responsePlan: plan));
        services.AddSingleton<IWorkflowActorBindingReader>(new StaticWorkflowActorBindingReader(
            new WorkflowActorBinding(
                WorkflowActorKind.Definition,
                actorId,
                actorId,
                string.Empty,
                workflowName,
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive,
                SourceVersion: 7,
                SourceEventId: "event-alpha",
                SourceKind: "builtin",
                CapabilityAdmissionPlan: plan.Clone(),
                CatalogPublicationContractVersion: catalogPublicationContractVersion)));
        services.AddSingleton<IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string>>(
            new StaticWorkflowCatalogDocumentReader(BuildCatalogDocument(actorId, workflowName, 7)));
        services.AddWorkflowDefinitionFileSource();
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<FileBackedWorkflowCatalogPort>().MaterializeAsync(
        [
            new WorkflowDefinitionRegistration(
                workflowName,
                workflowYaml,
                actorId,
                ExternalCapabilityExecutionMode.Interactive,
                "builtin"),
        ]);

        runtime.Created.Should().ContainSingle(item => item.ActorId == actorId);
        dispatch.Envelopes.Should().ContainSingle();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale-version")]
    [InlineData("future-version")]
    [InlineData("old-contract")]
    [InlineData("actor-id")]
    [InlineData("workflow-name")]
    public async Task MaterializeAsync_ShouldRebind_WhenPublicCatalogReadModelIsMissingOrStale(string mismatch)
    {
        const string actorId = "workflow-definition:catalog-stale-alpha";
        const string workflowName = "catalog-stale-alpha";
        const string workflowYaml = "name: catalog-stale-alpha";
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);
        var catalog = mismatch == "missing"
            ? null
            : BuildCatalogDocument(
                mismatch == "actor-id" ? "workflow-definition:other" : actorId,
                mismatch == "workflow-name" ? "other-template" : workflowName,
                mismatch switch
                {
                    "stale-version" => 6,
                    "future-version" => 8,
                    _ => 7,
                },
                mismatch == "old-contract"
                    ? WorkflowCatalogPublicationContracts.LegacyV0
                    : WorkflowCatalogPublicationContracts.CurrentVersion,
                workflowName);
        var runtime = new RecordingActorRuntime();
        var observations = new RecordingWorkflowDefinitionBindObservationRuntime();
        var dispatch = new RecordingActorDispatchPort(observations);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatch);
        services.AddSingleton<IWorkflowDefinitionBindObservationScopeLeasePreparationPort>(observations);
        services.AddSingleton<IWorkflowDefinitionBindObservationProjectionPort>(observations);
        services.AddSingleton<IWorkflowExternalCapabilityAdmissionService>(
            new RecordingWorkflowCapabilityAdmissionService(responsePlan: plan));
        services.AddSingleton<IWorkflowActorBindingReader>(new StaticWorkflowActorBindingReader(
            new WorkflowActorBinding(
                WorkflowActorKind.Definition,
                actorId,
                actorId,
                string.Empty,
                workflowName,
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive,
                SourceVersion: 7,
                SourceEventId: "event-alpha",
                SourceKind: "builtin",
                CapabilityAdmissionPlan: plan.Clone(),
                CatalogPublicationContractVersion: WorkflowCatalogPublicationContracts.CurrentVersion)));
        services.AddSingleton<IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string>>(
            new StaticWorkflowCatalogDocumentReader(catalog));
        services.AddWorkflowDefinitionFileSource();
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<FileBackedWorkflowCatalogPort>().MaterializeAsync(
        [
            new WorkflowDefinitionRegistration(
                workflowName,
                workflowYaml,
                actorId,
                ExternalCapabilityExecutionMode.Interactive,
                "builtin"),
        ]);

        runtime.Created.Should().ContainSingle(item => item.ActorId == actorId);
        dispatch.Envelopes.Should().ContainSingle();
    }

    [Theory]
    [InlineData("definition-actor-id")]
    [InlineData("workflow-yaml")]
    [InlineData("execution-mode")]
    [InlineData("source-kind")]
    [InlineData("admission-digest")]
    [InlineData("tool-catalog-policy")]
    public async Task MaterializeAsync_ShouldRebind_WhenCommittedDefinitionBindingDiffers(string mismatch)
    {
        const string actorId = "workflow-definition:committed-beta";
        const string workflowName = "committed-beta";
        const string workflowYaml = "name: committed-beta";
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExternalCapabilityExecutionMode.Interactive,
            [],
            []);
        var persistedPlan = plan.Clone();
        if (mismatch == "admission-digest")
        {
            persistedPlan.DefinitionDigest = "different-definition-digest";
            persistedPlan.AdmissionDigest = WorkflowCapabilityAdmissionPlanIntegrity
                .ComputeAdmissionDigest(persistedPlan);
        }

        var binding = new WorkflowActorBinding(
            WorkflowActorKind.Definition,
            actorId,
            mismatch == "definition-actor-id" ? "workflow-definition:other-beta" : actorId,
            string.Empty,
            workflowName,
            mismatch == "workflow-yaml" ? "name: other-beta" : workflowYaml,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            mismatch == "execution-mode"
                ? ExternalCapabilityExecutionMode.Durable
                : ExternalCapabilityExecutionMode.Interactive,
            SourceVersion: 8,
            SourceEventId: "event-beta",
            SourceKind: mismatch == "source-kind" ? "repo" : "builtin",
            CapabilityAdmissionPlan: persistedPlan,
            ToolCatalogPolicyVersion: mismatch == "tool-catalog-policy"
                ? WorkflowToolCatalogPolicies.LegacyV0
                : WorkflowToolCatalogPolicies.CurrentVersion,
            CatalogPublicationContractVersion: WorkflowCatalogPublicationContracts.CurrentVersion);
        var runtime = new RecordingActorRuntime();
        var observations = new RecordingWorkflowDefinitionBindObservationRuntime();
        var dispatch = new RecordingActorDispatchPort(observations);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatch);
        services.AddSingleton<IWorkflowDefinitionBindObservationScopeLeasePreparationPort>(observations);
        services.AddSingleton<IWorkflowDefinitionBindObservationProjectionPort>(observations);
        services.AddSingleton<IWorkflowExternalCapabilityAdmissionService>(
            new RecordingWorkflowCapabilityAdmissionService(responsePlan: plan));
        services.AddSingleton<IWorkflowActorBindingReader>(new StaticWorkflowActorBindingReader(binding));
        services.AddSingleton<IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string>>(
            new StaticWorkflowCatalogDocumentReader(BuildCatalogDocument(actorId, workflowName, 8)));
        services.AddWorkflowDefinitionFileSource();
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<FileBackedWorkflowCatalogPort>().MaterializeAsync(
        [
            new WorkflowDefinitionRegistration(
                workflowName,
                workflowYaml,
                actorId,
                ExternalCapabilityExecutionMode.Interactive,
                "builtin"),
        ]);

        runtime.Created.Should().ContainSingle(item => item.ActorId == actorId);
        dispatch.Envelopes.Should().ContainSingle();
    }

    [Fact]
    public async Task MaterializeAsync_ShouldNotCreateActor_WhenAdmissionFails()
    {
        var runtime = new RecordingActorRuntime();
        var observations = new RecordingWorkflowDefinitionBindObservationRuntime();
        var dispatch = new RecordingActorDispatchPort(observations);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatch);
        services.AddSingleton<IWorkflowDefinitionBindObservationScopeLeasePreparationPort>(observations);
        services.AddSingleton<IWorkflowDefinitionBindObservationProjectionPort>(observations);
        services.AddSingleton<IWorkflowExternalCapabilityAdmissionService>(
            new RecordingWorkflowCapabilityAdmissionService(new InvalidOperationException("not ready")));
        services.AddWorkflowDefinitionFileSource();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<FileBackedWorkflowCatalogPort>().MaterializeAsync(
        [
            new WorkflowDefinitionRegistration(
                "repo_install",
                "name: repo_install",
                "workflow-definition:repo_install",
                ExternalCapabilityExecutionMode.Interactive,
                "repo"),
        ]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("not ready");
        runtime.Created.Should().BeEmpty();
        dispatch.Envelopes.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_ShouldRejectUnspecifiedModeBeforeAdmissionOrActorCreation()
    {
        var runtime = new RecordingActorRuntime();
        var observations = new RecordingWorkflowDefinitionBindObservationRuntime();
        var dispatch = new RecordingActorDispatchPort(observations);
        var admission = new RecordingWorkflowCapabilityAdmissionService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatch);
        services.AddSingleton<IWorkflowDefinitionBindObservationScopeLeasePreparationPort>(observations);
        services.AddSingleton<IWorkflowDefinitionBindObservationProjectionPort>(observations);
        services.AddSingleton<IWorkflowExternalCapabilityAdmissionService>(admission);
        services.AddWorkflowDefinitionFileSource();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<FileBackedWorkflowCatalogPort>().MaterializeAsync(
        [
            new WorkflowDefinitionRegistration(
                "repo_install",
                "name: repo_install",
                "workflow-definition:repo_install",
                ExternalCapabilityExecutionMode.Unspecified,
                "repo"),
        ]);

        var error = await act.Should().ThrowAsync<WorkflowDefinitionMaterializationException>();
        error.Which.Code.Should().Be(WorkflowDefinitionMaterializationException.InvalidExecutionModeCode);
        admission.Request.Should().BeNull();
        runtime.Created.Should().BeEmpty();
        dispatch.Envelopes.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_ShouldRejectAdmissionModeDriftBeforeActorCreation()
    {
        var runtime = new RecordingActorRuntime();
        var observations = new RecordingWorkflowDefinitionBindObservationRuntime();
        var dispatch = new RecordingActorDispatchPort(observations);
        var admission = new RecordingWorkflowCapabilityAdmissionService(
            responseMode: ExternalCapabilityExecutionMode.Durable);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatch);
        services.AddSingleton<IWorkflowDefinitionBindObservationScopeLeasePreparationPort>(observations);
        services.AddSingleton<IWorkflowDefinitionBindObservationProjectionPort>(observations);
        services.AddSingleton<IWorkflowExternalCapabilityAdmissionService>(admission);
        services.AddWorkflowDefinitionFileSource();
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<FileBackedWorkflowCatalogPort>().MaterializeAsync(
        [
            new WorkflowDefinitionRegistration(
                "repo_install",
                "name: repo_install",
                "workflow-definition:repo_install",
                ExternalCapabilityExecutionMode.Interactive,
                "repo"),
        ]);

        var error = await act.Should().ThrowAsync<WorkflowDefinitionMaterializationException>();
        error.Which.Code.Should().Be(WorkflowDefinitionMaterializationException.AdmissionModeMismatchCode);
        runtime.Created.Should().BeEmpty();
        dispatch.Envelopes.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_WithLocalRuntime_ShouldCommitRealWorkflowDefinitionBind()
    {
        const string actorId = "workflow-definition:studio-commit";
        const string workflowYaml = """
            name: studio
            roles:
              - id: assistant
                name: Assistant
                allowed_tools: []
            steps:
              - id: reply
                type: llm_call
                role: assistant
                parameters: {}
            """;
        using var provider = CreateLocalRuntimeProvider(TimeSpan.FromSeconds(2));
        var runtime = provider.GetRequiredService<IActorRuntime>();

        try
        {
            await provider.GetRequiredService<FileBackedWorkflowCatalogPort>().MaterializeAsync(
            [
                new WorkflowDefinitionRegistration(
                    "studio",
                    workflowYaml,
                    actorId,
                    ExternalCapabilityExecutionMode.Interactive,
                    "builtin"),
            ]);

            var actor = await runtime.GetAsync(actorId);
            actor.Should().NotBeNull();
            var definitionAgent = actor!.Agent.Should().BeOfType<WorkflowGAgent>().Subject;
            definitionAgent.State.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
            definitionAgent.State.CapabilityAdmissionPlan.ExecutionMode.Should()
                .Be(ExternalCapabilityExecutionMode.Interactive);
            definitionAgent.State.Version.Should().Be(1);
            var events = await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId);
            events.Should().ContainSingle();
            events[0].EventData.Unpack<BindWorkflowDefinitionEvent>().ExpectedExecutionMode.Should()
                .Be(ExternalCapabilityExecutionMode.Interactive);
        }
        finally
        {
            await runtime.DestroyAsync(actorId);
        }
    }

    [Fact]
    public async Task MaterializeAsync_WithLocalRuntime_ShouldRepublishUnchangedDefinitionBindWithoutAppendingEvent()
    {
        const string actorId = "workflow-definition:studio-republish";
        const string workflowYaml = """
            name: studio
            roles:
              - id: assistant
                name: Assistant
                allowed_tools: []
            steps:
              - id: reply
                type: llm_call
                role: assistant
                parameters: {}
            """;
        using var provider = CreateLocalRuntimeProvider(TimeSpan.FromSeconds(2));
        var runtime = provider.GetRequiredService<IActorRuntime>();

        try
        {
            var port = provider.GetRequiredService<FileBackedWorkflowCatalogPort>();
            var definition = new WorkflowDefinitionRegistration(
                "studio",
                workflowYaml,
                actorId,
                ExternalCapabilityExecutionMode.Interactive,
                "builtin");

            await port.MaterializeAsync([definition]);
            await port.MaterializeAsync([definition]);

            var actor = await runtime.GetAsync(actorId);
            var definitionAgent = actor!.Agent.Should().BeOfType<WorkflowGAgent>().Subject;
            definitionAgent.State.Version.Should().Be(1);
            var events = await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId);
            events.Should().ContainSingle();
            events[0].EventData.Unpack<BindWorkflowDefinitionEvent>().CatalogPublicationContractVersion.Should()
                .Be(WorkflowCatalogPublicationContracts.CurrentVersion);
        }
        finally
        {
            await runtime.DestroyAsync(actorId);
        }
    }

    [Fact]
    public async Task WorkflowGAgent_ShouldRepublishCommittedScopeWhenIncomingNoOpOmitsScope()
    {
        const string actorId = "workflow-definition:studio-scoped-republish";
        const string workflowYaml = """
            name: studio
            roles:
              - id: assistant
                name: Assistant
                allowed_tools: []
            steps:
              - id: reply
                type: llm_call
                role: assistant
                parameters: {}
            """;
        using var provider = CreateLocalRuntimeProvider(TimeSpan.FromSeconds(2));
        var runtime = provider.GetRequiredService<IActorRuntime>();
        await SeedDefinitionBindingAsync(
            provider,
            actorId,
            workflowYaml,
            ExternalCapabilityExecutionMode.Interactive,
            ExternalCapabilityExecutionMode.Interactive,
            scopeId: "scope-a");

        try
        {
            var actor = await runtime.CreateAsync<WorkflowGAgent>(actorId);
            var definitionAgent = actor.Agent.Should().BeOfType<WorkflowGAgent>().Subject;
            var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive,
                [],
                []);

            await definitionAgent.BindWorkflowDefinitionAsync(
                workflowYaml,
                "studio",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                scopeId: null,
                sourceKind: "builtin",
                capabilityAdmissionPlan: plan,
                workflowId: null,
                revisionId: null,
                expectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
                toolCatalogPolicyVersion: WorkflowToolCatalogPolicies.CurrentVersion,
                catalogPublicationContractVersion: WorkflowCatalogPublicationContracts.CurrentVersion);

            definitionAgent.State.Version.Should().Be(1);
            definitionAgent.State.ScopeId.Should().Be("scope-a");
            var events = await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId);
            events.Should().ContainSingle();
        }
        finally
        {
            await runtime.DestroyAsync(actorId);
        }
    }

    [Fact]
    public async Task MaterializeAsync_WithLegacyUnspecifiedBinding_ShouldCommitForwardRepair()
    {
        const string actorId = "workflow-definition:studio-legacy";
        const string workflowYaml = """
            name: studio
            roles:
              - id: assistant
                name: Assistant
                allowed_tools: []
            steps:
              - id: reply
                type: llm_call
                role: assistant
                parameters: {}
            """;
        using var provider = CreateLocalRuntimeProvider(TimeSpan.FromSeconds(2));
        var runtime = provider.GetRequiredService<IActorRuntime>();
        await SeedDefinitionBindingAsync(
            provider,
            actorId,
            workflowYaml,
            ExternalCapabilityExecutionMode.Unspecified,
            ExternalCapabilityExecutionMode.Durable);

        try
        {
            await provider.GetRequiredService<FileBackedWorkflowCatalogPort>().MaterializeAsync(
            [
                new WorkflowDefinitionRegistration(
                    "studio",
                    workflowYaml,
                    actorId,
                    ExternalCapabilityExecutionMode.Interactive,
                    "builtin"),
            ]);

            var actor = await runtime.GetAsync(actorId);
            var definitionAgent = actor!.Agent.Should().BeOfType<WorkflowGAgent>().Subject;
            definitionAgent.State.ExpectedExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
            definitionAgent.State.CapabilityAdmissionPlan.ExecutionMode.Should()
                .Be(ExternalCapabilityExecutionMode.Interactive);
            definitionAgent.State.Version.Should().Be(2);
            var events = await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId);
            events.Should().HaveCount(2);
            events[0].EventData.Unpack<BindWorkflowDefinitionEvent>().ExpectedExecutionMode.Should()
                .Be(ExternalCapabilityExecutionMode.Unspecified);
            events[1].EventData.Unpack<BindWorkflowDefinitionEvent>().ExpectedExecutionMode.Should()
                .Be(ExternalCapabilityExecutionMode.Interactive);
        }
        finally
        {
            await runtime.DestroyAsync(actorId);
        }
    }

    [Fact]
    public async Task MaterializeAsync_WhenRealActorRejectsModeChange_ShouldNotReportReadiness()
    {
        const string actorId = "workflow-definition:studio-durable";
        const string workflowYaml = """
            name: studio
            roles:
              - id: assistant
                name: Assistant
                allowed_tools: []
            steps:
              - id: reply
                type: llm_call
                role: assistant
                parameters: {}
            """;
        using var provider = CreateLocalRuntimeProvider(TimeSpan.FromMilliseconds(250));
        var runtime = provider.GetRequiredService<IActorRuntime>();
        await SeedDefinitionBindingAsync(
            provider,
            actorId,
            workflowYaml,
            ExternalCapabilityExecutionMode.Durable,
            ExternalCapabilityExecutionMode.Durable);

        try
        {
            var act = () => provider.GetRequiredService<FileBackedWorkflowCatalogPort>().MaterializeAsync(
            [
                new WorkflowDefinitionRegistration(
                    "studio",
                    workflowYaml,
                    actorId,
                    ExternalCapabilityExecutionMode.Interactive,
                    "builtin"),
            ]);

            var error = await act.Should().ThrowAsync<WorkflowDefinitionMaterializationException>();
            error.Which.Code.Should().Be(WorkflowDefinitionMaterializationException.BindNotCommittedCode);
            var actor = await runtime.GetAsync(actorId);
            actor!.Agent.Should().BeOfType<WorkflowGAgent>().Subject.State.ExpectedExecutionMode.Should()
                .Be(ExternalCapabilityExecutionMode.Durable);
            (await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId)).Should().ContainSingle();
        }
        finally
        {
            await runtime.DestroyAsync(actorId);
        }
    }

    [Fact]
    public async Task Bootstrap_ShouldLoadConfiguredDirectories_AndHonorCancellation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wf-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "review.yaml"), "name: review");
            var registry = new WorkflowDefinitionCatalog();
            var options = new WorkflowDefinitionFileSourceOptions
            {
                DuplicatePolicy = WorkflowDefinitionDuplicatePolicy.Override,
            };
            options.WorkflowDirectories.Add(tempDir);
            var observations = new RecordingWorkflowDefinitionBindObservationRuntime();
            var dispatch = new RecordingActorDispatchPort(observations);
            var service = new WorkflowDefinitionBootstrapHostedService(
                registry,
                new WorkflowDefinitionFileLoader(),
                new FileBackedWorkflowCatalogPort(
                    new RecordingActorRuntime(),
                    dispatch,
                    observations,
                    observations,
                    new RecordingWorkflowCapabilityAdmissionService(),
                    Options.Create(new WorkflowDefinitionFileSourceOptions()),
                    NullLogger<FileBackedWorkflowCatalogPort>.Instance),
                Options.Create(options),
                NullLogger<WorkflowDefinitionBootstrapHostedService>.Instance);

            await service.StartAsync(CancellationToken.None);

            registry.GetYaml("review").Should().Contain("name: review");
            dispatch.Envelopes.Should().BeEmpty(
                "actor materialization waits until every hosted service, including Kestrel and Orleans, has started");
            await service.StartedAsync(CancellationToken.None);
            dispatch.Envelopes.Should().ContainSingle();
            await service.StopAsync(CancellationToken.None);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var act = () => service.StartAsync(cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Bootstrap_StartedAsync_ShouldRetryTransientBindTimeout()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wf-bootstrap-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "review.yaml"), "name: review");
            var registry = new WorkflowDefinitionCatalog();
            var options = new WorkflowDefinitionFileSourceOptions
            {
                DuplicatePolicy = WorkflowDefinitionDuplicatePolicy.Override,
                BindCommitTimeout = TimeSpan.FromMilliseconds(10),
                BindCommitMaxAttempts = 2,
                BindCommitRetryDelay = TimeSpan.Zero,
            };
            options.WorkflowDirectories.Add(tempDir);
            var observations = new RecordingWorkflowDefinitionBindObservationRuntime
            {
                PublishCommittedBinds = false,
            };
            var dispatch = new RecordingActorDispatchPort(observations);
            dispatch.OnDispatch = count =>
            {
                if (count == 2)
                    observations.PublishCommittedBinds = true;
            };
            var service = new WorkflowDefinitionBootstrapHostedService(
                registry,
                new WorkflowDefinitionFileLoader(),
                new FileBackedWorkflowCatalogPort(
                    new RecordingActorRuntime(),
                    dispatch,
                    observations,
                    observations,
                    new RecordingWorkflowCapabilityAdmissionService(),
                    Options.Create(options),
                    NullLogger<FileBackedWorkflowCatalogPort>.Instance),
                Options.Create(options),
                NullLogger<WorkflowDefinitionBootstrapHostedService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await service.StartedAsync(CancellationToken.None);

            dispatch.Envelopes.Should().HaveCount(2);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Bootstrap_StartedAsync_ShouldRetryTransientObservationPreparationTimeout()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wf-bootstrap-observation-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "review.yaml"), "name: review");
            var registry = new WorkflowDefinitionCatalog();
            var options = new WorkflowDefinitionFileSourceOptions
            {
                DuplicatePolicy = WorkflowDefinitionDuplicatePolicy.Override,
                BindCommitMaxAttempts = 2,
                BindCommitRetryDelay = TimeSpan.Zero,
            };
            options.WorkflowDirectories.Add(tempDir);
            var observations = new RecordingWorkflowDefinitionBindObservationRuntime
            {
                PrepareException = new TimeoutException("projection observation relay pending"),
            };
            var dispatch = new RecordingActorDispatchPort(observations);
            var service = new WorkflowDefinitionBootstrapHostedService(
                registry,
                new WorkflowDefinitionFileLoader(),
                new FileBackedWorkflowCatalogPort(
                    new RecordingActorRuntime(),
                    dispatch,
                    observations,
                    observations,
                    new RecordingWorkflowCapabilityAdmissionService(),
                    Options.Create(options),
                    NullLogger<FileBackedWorkflowCatalogPort>.Instance),
                Options.Create(options),
                NullLogger<WorkflowDefinitionBootstrapHostedService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await service.StartedAsync(CancellationToken.None);

            observations.PrepareCallCount.Should().Be(2);
            dispatch.Envelopes.Should().ContainSingle();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static ServiceProvider CreateLocalRuntimeProvider(TimeSpan bindCommitTimeout)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAevatarRuntime();
        services.AddAevatarWorkflow();
        services.AddSingleton<IWorkflowActorBindingReader, EmptyWorkflowActorBindingReader>();
        services.AddWorkflowExecutionProjectionCQRS();
        services.AddSingleton<IWorkflowExternalCapabilityAdmissionService,
            IntegrityWorkflowCapabilityAdmissionService>();
        services.AddWorkflowDefinitionFileSource(options =>
            options.BindCommitTimeout = bindCommitTimeout);
        return services.BuildServiceProvider();
    }

    private static Task SeedDefinitionBindingAsync(
        IServiceProvider provider,
        string actorId,
        string workflowYaml,
        ExternalCapabilityExecutionMode persistedMode,
        ExternalCapabilityExecutionMode admissionMode,
        string scopeId = "")
    {
        var plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            workflowYaml,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            admissionMode,
            [],
            []);
        var workflow = new WorkflowParser().Parse(workflowYaml);
        var bind = new BindWorkflowDefinitionEvent
        {
            WorkflowName = "studio",
            WorkflowYaml = workflowYaml,
            SourceKind = "builtin",
            AuthorizationDependencies = WorkflowAuthorizationDependencyEvaluator.Evaluate(workflow),
            CapabilityAdmissionPlan = plan,
            ExpectedExecutionMode = persistedMode,
            ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
            CatalogPublicationContractVersion = WorkflowCatalogPublicationContracts.CurrentVersion,
        };
        if (!string.IsNullOrWhiteSpace(scopeId))
            bind.ScopeId = scopeId;
        return provider.GetRequiredService<IEventStore>().AppendAsync(
            actorId,
            [
                new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Version = 1,
                    EventType = BindWorkflowDefinitionEvent.Descriptor.FullName,
                    EventData = Any.Pack(bind),
                    AgentId = actorId,
                },
            ],
            expectedVersion: 0);
    }

    private static WorkflowCatalogCurrentStateDocument BuildCatalogDocument(
        string actorId,
        string workflowName,
        long stateVersion,
        string catalogPublicationContractVersion = WorkflowCatalogPublicationContracts.CurrentVersion,
        string? documentId = null) =>
        new()
        {
            Id = documentId ?? workflowName,
            ActorId = actorId,
            WorkflowName = workflowName,
            StateVersion = stateVersion,
            CatalogPublicationContractVersion = catalogPublicationContractVersion,
        };

    private sealed class IntegrityWorkflowCapabilityAdmissionService :
        IWorkflowExternalCapabilityAdmissionService
    {
        public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(WorkflowCapabilityAdmissionPlanIntegrity.Create(
                request.WorkflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                request.ExecutionMode,
                [],
                []));
        }

        public Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
            PersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(request.Plan.Clone());
        }

        public Task<WorkflowCapabilityAdmissionPlan> RefreshPersistedAsync(
            RefreshPersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(request.Persisted.Plan.Clone());
        }
    }

    private sealed class RecordingWorkflowCapabilityAdmissionService(
        Exception? failure = null,
        ExternalCapabilityExecutionMode? responseMode = null,
        WorkflowCapabilityAdmissionPlan? responsePlan = null) :
        IWorkflowExternalCapabilityAdmissionService
    {
        public WorkflowExternalCapabilityAdmissionRequest? Request { get; private set; }

        public PersistedWorkflowCapabilityAdmissionRequest? PersistedRequest { get; private set; }

        public RefreshPersistedWorkflowCapabilityAdmissionRequest? RefreshRequest { get; private set; }

        public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            if (failure is not null)
                throw failure;
            return Task.FromResult(responsePlan?.Clone() ?? new WorkflowCapabilityAdmissionPlan
            {
                DefinitionDigest = "startup-definition-digest",
                AdmissionDigest = "startup-admission-digest",
                ExecutionMode = responseMode ?? request.ExecutionMode,
            });
        }

        public Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
            PersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PersistedRequest = request;
            if (failure is not null)
                throw failure;
            return Task.FromResult(request.Plan.Clone());
        }

        public Task<WorkflowCapabilityAdmissionPlan> RefreshPersistedAsync(
            RefreshPersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshRequest = request;
            if (failure is not null)
                throw failure;
            return Task.FromResult(request.Persisted.Plan.Clone());
        }
    }

    private sealed class StaticWorkflowActorBindingReader(WorkflowActorBinding binding)
        : IWorkflowActorBindingReader
    {
        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkflowActorBinding?>(
                string.Equals(actorId, binding.ActorId, StringComparison.Ordinal) ? binding : null);
        }
    }

    private sealed class StaticWorkflowCatalogDocumentReader(WorkflowCatalogCurrentStateDocument? document)
        : IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string>
    {
        public Task<WorkflowCatalogCurrentStateDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkflowCatalogCurrentStateDocument?>(
                string.Equals(key, document?.Id, StringComparison.Ordinal) ? document : null);
        }

        public Task<ProjectionDocumentQueryResult<WorkflowCatalogCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(document == null
                ? ProjectionDocumentQueryResult<WorkflowCatalogCurrentStateDocument>.Empty
                : new ProjectionDocumentQueryResult<WorkflowCatalogCurrentStateDocument>
                {
                    Items = [document],
                });
        }
    }

    private sealed class EmptyWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkflowActorBinding?>(null);
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<(string ActorId, System.Type AgentType)> Created { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(
            System.Type agentType,
            string? id = null,
            CancellationToken ct = default)
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            Created.Add((actorId, agentType));
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort(
        RecordingWorkflowDefinitionBindObservationRuntime observations) : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Envelopes { get; } = [];

        public Action<int>? OnDispatch { get; set; }

        public async Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Envelopes.Add((actorId, envelope));
            OnDispatch?.Invoke(Envelopes.Count);
            await observations.PublishCommittedBindAsync(actorId, envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    private sealed class RecordingWorkflowDefinitionBindObservationRuntime :
        IWorkflowDefinitionBindObservationScopeLeasePreparationPort,
        IWorkflowDefinitionBindObservationProjectionPort
    {
        private IEventSink<EventEnvelope>? _sink;
        private string _actorId = string.Empty;
        private string _commandId = string.Empty;

        public bool ProjectionEnabled => true;

        public bool PublishCommittedBinds { get; set; } = true;

        public Exception? PrepareException { get; set; }

        public int PrepareCallCount { get; private set; }

        public Task<WorkflowDefinitionBindObservationScopeLeasePreparation?> PrepareAsync(
            string actorId,
            string commandId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PrepareCallCount++;
            if (PrepareException is { } exception)
            {
                PrepareException = null;
                throw exception;
            }

            _actorId = actorId;
            _commandId = commandId;
            return Task.FromResult<WorkflowDefinitionBindObservationScopeLeasePreparation?>(
                new WorkflowDefinitionBindObservationScopeLeasePreparation(actorId, commandId));
        }

        public Task ReleaseAsync(
            WorkflowDefinitionBindObservationScopeLeasePreparation preparation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<EventSinkProjectionAttachment<IWorkflowDefinitionBindObservationProjectionLease>?>
            AttachExistingDefinitionProjectionAsync(
                string actorId,
                string commandId,
                IEventSink<EventEnvelope> sink,
                CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            actorId.Should().Be(_actorId);
            commandId.Should().Be(_commandId);
            _sink = sink;
            var lease = new RecordingWorkflowDefinitionBindObservationLease(actorId, commandId);
            return Task.FromResult<
                EventSinkProjectionAttachment<IWorkflowDefinitionBindObservationProjectionLease>?>(
                new EventSinkProjectionAttachment<IWorkflowDefinitionBindObservationProjectionLease>(
                    lease,
                    new CallbackAsyncDisposable(() => _sink = null)));
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IWorkflowDefinitionBindObservationProjectionLease lease,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _sink = sink;
            return Task.FromResult<IAsyncDisposable?>(new CallbackAsyncDisposable(() => _sink = null));
        }

        public async Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (liveSinkLease != null)
                await liveSinkLease.DisposeAsync();
        }

        public Task ReleaseActorProjectionAsync(
            IWorkflowDefinitionBindObservationProjectionLease lease,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async Task PublishCommittedBindAsync(
            string actorId,
            EventEnvelope command,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!PublishCommittedBinds ||
                _sink == null ||
                !string.Equals(actorId, _actorId, StringComparison.Ordinal))
                return;

            var committed = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = Guid.NewGuid().ToString("N"),
                        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                        Version = 1,
                        EventType = BindWorkflowDefinitionEvent.Descriptor.FullName,
                        EventData = command.Payload.Clone(),
                        AgentId = actorId,
                    },
                }),
                Route = EnvelopeRouteSemantics.CreateObserverPublication(
                    actorId,
                    ObserverAudience.CommittedFacts),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = command.Propagation?.CorrelationId ?? string.Empty,
                    CausationEventId = command.Id,
                },
            };
            await _sink.PushAsync(committed, ct);
        }
    }

    private sealed record RecordingWorkflowDefinitionBindObservationLease(
        string ActorId,
        string CommandId) : IWorkflowDefinitionBindObservationProjectionLease;

    private sealed class CallbackAsyncDisposable(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
