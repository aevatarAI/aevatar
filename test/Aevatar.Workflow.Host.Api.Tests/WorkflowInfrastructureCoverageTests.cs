using Aevatar.AI.Abstractions.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
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
        services.AddSingleton<IActorRuntime>(new FakeActorRuntime());
        services.AddSingleton<IActorTypeProbe, RuntimeBackedActorTypeProbe>();
        services.AddSingleton<IAgentTypeVerifier, DefaultAgentTypeVerifier>();
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

        runtime.ActorToReturn = new StubActor("actor-1", new StubAgent("agent-1"));
        var got = await port.GetAsync("actor-1", CancellationToken.None);
        got.Should().NotBeNull();
        runtime.GetCalls.Should().ContainSingle().Which.Should().Be("actor-1");

        runtime.ActorToCreate = new StubActor("definition-created", new StubAgent("agent-created"));
        var createdDefinition = await port.CreateDefinitionAsync(ct: CancellationToken.None);
        createdDefinition.Id.Should().Be("definition-created");
        runtime.LastGenericCreateType.Should().Be(typeof(WorkflowGAgent));

        var createdRunActor = new StubActor("run-created", new StubAgent("run-agent"));
        runtime.ActorToCreate = createdRunActor;
        var createdRun = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-created",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);
        createdRun.Id.Should().Be("run-created");
        runtime.LastGenericCreateType.Should().Be(typeof(WorkflowRunGAgent));
        createdRunActor.LastHandledEnvelope.Should().NotBeNull();
        createdRunActor.LastHandledEnvelope!.Payload.Should().NotBeNull();
        createdRunActor.LastHandledEnvelope.Payload!.Is(BindWorkflowRunDefinitionEvent.Descriptor).Should().BeTrue();

        var destroyAct = async () => await port.DestroyAsync(" ", CancellationToken.None);
        await destroyAct.Should().ThrowAsync<ArgumentException>();

        await port.DestroyAsync("actor-1", CancellationToken.None);
        runtime.DestroyCalls.Should().ContainSingle().Which.Should().Be("actor-1");

        var unknownActor = new StubActor("x", new StubAgent("a"));
        (await port.IsWorkflowDefinitionActorAsync(unknownActor, CancellationToken.None)).Should().BeFalse();
        (await port.IsWorkflowRunActorAsync(unknownActor, CancellationToken.None)).Should().BeFalse();
        (await port.GetBoundWorkflowNameAsync(unknownActor, CancellationToken.None)).Should().BeNull();
        (await port.DescribeAsync(unknownActor, CancellationToken.None)).ActorKind.Should().Be(WorkflowActorKind.Unsupported);

        var recordingActor = new StubActor("wf-actor", new StubAgent("wf-agent"));
        await port.BindWorkflowDefinitionAsync(recordingActor, "name: x", "x", null, CancellationToken.None);
        recordingActor.LastHandledEnvelope.Should().NotBeNull();
        recordingActor.LastHandledEnvelope!.Payload.Should().NotBeNull();
        recordingActor.LastHandledEnvelope.Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task WorkflowRunActorPort_WhenRuntimeTypeMatches_ShouldRecognizeWorkflowAgent()
    {
        var runtime = new FakeActorRuntime();
        var port = CreatePort(runtime);
        var actor = new StubActor(
            "wf-actor",
            new WorkflowGAgent());
        runtime.ActorToReturn = actor;

        (await port.IsWorkflowDefinitionActorAsync(actor, CancellationToken.None)).Should().BeTrue();
        (await port.GetBoundWorkflowNameAsync(actor, CancellationToken.None)).Should().BeNull();
        (await port.DescribeAsync(actor, CancellationToken.None)).ActorKind.Should().Be(WorkflowActorKind.Definition);
    }

    [Fact]
    public async Task WorkflowRunActorPort_WhenRuntimeTypeMatchesRunActor_ShouldRecognizeWorkflowRunAgent()
    {
        var runtime = new FakeActorRuntime();
        var port = CreatePort(runtime);
        var actor = new StubActor(
            "wf-run-actor",
            new WorkflowRunGAgent(
                runtime,
                new FakeRoleAgentTypeResolver(),
                new FakeStepExecutorFactory(),
                [new WorkflowCoreModulePack()]));
        runtime.ActorToReturn = actor;

        (await port.IsWorkflowRunActorAsync(actor, CancellationToken.None)).Should().BeTrue();
        (await port.DescribeAsync(actor, CancellationToken.None)).ActorKind.Should().Be(WorkflowActorKind.Run);
    }

    [Fact]
    public async Task WorkflowRunActorPort_WhenRuntimeTypeLooksSimilar_ShouldRejectWorkflowAgentMatch()
    {
        var runtime = new FakeActorRuntime();
        var port = CreatePort(runtime);
        var actor = new StubActor("wf-actor", new StubAgent("wf-agent"));

        (await port.IsWorkflowDefinitionActorAsync(actor, CancellationToken.None)).Should().BeFalse();
        (await port.IsWorkflowRunActorAsync(actor, CancellationToken.None)).Should().BeFalse();
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

    private static WorkflowRunActorPort CreatePort(FakeActorRuntime runtime)
    {
        var verifier = new DefaultAgentTypeVerifier(new RuntimeBackedActorTypeProbe(runtime));
        return new WorkflowRunActorPort(runtime, verifier, [new WorkflowCoreModulePack()]);
    }

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
        public IActor? ActorToReturn { get; set; }
        public IActor? ActorToCreate { get; set; }
        public Type? LastGenericCreateType { get; private set; }
        public List<string> GetCalls { get; } = [];
        public List<string> DestroyCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
        {
            _ = id;
            ct.ThrowIfCancellationRequested();
            LastGenericCreateType = typeof(TAgent);
            return Task.FromResult(ActorToCreate ?? new StubActor("new", new StubAgent("new-agent")));
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            _ = agentType;
            _ = id;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ActorToCreate ?? new StubActor("new", new StubAgent("new-agent")));
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

    private sealed class RuntimeBackedActorTypeProbe : IActorTypeProbe
    {
        private readonly IActorRuntime _runtime;

        public RuntimeBackedActorTypeProbe(IActorRuntime runtime)
        {
            _runtime = runtime;
        }

        public async Task<string?> GetRuntimeAgentTypeNameAsync(string actorId, CancellationToken ct = default)
        {
            _ = ct;
            var actor = await _runtime.GetAsync(actorId);
            var runtimeType = actor?.Agent.GetType();
            return runtimeType?.AssemblyQualifiedName ?? runtimeType?.FullName;
        }
    }
}
