using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Bindings;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.Foundation.Abstractions;
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
            [
                "name: main\nsteps:\n  - run: echo hello",
                "name: child\nsteps:\n  - run: echo child",
            ]));

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
            [
                "name: root_flow\nsteps:\n  - run: echo root",
                "name: sub_a\nsteps:\n  - run: echo a",
                "name: sub_b\nsteps:\n  - run: echo b",
            ],
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
            ScopeBindingImplementationKind.Script,
            Script: new ScopeBindingScriptSpec("script-a"),
            DisplayName: "Orders Script"));

        commandPort.Calls.Should().HaveCount(6);
        var revisionCommand = commandPort.Calls[1].Command.Should().BeOfType<CreateServiceRevisionCommand>().Subject;
        revisionCommand.Spec.ImplementationKind.Should().Be(ServiceImplementationKind.Scripting);
        revisionCommand.Spec.ScriptingSpec.Should().NotBeNull();
        revisionCommand.Spec.ScriptingSpec!.ScriptId.Should().Be("script-a");
        revisionCommand.Spec.ScriptingSpec.Revision.Should().Be("script-rev-1");
        revisionCommand.Spec.ScriptingSpec.DefinitionActorId.Should().Be("definition-script-1");
        result.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Script);
        result.Script.Should().NotBeNull();
        result.Script!.ScriptId.Should().Be("script-a");
        result.Script.ScriptRevision.Should().Be("script-rev-1");
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
                typeof(TestStaticAgent).AssemblyQualifiedName!,
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
        revisionCommand.Spec.StaticSpec!.ActorTypeName.Should().Be(typeof(TestStaticAgent).AssemblyQualifiedName);
        revisionCommand.Spec.StaticSpec.PreferredActorId.Should().Be("gagent-orders");
        result.ImplementationKind.Should().Be(ScopeBindingImplementationKind.GAgent);
        result.GAgent.Should().NotBeNull();
        result.GAgent!.PreferredActorId.Should().Be("gagent-orders");
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
            [
                "name: main\nsteps:\n  - run: echo hello",
            ],
            DisplayName: "Orders App"));

        commandPort.Calls.Should().HaveCount(6);
        commandPort.Calls[0].Method.Should().Be("UpdateServiceAsync");
        commandPort.Calls.Should().NotContain(call => call.Method == "CreateServiceAsync");
        var updateCommand = commandPort.Calls[0].Command.Should().BeOfType<UpdateServiceDefinitionCommand>().Subject;
        updateCommand.Spec.DisplayName.Should().Be("Orders App");
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
            [
                "name: main\nsteps:\n  - run: echo hello",
            ]));

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
            [
                "name: main\nsteps:\n  - run: echo hello",
            ],
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
            [
                "name: repeat\nsteps:\n  - run: echo root",
                "name: repeat\nsteps:\n  - run: echo child",
            ]));

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
            [
                "name: main\nsteps:\n  - run: echo hello",
            ]));

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
            [
                "name: main\nsteps:\n  - run: echo hello",
                "   ",
            ]));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must not contain empty YAML entries*");
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
        string definitionActorId) =>
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
                        Kind = ScriptMessageKind.Command,
                    },
                },
            },
            DefinitionActorId: definitionActorId,
            ScopeId: ScopeId);

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

        public FakeServiceLifecycleQueryPort(ServiceCatalogSnapshot? getResult)
        {
            _getResult = getResult;
        }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(_getResult);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);
    }

    private sealed class FakeWorkflowRunActorPort : IWorkflowRunActorPort
    {
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

    private sealed class TestStaticAgent : AgentBase
    {
    }
}
