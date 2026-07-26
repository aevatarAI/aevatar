using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Workflows;
using FluentAssertions;
using Google.Protobuf;
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
        var dispatch = new RecordingActorDispatchPort();
        var admission = new RecordingWorkflowCapabilityAdmissionService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatch);
        services.AddSingleton<IWorkflowExternalCapabilityAdmissionService>(admission);
        services.AddWorkflowDefinitionFileSource();
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<FileBackedWorkflowCatalogPort>().MaterializeAsync(
        [
            new WorkflowDefinitionRegistration(
                "repo_install",
                "name: repo_install",
                "workflow-definition:repo_install",
                "repo"),
        ]);

        admission.Request.Should().NotBeNull();
        admission.Request!.WorkflowYaml.Should().Be("name: repo_install");
        admission.Request.SourceKind.Should().Be("repo");
        admission.Request.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
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
    }

    [Fact]
    public async Task MaterializeAsync_ShouldNotCreateActor_WhenAdmissionFails()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IActorDispatchPort>(dispatch);
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
                "repo"),
        ]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("not ready");
        runtime.Created.Should().BeEmpty();
        dispatch.Envelopes.Should().BeEmpty();
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
            var service = new WorkflowDefinitionBootstrapHostedService(
                registry,
                new WorkflowDefinitionFileLoader(),
                new FileBackedWorkflowCatalogPort(
                    new RecordingActorRuntime(),
                    new RecordingActorDispatchPort(),
                    new RecordingWorkflowCapabilityAdmissionService(),
                    NullLogger<FileBackedWorkflowCatalogPort>.Instance),
                Options.Create(options),
                NullLogger<WorkflowDefinitionBootstrapHostedService>.Instance);

            await service.StartAsync(CancellationToken.None);

            registry.GetYaml("review").Should().Contain("name: review");
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

    private sealed class RecordingWorkflowCapabilityAdmissionService(Exception? failure = null) :
        IWorkflowExternalCapabilityAdmissionService
    {
        public WorkflowExternalCapabilityAdmissionRequest? Request { get; private set; }

        public PersistedWorkflowCapabilityAdmissionRequest? PersistedRequest { get; private set; }

        public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            if (failure is not null)
                throw failure;
            return Task.FromResult(new WorkflowCapabilityAdmissionPlan
            {
                DefinitionDigest = "startup-definition-digest",
                AdmissionDigest = "startup-admission-digest",
                ExecutionMode = request.ExecutionMode,
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
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<(string ActorId, Type AgentType)> Created { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
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

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Envelopes.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
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
