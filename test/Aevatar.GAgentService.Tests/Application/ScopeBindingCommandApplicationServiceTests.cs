using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Bindings;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Core.Assemblers;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScopeBindingCommandApplicationServiceTests
{
    private const string ScopeId = "scope-a";
    private static readonly ScopeWorkflowCapabilityOptions DefaultOptions = new()
    {
        DefaultServiceId = "default",
        ServiceAppId = "default",
        ServiceNamespace = "default",
    };

    [Fact]
    public async Task UpsertAsync_ShouldCreateDefaultServiceAndLifecycle_WhenNewWorkflowBindingIsSubmitted()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
                "name: child\nsteps:\n  - run: echo child",
            ])));

        commandPort.Calls.Should().HaveCount(6);
        commandPort.Calls[0].Method.Should().Be("CreateServiceAsync");
        commandPort.Calls[1].Method.Should().Be("CreateRevisionAsync");
        commandPort.Calls[2].Method.Should().Be("PrepareRevisionAsync");
        commandPort.Calls[3].Method.Should().Be("PublishRevisionAsync");
        commandPort.Calls[4].Method.Should().Be("SetDefaultServingRevisionAsync");
        commandPort.Calls[5].Method.Should().Be("ActivateServiceRevisionAsync");
        result.ScopeId.Should().Be(ScopeId);
        result.ServiceId.Should().Be(DefaultOptions.DefaultServiceId);
        result.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Workflow);
        result.Workflow.Should().NotBeNull();
        result.Workflow!.WorkflowName.Should().Be("main");
        result.DisplayName.Should().Be("main");

        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = ScopeId,
            AppId = DefaultOptions.ServiceAppId,
            Namespace = DefaultOptions.ServiceNamespace,
            ServiceId = DefaultOptions.DefaultServiceId,
        });
    }

    [Fact]
    public async Task UpsertAsync_ShouldTreatFirstYamlAsEntryWorkflow_AndRemainingAsSubWorkflows()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: root_flow\nsteps:\n  - run: echo root",
                "name: sub_a\nsteps:\n  - run: echo a",
                "name: sub_b\nsteps:\n  - run: echo b",
            ]),
            DisplayName: "My App"));

        var revisionCommand = commandPort.Calls[1].Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        revisionCommand.Spec.Identity.ServiceId.Should().Be(DefaultOptions.DefaultServiceId);
        revisionCommand.Spec.WorkflowSpec.Should().NotBeNull();
        revisionCommand.Spec.WorkflowSpec!.WorkflowName.Should().Be("root_flow");
        revisionCommand.Spec.WorkflowSpec.WorkflowYaml.Should().Contain("name: root_flow");
        revisionCommand.Spec.WorkflowSpec.InlineWorkflowYamls.Should().ContainKey("sub_a");
        revisionCommand.Spec.WorkflowSpec.InlineWorkflowYamls.Should().ContainKey("sub_b");
        revisionCommand.Spec.WorkflowSpec.InlineWorkflowYamls.Should().NotContainKey("root_flow");
    }

    [Fact]
    public async Task UpsertAsync_ShouldCreateScriptingRevision_FromScopeScript()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort
        {
            Script = new ScopeScriptSummary(
                ScopeId,
                "script-a",
                "catalog-1",
                "definition-script-1",
                "script-rev-1",
                "hash-script-1",
                DateTimeOffset.UtcNow),
        };
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort
        {
            Snapshot = CreateScriptDefinitionSnapshot("script-a", "script-rev-1", "definition-script-1"),
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting,
            Script: new ScopeBindingScriptSpec("script-a"),
            DisplayName: "Orders Script"));

        commandPort.Calls.Should().HaveCount(6);
        var revisionCommand = commandPort.Calls[1].Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        revisionCommand.Spec.ImplementationKind.Should().Be(ServiceImplementationKind.Scripting);
        revisionCommand.Spec.ScriptingSpec.Should().NotBeNull();
        revisionCommand.Spec.ScriptingSpec!.ScriptId.Should().Be("script-a");
        revisionCommand.Spec.ScriptingSpec.Revision.Should().Be("script-rev-1");
        revisionCommand.Spec.ScriptingSpec.DefinitionActorId.Should().Be("definition-script-1");
        result.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Scripting);
        result.Script.Should().NotBeNull();
        result.Script!.ScriptId.Should().Be("script-a");
        result.Script.ScriptRevision.Should().Be("script-rev-1");
    }

    [Fact]
    public async Task UpsertAsync_ShouldReuseExistingScriptingRevision_WhenExplicitRevisionAlreadyExists()
    {
        const string revisionId = "script-a-script-rev-1";
        var snapshot = CreateScriptDefinitionSnapshot("script-a", "script-rev-1", "definition-script-1");
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            new ServiceCatalogSnapshot(
                "scope-a:default:default:default",
                ScopeId,
                DefaultOptions.ServiceAppId,
                DefaultOptions.ServiceNamespace,
                DefaultOptions.DefaultServiceId,
                "Orders Script",
                revisionId,
                revisionId,
                "dep-1",
                "actor-1",
                "Active",
                [
                    new ServiceEndpointSnapshot(
                        "google.protobuf.StringValue",
                        "google.protobuf.StringValue",
                        ServiceEndpointKind.Command.ToString(),
                        "type.googleapis.com/google.protobuf.StringValue",
                        string.Empty,
                        "Scripting command endpoint for google.protobuf.StringValue."),
                ],
                [],
                DateTimeOffset.UtcNow),
            new ServiceRevisionCatalogSnapshot(
                "scope-a:default:default:default",
                [
                    new ServiceRevisionSnapshot(
                        revisionId,
                        ServiceImplementationKind.Scripting.ToString(),
                        ServiceRevisionStatus.Published.ToString(),
                        CreateScriptingArtifactHash(revisionId, snapshot),
                        string.Empty,
                        [],
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        null),
                ],
                DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort
        {
            Script = new ScopeScriptSummary(
                ScopeId,
                "script-a",
                "catalog-1",
                "definition-script-1",
                "script-rev-1",
                "hash-script-1",
                DateTimeOffset.UtcNow),
        };
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort
        {
            Snapshot = snapshot,
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting,
            Script: new ScopeBindingScriptSpec("script-a"),
            DisplayName: "Orders Script",
            RevisionId: revisionId));

        commandPort.Calls.Should().HaveCount(4);
        commandPort.Calls.Should().NotContain(call => call.Method == "CreateRevisionAsync");
        commandPort.Calls[0].Method.Should().Be("PrepareRevisionAsync");
        result.RevisionId.Should().Be(revisionId);
        result.Script.Should().NotBeNull();
        result.Script!.ScriptRevision.Should().Be("script-rev-1");
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectScriptingRevisionReuse_WhenExistingRevisionArtifactDiffers()
    {
        const string revisionId = "script-a-script-rev-1";
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(
            new ServiceCatalogSnapshot(
                "scope-a:default:default:default",
                ScopeId,
                DefaultOptions.ServiceAppId,
                DefaultOptions.ServiceNamespace,
                DefaultOptions.DefaultServiceId,
                "Orders Script",
                revisionId,
                revisionId,
                "dep-1",
                "actor-1",
                "Active",
                [
                    new ServiceEndpointSnapshot(
                        "google.protobuf.StringValue",
                        "google.protobuf.StringValue",
                        ServiceEndpointKind.Command.ToString(),
                        "type.googleapis.com/google.protobuf.StringValue",
                        string.Empty,
                        "Scripting command endpoint for google.protobuf.StringValue."),
                ],
                [],
                DateTimeOffset.UtcNow),
            new ServiceRevisionCatalogSnapshot(
                "scope-a:default:default:default",
                [
                    new ServiceRevisionSnapshot(
                        revisionId,
                        ServiceImplementationKind.Scripting.ToString(),
                        ServiceRevisionStatus.Published.ToString(),
                        "different-hash",
                        string.Empty,
                        [],
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        DateTimeOffset.UtcNow.AddHours(-1),
                        null),
                ],
                DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort
        {
            Script = new ScopeScriptSummary(
                ScopeId,
                "script-a",
                "catalog-1",
                "definition-script-1",
                "script-rev-1",
                "hash-script-1",
                DateTimeOffset.UtcNow),
        };
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort
        {
            Snapshot = CreateScriptDefinitionSnapshot("script-a", "script-rev-1", "definition-script-1"),
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting,
            Script: new ScopeBindingScriptSpec("script-a"),
            DisplayName: "Orders Script",
            RevisionId: revisionId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different scripting artifact*");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectScriptingBinding_WhenScriptSpecIsMissing()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*script is required*");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectScriptingBinding_WhenRequestedRevisionDiffersFromActiveRevision()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort
        {
            Script = new ScopeScriptSummary(
                ScopeId,
                "script-a",
                "catalog-1",
                "definition-script-1",
                "script-rev-1",
                "hash-script-1",
                DateTimeOffset.UtcNow),
        };
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting,
            Script: new ScopeBindingScriptSpec("script-a", "script-rev-2")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*currently at revision*");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectScriptingBinding_WhenScopeScriptIsMissing()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting,
            Script: new ScopeBindingScriptSpec("script-missing")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not have an active script*");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectScriptingBinding_WhenScriptDeclaresNoCommandEndpoints()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort
        {
            Script = new ScopeScriptSummary(
                ScopeId,
                "script-a",
                "catalog-1",
                "definition-script-1",
                "script-rev-1",
                "hash-script-1",
                DateTimeOffset.UtcNow),
        };
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort
        {
            Snapshot = CreateScriptDefinitionSnapshot(
                "script-a",
                "script-rev-1",
                "definition-script-1",
                ScriptMessageKind.InternalSignal),
        };
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Scripting,
            Script: new ScopeBindingScriptSpec("script-a")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not declare command endpoints*");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldCreateStaticRevision_ForGAgentBinding()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
                ScopeBindingImplementationKind.GAgent,
                GAgent: new ScopeBindingGAgentSpec(
                typeof(TestStaticServiceAgent).AssemblyQualifiedName!,
                "gagent-orders",
                [
                    new ScopeBindingGAgentEndpoint(
                        "run",
                        "Run",
                        ServiceEndpointKind.Command,
                        "type.googleapis.com/google.protobuf.StringValue",
                        string.Empty,
                        "Run the bound gagent."),
                ]),
            DisplayName: "Orders GAgent"));

        commandPort.Calls.Should().HaveCount(6);
        var createCommand = commandPort.Calls[0].Command.Should().BeOfType<CreateServiceDefinitionCommand>().Subject;
        createCommand.Spec.Endpoints.Should().ContainSingle();
        createCommand.Spec.Endpoints[0].EndpointId.Should().Be("run");
        var revisionCommand = commandPort.Calls[1].Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        revisionCommand.Spec.ImplementationKind.Should().Be(ServiceImplementationKind.Static);
        revisionCommand.Spec.StaticSpec.Should().NotBeNull();
        revisionCommand.Spec.StaticSpec!.ActorTypeName.Should().Be(typeof(TestStaticServiceAgent).AssemblyQualifiedName);
        revisionCommand.Spec.StaticSpec.PreferredActorId.Should().Be("gagent-orders");
        result.ImplementationKind.Should().Be(ScopeBindingImplementationKind.GAgent);
        result.GAgent.Should().NotBeNull();
        result.GAgent!.PreferredActorId.Should().Be("gagent-orders");
    }

    [Fact]
    public async Task UpsertAsync_ShouldRejectGAgentBinding_WhenEndpointsAreMissing()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.GAgent,
            GAgent: new ScopeBindingGAgentSpec(
                typeof(TestStaticServiceAgent).AssemblyQualifiedName!,
                "gagent-orders",
                [])));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("gagent endpoints are required.");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldUpdateExistingService_WhenDisplayNameChanges()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            ScopeId,
            DefaultOptions.ServiceAppId,
            DefaultOptions.ServiceNamespace,
            DefaultOptions.DefaultServiceId,
            "Old Name",
            "rev-old",
            "rev-old",
            "dep-old",
            "actor-old",
            "Active",
            [],
            [],
            DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            DisplayName: "Orders App"));

        commandPort.Calls.Should().HaveCount(6);
        commandPort.Calls[0].Method.Should().Be("UpdateServiceAsync");
        commandPort.Calls.Should().NotContain(call => call.Method == "CreateServiceAsync");
        var updateCommand = commandPort.Calls[0].Command.Should().BeOfType<UpdateServiceDefinitionCommand>().Subject;
        updateCommand.Spec.DisplayName.Should().Be("Orders App");
    }

    [Fact]
    public async Task UpsertAsync_ShouldPreserveExistingPolicyIds_WhenUpdatingServiceDefinitionForEndpointDrift()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            ScopeId,
            DefaultOptions.ServiceAppId,
            DefaultOptions.ServiceNamespace,
            DefaultOptions.DefaultServiceId,
            "main",
            "rev-old",
            "rev-old",
            "dep-old",
            "actor-old",
            "Active",
            [
                new ServiceEndpointSnapshot(
                    "chat",
                    "chat",
                    ServiceEndpointKind.Command.ToString(),
                    "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                    "type.googleapis.com/aevatar.ai.ChatResponseEvent",
                    "Old workflow endpoint contract."),
            ],
            ["policy-a", "policy-b"],
            DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            DisplayName: "Orders App"));

        var updateCommand = commandPort.Calls[0].Command.Should().BeOfType<UpdateServiceDefinitionCommand>().Subject;
        updateCommand.Spec.PolicyIds.Should().Equal("policy-a", "policy-b");
    }

    [Fact]
    public async Task UpsertAsync_ShouldSkipServiceDefinitionMutation_WhenDisplayNameIsUnchanged()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            ScopeId,
            DefaultOptions.ServiceAppId,
            DefaultOptions.ServiceNamespace,
            DefaultOptions.DefaultServiceId,
            "main",
            "rev-old",
            "rev-old",
            "dep-old",
            "actor-old",
            "Active",
            [
                new ServiceEndpointSnapshot(
                    "chat",
                    "chat",
                    ServiceEndpointKind.Chat.ToString(),
                    "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                    "type.googleapis.com/aevatar.ai.ChatResponseEvent",
                    "Workflow chat endpoint."),
            ],
            [],
            DateTimeOffset.UtcNow));
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ])));

        commandPort.Calls.Should().HaveCount(5);
        commandPort.Calls.Should().NotContain(call =>
            string.Equals(call.Method, "CreateServiceAsync", StringComparison.Ordinal) ||
            string.Equals(call.Method, "UpdateServiceAsync", StringComparison.Ordinal));
        commandPort.Calls[0].Method.Should().Be("CreateRevisionAsync");
    }

    [Fact]
    public async Task UpsertAsync_ShouldHonorExplicitRevisionId()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var result = await service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ]),
            DisplayName: "Orders App",
            RevisionId: "rev-explicit"));

        var revisionCommand = commandPort.Calls[1].Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        revisionCommand.Spec.RevisionId.Should().Be("rev-explicit");
        result.RevisionId.Should().Be("rev-explicit");
    }

    [Fact]
    public async Task UpsertAsync_ShouldThrow_WhenWorkflowNamesAreDuplicated()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: repeat\nsteps:\n  - run: echo root",
                "name: repeat\nsteps:\n  - run: echo child",
            ])));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Duplicate workflow name*");
    }

    [Fact]
    public async Task UpsertAsync_ShouldThrow_WhenImplementationKindIsUnsupported()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            (ScopeBindingImplementationKind)99,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
            ])));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unsupported implementationKind*");
    }

    [Fact]
    public async Task UpsertAsync_ShouldThrow_WhenWorkflowYamlEntryIsEmpty()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: main\nsteps:\n  - run: echo hello",
                "   ",
            ])));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must not contain empty YAML entries*");
    }

    [Fact]
    public async Task UpsertAsync_ShouldThrow_WhenWorkflowYamlParsingFails()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        actorPort.ParseResultsByYaml["workflow: invalid"] = WorkflowYamlParseResult.Invalid("Workflow YAML is invalid.");
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "workflow: invalid",
            ])));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Workflow YAML is invalid.");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ShouldThrow_WhenParsedWorkflowNameIsBlank()
    {
        var commandPort = new RecordingServiceCommandPort();
        var lifecyclePort = new FakeServiceLifecycleQueryPort(getResult: null);
        var scopeScriptQueryPort = new FakeScopeScriptQueryPort();
        var scriptDefinitionSnapshotPort = new FakeScriptDefinitionSnapshotPort();
        var actorPort = new FakeWorkflowRunActorPort();
        actorPort.ParseResultsByYaml["name: blank"] = WorkflowYamlParseResult.Success(string.Empty);
        var service = CreateService(commandPort, lifecyclePort, scopeScriptQueryPort, scriptDefinitionSnapshotPort, actorPort);

        var act = () => service.UpsertAsync(new ScopeBindingUpsertRequest(
            ScopeId,
            ScopeBindingImplementationKind.Workflow,
            Workflow: new ScopeBindingWorkflowSpec([
                "name: blank",
            ])));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must define a workflow name*");
        commandPort.Calls.Should().BeEmpty();
    }

    private static ScopeBindingCommandApplicationService CreateService(
        RecordingServiceCommandPort commandPort,
        FakeServiceLifecycleQueryPort lifecyclePort,
        FakeScopeScriptQueryPort scopeScriptQueryPort,
        FakeScriptDefinitionSnapshotPort scriptDefinitionSnapshotPort,
        FakeWorkflowRunActorPort actorPort) =>
        new(
            commandPort,
            lifecyclePort,
            scopeScriptQueryPort,
            scriptDefinitionSnapshotPort,
            actorPort,
            Options.Create(DefaultOptions));

    private static ScriptDefinitionSnapshot CreateScriptDefinitionSnapshot(
        string scriptId,
        string revision,
        string definitionActorId,
        ScriptMessageKind messageKind = ScriptMessageKind.Command) =>
        new(
            scriptId,
            revision,
            "return input;",
            "hash-script-1",
            "state",
            "readmodel",
            "v1",
            "hash-rm",
            RuntimeSemantics: new ScriptRuntimeSemanticsSpec
            {
                Messages =
                {
                    new ScriptMessageSemanticsSpec
                    {
                        TypeUrl = "type.googleapis.com/google.protobuf.StringValue",
                        DescriptorFullName = "google.protobuf.StringValue",
                        Kind = messageKind,
                    },
                },
            },
            DefinitionActorId: definitionActorId,
            ScopeId: ScopeId);

    private static string CreateScriptingArtifactHash(
        string revisionId,
        ScriptDefinitionSnapshot snapshot)
    {
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = new ServiceIdentity
            {
                TenantId = ScopeId,
                AppId = DefaultOptions.ServiceAppId,
                Namespace = DefaultOptions.ServiceNamespace,
                ServiceId = DefaultOptions.DefaultServiceId,
            },
            RevisionId = revisionId,
            ImplementationKind = ServiceImplementationKind.Scripting,
            ProtocolDescriptorSet = snapshot.ProtocolDescriptorSet,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                ScriptingPlan = new ScriptingServiceDeploymentPlan
                {
                    ScriptId = snapshot.ScriptId,
                    Revision = snapshot.Revision,
                    DefinitionActorId = snapshot.DefinitionActorId,
                    SourceHash = snapshot.SourceHash,
                    PackageSpec = ToServicePackage(snapshot.ScriptPackage),
                },
            },
        };
        artifact.Endpoints.Add(
            snapshot.RuntimeSemantics.Messages
                .Where(x => x.Kind == ScriptMessageKind.Command)
                .Select(x => new ServiceEndpointDescriptor
                {
                    EndpointId = string.IsNullOrWhiteSpace(x.DescriptorFullName)
                        ? x.TypeUrl ?? string.Empty
                        : x.DescriptorFullName,
                    DisplayName = string.IsNullOrWhiteSpace(x.DescriptorFullName)
                        ? x.TypeUrl ?? string.Empty
                        : x.DescriptorFullName,
                    Kind = ServiceEndpointKind.Command,
                    RequestTypeUrl = x.TypeUrl ?? string.Empty,
                    ResponseTypeUrl = string.Empty,
                    Description = $"Scripting command endpoint for {(string.IsNullOrWhiteSpace(x.DescriptorFullName) ? x.TypeUrl ?? string.Empty : x.DescriptorFullName)}.",
                }));
        return new PreparedServiceRevisionArtifactAssembler()
            .Assemble(artifact)
            .ArtifactHash;
    }

    private static ServiceSourcePackageSpec ToServicePackage(ScriptPackageSpec packageSpec)
    {
        var result = new ServiceSourcePackageSpec
        {
            EntryBehaviorTypeName = packageSpec.EntryBehaviorTypeName ?? string.Empty,
            EntrySourcePath = packageSpec.EntrySourcePath ?? string.Empty,
        };
        result.CsharpSources.Add(packageSpec.CsharpSources.Select(x => new ServicePackageFile
        {
            Path = x.Path ?? string.Empty,
            Content = x.Content ?? string.Empty,
        }));
        result.ProtoFiles.Add(packageSpec.ProtoFiles.Select(x => new ServicePackageFile
        {
            Path = x.Path ?? string.Empty,
            Content = x.Content ?? string.Empty,
        }));
        return result;
    }

    private sealed record CommandCall(string Method, object? Command);

    private sealed class RecordingServiceCommandPort : IServiceCommandPort
    {
        private static readonly ServiceCommandAcceptedReceipt DefaultReceipt =
            new("target-actor", "cmd-1", "correlation-1");

        public List<CommandCall> Calls { get; } = [];

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("CreateServiceAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("UpdateServiceAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("CreateRevisionAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("PrepareRevisionAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("PublishRevisionAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("SetDefaultServingRevisionAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add(new CommandCall("ActivateServiceRevisionAsync", command));
            return Task.FromResult(DefaultReceipt);
        }

        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(DeactivateServiceDeploymentCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(ReplaceServiceServingTargetsCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(StartServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(AdvanceServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);
    }

    private sealed class FakeServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        private readonly ServiceCatalogSnapshot? _getResult;
        private readonly ServiceRevisionCatalogSnapshot? _revisions;

        public FakeServiceLifecycleQueryPort(
            ServiceCatalogSnapshot? getResult,
            ServiceRevisionCatalogSnapshot? revisions = null)
        {
            _getResult = getResult;
            _revisions = revisions;
        }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(_getResult);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(_revisions);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);
    }

    private sealed class FakeWorkflowRunActorPort : IWorkflowRunActorPort
    {
        public Dictionary<string, WorkflowYamlParseResult> ParseResultsByYaml { get; } =
            new(StringComparer.Ordinal);

        public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task BindWorkflowDefinitionAsync(
            IActor actor,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
            string? scopeId = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task MarkStoppedAsync(
            string actorId,
            string runId,
            string reason,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default)
        {
            if (ParseResultsByYaml.TryGetValue(workflowYaml, out var parseResult))
                return Task.FromResult(parseResult);

            var line = (workflowYaml ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(static value => value.StartsWith("name:", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(line))
                return Task.FromResult(WorkflowYamlParseResult.Invalid("Workflow YAML is invalid."));

            var workflowName = line["name:".Length..].Trim();
            return Task.FromResult(
                string.IsNullOrWhiteSpace(workflowName)
                    ? WorkflowYamlParseResult.Invalid("Workflow YAML is invalid.")
                    : WorkflowYamlParseResult.Success(workflowName));
        }
    }

    private sealed class FakeScopeScriptQueryPort : IScopeScriptQueryPort
    {
        public ScopeScriptSummary? Script { get; set; }

        public Task<IReadOnlyList<ScopeScriptSummary>> ListAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeScriptSummary>>(Script == null ? [] : [Script]);

        public Task<ScopeScriptSummary?> GetByScriptIdAsync(string scopeId, string scriptId, CancellationToken ct = default) =>
            Task.FromResult(
                Script != null &&
                string.Equals(Script.ScopeId, scopeId, StringComparison.Ordinal) &&
                string.Equals(Script.ScriptId, scriptId, StringComparison.Ordinal)
                    ? Script
                    : null);
    }

    private sealed class FakeScriptDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
    {
        public ScriptDefinitionSnapshot? Snapshot { get; set; }

        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            if (Snapshot == null ||
                !string.Equals(Snapshot.DefinitionActorId, definitionActorId, StringComparison.Ordinal) ||
                !string.Equals(Snapshot.Revision, requestedRevision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Script definition snapshot was not found.");
            }

            return Task.FromResult(Snapshot);
        }
    }
}
