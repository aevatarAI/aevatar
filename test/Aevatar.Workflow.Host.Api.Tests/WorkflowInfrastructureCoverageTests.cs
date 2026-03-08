using Aevatar.AI.Abstractions.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Hosting;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Application.Queries;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Reporting;
using Aevatar.Workflow.Infrastructure.Runs;
using Aevatar.Workflow.Infrastructure.Workflows;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowInfrastructureCoverageTests
{
    [Fact]
    public async Task AddWorkflowInfrastructure_ShouldReplaceReportSink_AndRegisterActorPort()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowExecutionReportArtifactSink>(new FakeReportSink());
        services.AddLogging();
        var runtime = new FakeActorRuntime();
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IWorkflowActorBindingReader>(new RuntimeBackedWorkflowActorBindingReader(runtime));
        var registry = new WorkflowDefinitionRegistry();
        registry.Register("sub_flow", "name: sub_flow\nroles: []\nsteps: []\n");
        services.AddSingleton<IWorkflowDefinitionRegistry>(registry);

        services.AddWorkflowInfrastructure(options =>
        {
            options.Enabled = false;
            options.OutputDirectory = "/tmp/workflow-reports";
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WorkflowExecutionReportArtifactOptions>>().Value;
        options.Enabled.Should().BeFalse();
        options.OutputDirectory.Should().Be("/tmp/workflow-reports");

        provider.GetRequiredService<IWorkflowExecutionReportArtifactSink>()
            .Should().BeOfType<FileSystemWorkflowExecutionReportArtifactSink>();
        provider.GetRequiredService<IWorkflowRunActorPort>()
            .Should().BeOfType<WorkflowRunActorPort>();
        var resolver = provider.GetRequiredService<IWorkflowDefinitionResolver>();
        resolver.Should().NotBeNull();
        (await resolver.GetWorkflowYamlAsync("sub_flow")).Should().Contain("name: sub_flow");
    }

    [Fact]
    public void AddWorkflowDefinitionFileSource_ShouldRegisterLoaderAndHostedService()
    {
        var services = new ServiceCollection();

        services.AddWorkflowDefinitionFileSource(options =>
        {
            options.WorkflowDirectories.Add("/tmp/a");
            options.WorkflowDirectories.Add("/tmp/b");
        });

        services.Should().Contain(x =>
            x.ServiceType == typeof(WorkflowDefinitionFileLoader) &&
            x.ImplementationType == typeof(WorkflowDefinitionFileLoader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(WorkflowDefinitionBootstrapHostedService));
    }

    [Fact]
    public async Task WorkflowDefinitionBootstrapHostedService_ShouldLoadWorkflowFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aevatar-workflow-defs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var yamlPath = Path.Combine(tempDir, "demo.yaml");
            await File.WriteAllTextAsync(yamlPath, "name: demo\nroles: []\nsteps: []\n");

            var options = new WorkflowDefinitionFileSourceOptions();
            options.WorkflowDirectories.Add(tempDir);
            var registry = new WorkflowDefinitionRegistry();
            var service = new WorkflowDefinitionBootstrapHostedService(
                registry,
                new WorkflowDefinitionFileLoader(),
                Options.Create(options),
                NullLogger<WorkflowDefinitionBootstrapHostedService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);

            registry.GetYaml("demo").Should().Contain("name: demo");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDefinitionBootstrapHostedService_WhenCanceled_ShouldThrow()
    {
        var options = new WorkflowDefinitionFileSourceOptions();
        var registry = new WorkflowDefinitionRegistry();
        var service = new WorkflowDefinitionBootstrapHostedService(
            registry,
            new WorkflowDefinitionFileLoader(),
            Options.Create(options),
            NullLogger<WorkflowDefinitionBootstrapHostedService>.Instance);

        var act = async () => await service.StartAsync(new CancellationToken(canceled: true));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FileSystemWorkflowExecutionReportArtifactSink_ShouldRespectEnabledFlagAndWriteFiles()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "aevatar-report-sink-" + Guid.NewGuid().ToString("N"));
        var disabledDir = Path.Combine(baseDir, "disabled");
        var enabledDir = Path.Combine(baseDir, "enabled");
        Directory.CreateDirectory(baseDir);
        try
        {
            var report = new WorkflowRunReport
            {
                WorkflowName = "direct",
                RootActorId = "actor-1",
                CommandId = "cmd-1",
                StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                EndedAt = DateTimeOffset.UtcNow,
                DurationMs = 100,
                Success = true,
                Input = "hello",
                FinalOutput = "ok",
            };

            var disabledSink = new FileSystemWorkflowExecutionReportArtifactSink(
                Options.Create(new WorkflowExecutionReportArtifactOptions
                {
                    Enabled = false,
                    OutputDirectory = disabledDir,
                }),
                NullLogger<FileSystemWorkflowExecutionReportArtifactSink>.Instance);
            await disabledSink.PersistAsync(report, CancellationToken.None);
            Directory.Exists(disabledDir).Should().BeFalse();

            var enabledSink = new FileSystemWorkflowExecutionReportArtifactSink(
                Options.Create(new WorkflowExecutionReportArtifactOptions
                {
                    Enabled = true,
                    OutputDirectory = enabledDir,
                }),
                NullLogger<FileSystemWorkflowExecutionReportArtifactSink>.Instance);
            await enabledSink.PersistAsync(report, CancellationToken.None);

            var jsonFiles = Directory.GetFiles(enabledDir, "workflow-execution-*.json");
            var htmlFiles = Directory.GetFiles(enabledDir, "workflow-execution-*.html");
            jsonFiles.Should().NotBeEmpty();
            htmlFiles.Should().NotBeEmpty();
            var json = await File.ReadAllTextAsync(jsonFiles[0]);
            json.Should().Contain("\"commandId\": \"cmd-1\"");
        }
        finally
        {
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowRunActorPort_ShouldForwardRuntimeCalls_AndValidateArguments()
    {
        var runtime = new FakeActorRuntime();
        var port = CreatePort(runtime);

        runtime.ActorToCreate = new StubActor("definition-created", new StubAgent("agent-created"));
        var createdDefinition = await port.CreateDefinitionAsync(ct: CancellationToken.None);
        createdDefinition.Id.Should().Be("definition-created");
        runtime.LastGenericCreateType.Should().Be(typeof(WorkflowGAgent));

        runtime.StoredActors["definition-created"] = new StubActor("definition-created", new WorkflowGAgent());
        var createdRunActor = new StubActor("run-created", new StubAgent("run-agent"));
        runtime.ActorToCreate = createdRunActor;
        var createdRun = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-created",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);
        createdRun.Actor.Id.Should().Be("run-created");
        runtime.LastGenericCreateType.Should().Be(typeof(WorkflowRunGAgent));
        createdRunActor.LastHandledEnvelope.Should().NotBeNull();
        createdRunActor.LastHandledEnvelope!.Payload.Should().NotBeNull();
        createdRunActor.LastHandledEnvelope.Payload!.Is(BindWorkflowRunDefinitionEvent.Descriptor).Should().BeTrue();
        createdRun.CreatedActorIds.Should().Contain("run-created");

        var destroyAct = async () => await port.DestroyAsync(" ", CancellationToken.None);
        await destroyAct.Should().ThrowAsync<ArgumentException>();

        await port.DestroyAsync("actor-1", CancellationToken.None);
        runtime.DestroyCalls.Should().ContainSingle().Which.Should().Be("actor-1");

        var recordingActor = new StubActor("wf-actor", new StubAgent("wf-agent"));
        await port.BindWorkflowDefinitionAsync(recordingActor, "name: x", "x", null, CancellationToken.None);
        recordingActor.LastHandledEnvelope.Should().NotBeNull();
        recordingActor.LastHandledEnvelope!.Payload.Should().NotBeNull();
        recordingActor.LastHandledEnvelope.Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task WorkflowRunActorPort_WhenBindingReaderIdentifiesDefinitionActor_ShouldReuseExistingDefinition()
    {
        var runtime = new FakeActorRuntime();
        var existingDefinition = new StubActor("wf-actor", new StubAgent("proxy"));
        runtime.StoredActors["wf-actor"] = existingDefinition;
        runtime.ActorToCreate = new StubActor("run-created", new StubAgent("run-agent"));
        var port = CreatePort(
            runtime,
            new StaticWorkflowActorBindingReader(new Dictionary<string, WorkflowActorBinding?>
            {
                ["wf-actor"] = new WorkflowActorBinding(
                    WorkflowActorKind.Definition,
                    "wf-actor",
                    "wf-actor",
                    string.Empty,
                    "direct",
                    "name: direct\nroles: []\nsteps: []\n",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            }));

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "wf-actor",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("wf-actor");
        runtime.CreateRequests.Should().ContainSingle(x => x.AgentType == typeof(WorkflowRunGAgent));
    }

    [Fact]
    public async Task WorkflowRunActorPort_ParseWorkflowYaml_ShouldRejectUnknownStepTypes()
    {
        var runtime = new FakeActorRuntime();
        var port = CreatePort(runtime);

        var parseResult = await port.ParseWorkflowYamlAsync(
            """
            name: inline_unknown
            roles: []
            steps:
              - id: s1
                type: typo_unknown_step
            """,
            CancellationToken.None);

        parseResult.Succeeded.Should().BeFalse();
        parseResult.Error.Should().Contain("未知原语");
    }

    [Fact]
    public async Task WorkflowRunActorPort_ParseWorkflowYaml_ShouldAcceptKnownStepTypes()
    {
        var runtime = new FakeActorRuntime();
        var port = CreatePort(runtime);

        var parseResult = await port.ParseWorkflowYamlAsync(
            """
            name: inline_known
            roles: []
            steps:
              - id: s1
                type: transform
            """,
            CancellationToken.None);

        parseResult.Succeeded.Should().BeTrue();
        parseResult.WorkflowName.Should().Be("inline_known");
    }

    [Fact]
    public async Task WorkflowRunActorPort_WhenDefinitionActorIdBlank_ShouldCreateDefinitionWithNullPreferredId()
    {
        var runtime = new FakeActorRuntime();
        runtime.ActorsToCreate.Enqueue(new StubActor("definition-generated", new StubAgent("definition-generated")));
        runtime.ActorsToCreate.Enqueue(new StubActor("run-generated", new StubAgent("run-generated")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "   ",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-generated");
        runtime.CreateRequests.Should().ContainInOrder(
            (typeof(WorkflowGAgent), (string?)null),
            (typeof(WorkflowRunGAgent), (string?)null));
    }

    [Fact]
    public async Task WorkflowRunActorPort_WhenDefinitionActorIdRequestedAndMissing_ShouldPreserveRequestedId()
    {
        var runtime = new FakeActorRuntime();
        runtime.ActorsToCreate.Enqueue(new StubActor("definition-preferred", new StubAgent("definition-preferred")));
        runtime.ActorsToCreate.Enqueue(new StubActor("run-generated", new StubAgent("run-generated")));
        var port = CreatePort(runtime);

        _ = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-preferred",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        runtime.CreateRequests.Should().ContainInOrder(
            (typeof(WorkflowGAgent), "definition-preferred"),
            (typeof(WorkflowRunGAgent), (string?)null));
    }

    [Fact]
    public void AddWorkflowCapability_ShouldRegisterCapabilityAndValidateNull()
    {
        Action act = () => WorkflowCapabilityHostBuilderExtensions.AddWorkflowCapability(null!);
        act.Should().Throw<ArgumentNullException>();

        var builder = WebApplication.CreateBuilder();
        var returned = builder.AddWorkflowCapability();

        returned.Should().BeSameAs(builder);
        var registrations = builder.Services
            .Where(x => x.ServiceType == typeof(AevatarCapabilityRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AevatarCapabilityRegistration>()
            .ToList();
        registrations.Should().ContainSingle(x => x.Name == "workflow");
    }

    [Fact]
    public void AddWorkflowCapabilityServices_ShouldRegisterCoreWorkflowCapabilityComponents()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowExecutionProjection:Enabled"] = "true",
                ["WorkflowExecutionReportArtifacts:Enabled"] = "true",
            })
            .Build();

        services.AddWorkflowCapability(configuration);

        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowRunCommandService) &&
            x.ImplementationType == typeof(WorkflowChatRunApplicationService));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowExecutionQueryApplicationService) &&
            x.ImplementationType == typeof(WorkflowExecutionQueryApplicationService));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowExecutionReportArtifactSink) &&
            x.ImplementationType == typeof(FileSystemWorkflowExecutionReportArtifactSink));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(WorkflowDefinitionBootstrapHostedService));
    }

    [Fact]
    public void AddWorkflowCapabilityServices_ShouldSetFileSourceDuplicatePolicyToOverride()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().Build();

        services.AddWorkflowCapability(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WorkflowDefinitionFileSourceOptions>>().Value;
        options.DuplicatePolicy.Should().Be(WorkflowDefinitionDuplicatePolicy.Override);
    }

    private static WorkflowRunActorPort CreatePort(
        FakeActorRuntime runtime,
        IWorkflowActorBindingReader? bindingReader = null) =>
        new(runtime, bindingReader ?? new RuntimeBackedWorkflowActorBindingReader(runtime), [new WorkflowCoreModulePack()]);

    private sealed class FakeReportSink : IWorkflowExecutionReportArtifactSink
    {
        public Task PersistAsync(WorkflowRunReport report, CancellationToken ct = default)
        {
            _ = report;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeActorRuntime : IActorRuntime
    {
        public Dictionary<string, IActor> StoredActors { get; } = new(StringComparer.Ordinal);
        public IActor? ActorToReturn { get; set; }
        public IActor? ActorToCreate { get; set; }
        public Type? LastGenericCreateType { get; private set; }
        public List<string> GetCalls { get; } = [];
        public List<string> DestroyCalls { get; } = [];
        public Queue<IActor> ActorsToCreate { get; } = [];
        public List<(Type AgentType, string? RequestedId)> CreateRequests { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
        {
            ct.ThrowIfCancellationRequested();
            LastGenericCreateType = typeof(TAgent);
            CreateRequests.Add((typeof(TAgent), id));
            return Task.FromResult(ResolveCreatedActor(id));
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastGenericCreateType = agentType;
            CreateRequests.Add((agentType, id));
            return Task.FromResult(ResolveCreatedActor(id));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DestroyCalls.Add(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            GetCalls.Add(id);
            if (StoredActors.TryGetValue(id, out var actor))
                return Task.FromResult<IActor?>(actor);

            return Task.FromResult(ActorToReturn);
        }

        public Task<bool> ExistsAsync(string id)
        {
            _ = id;
            return Task.FromResult(false);
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            _ = parentId;
            _ = childId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            _ = childId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private IActor ResolveCreatedActor(string? id)
        {
            if (ActorsToCreate.Count > 0)
                return ActorsToCreate.Dequeue();

            if (ActorToCreate != null)
                return ActorToCreate;

            return new StubActor(id ?? "new", new StubAgent("new-agent"));
        }

    }

    private sealed class StubActor : IActor
    {
        public StubActor(string id, IAgent agent)
        {
            Id = id;
            Agent = agent;
        }

        public string Id { get; }
        public IAgent Agent { get; }
        public EventEnvelope? LastHandledEnvelope { get; private set; }
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            LastHandledEnvelope = envelope;
            return Task.CompletedTask;
        }
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeRoleAgentTypeResolver : IRoleAgentTypeResolver
    {
        public Type ResolveRoleAgentType() => typeof(StubAgent);
    }

    private sealed class FakeStepExecutorFactory : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
        {
            _ = name;
            module = null;
            return false;
        }
    }

    private sealed class EmptyWorkflowModulePack : IWorkflowModulePack
    {
        public string Name => "empty";
        public IReadOnlyList<WorkflowModuleRegistration> Modules { get; } = [];
        public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders { get; } = [];
        public IReadOnlyList<IWorkflowModuleConfigurator> Configurators { get; } = [];
    }

    private sealed class RuntimeBackedWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        private readonly IActorRuntime _runtime;

        public RuntimeBackedWorkflowActorBindingReader(IActorRuntime runtime)
        {
            _runtime = runtime;
        }

        public async Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            _ = ct;
            var actor = await _runtime.GetAsync(actorId);
            if (actor == null)
                return null;

            return actor.Agent switch
            {
                WorkflowGAgent definition => new WorkflowActorBinding(
                    WorkflowActorKind.Definition,
                    actor.Id,
                    actor.Id,
                    string.Empty,
                    definition.State.WorkflowName ?? string.Empty,
                    definition.State.WorkflowYaml ?? string.Empty,
                    new Dictionary<string, string>(definition.State.InlineWorkflowYamls, StringComparer.OrdinalIgnoreCase)),
                WorkflowRunGAgent run => new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    actor.Id,
                    run.State.DefinitionActorId ?? string.Empty,
                    run.State.RunId ?? string.Empty,
                    run.State.WorkflowName ?? string.Empty,
                    run.State.WorkflowYaml ?? string.Empty,
                    new Dictionary<string, string>(run.State.InlineWorkflowYamls, StringComparer.OrdinalIgnoreCase)),
                _ => WorkflowActorBinding.Unsupported(actor.Id),
            };
        }
    }

    private sealed class StaticWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        private readonly IReadOnlyDictionary<string, WorkflowActorBinding?> _bindings;

        public StaticWorkflowActorBindingReader(IReadOnlyDictionary<string, WorkflowActorBinding?> bindings)
        {
            _bindings = bindings;
        }

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            _ = ct;
            _bindings.TryGetValue(actorId, out var binding);
            return Task.FromResult(binding);
        }
    }
}
