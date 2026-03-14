using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Configuration;
using Aevatar.Hosting;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Workflows;
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
    public void AddWorkflowInfrastructure_ShouldReplaceReportSink_AndRegisterPorts()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkflowExecutionReportArtifactSink, FakeReportSink>();

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
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowActorBindingReader) &&
            x.ImplementationType == typeof(RuntimeWorkflowActorBindingReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowRunActorPort) &&
            x.ImplementationType == typeof(WorkflowRunActorPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowDefinitionResolver) &&
            x.ImplementationType == typeof(RegistryWorkflowDefinitionResolver));
    }

    [Fact]
    public void AddWorkflowDefinitionFileSource_ShouldRegisterLoaderAndHostedService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowDefinitionRegistry>(new WorkflowDefinitionRegistry());

        services.AddWorkflowDefinitionFileSource(options =>
        {
            options.WorkflowDirectories.Add("/tmp/a");
            options.WorkflowDirectories.Add("/tmp/b");
        });

        services.Should().Contain(x => x.ServiceType == typeof(FileBackedWorkflowCatalogPort));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowCatalogPort));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowCapabilitiesPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(WorkflowDefinitionBootstrapHostedService));

        using var provider = services.BuildServiceProvider();
        var catalogPort = provider.GetRequiredService<IWorkflowCatalogPort>();
        var capabilitiesPort = provider.GetRequiredService<IWorkflowCapabilitiesPort>();
        catalogPort.Should().BeOfType<FileBackedWorkflowCatalogPort>();
        capabilitiesPort.Should().BeOfType<FileBackedWorkflowCatalogPort>();
        catalogPort.Should().BeSameAs(capabilitiesPort);
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
    public void FileBackedWorkflowCatalogPort_ShouldReturnCatalogAndDetail()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aevatar-workflow-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var yamlPath = Path.Combine(tempDir, "repo_install.yaml");
            File.WriteAllText(yamlPath, """
            name: repo_install
            description: Bootstrap runtime.
            roles:
              - id: operator
                name: Operator
                system_prompt: ""
            steps:
              - id: bootstrap
                type: assign
                parameters:
                  target: result
                  value: "ok"
            """);

            var options = new WorkflowDefinitionFileSourceOptions();
            options.WorkflowDirectories.Add(tempDir);

            var registry = new WorkflowDefinitionRegistry();
            registry.Register("direct", WorkflowDefinitionRegistry.BuiltInDirectYaml);
            registry.Register("repo_install", File.ReadAllText(yamlPath));

            var port = new FileBackedWorkflowCatalogPort(registry, Options.Create(options));

            var catalog = port.ListWorkflowCatalog();
            catalog.Should().Contain(item => item.Name == "repo_install" && item.Source == "file");
            catalog.Should().Contain(item => item.Name == "direct" && item.Source == "builtin");

            var detail = port.GetWorkflowDetail("repo_install");
            detail.Should().NotBeNull();
            detail!.Catalog.Name.Should().Be("repo_install");
            detail.Catalog.RequiresLlmProvider.Should().BeFalse();
            detail.Definition.Description.Should().Be("Bootstrap runtime.");
            detail.Definition.Steps.Should().ContainSingle(step => step.Id == "bootstrap");

            var capabilities = port.GetCapabilities();
            capabilities.SchemaVersion.Should().Be("capabilities.v1");
            capabilities.Workflows.Should().Contain(item => item.Name == "repo_install");
            capabilities.Primitives.Should().Contain(item => item.Name == "assign");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
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
    public void AddWorkflowCapability_ShouldRegisterCapabilityAndValidateNull()
    {
        Action act = () => WorkflowCapabilityHostBuilderExtensions.AddWorkflowCapability(null!);
        act.Should().Throw<ArgumentNullException>();

        var builder = WebApplication.CreateBuilder();
        var returned = builder.AddWorkflowCapability();

        returned.Should().BeSameAs(builder);
        builder.Services
            .Where(x => x.ServiceType == typeof(AevatarCapabilityRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AevatarCapabilityRegistration>()
            .Should()
            .Contain(x => x.Name == "workflow");
    }

    [Fact]
    public void AddWorkflowCapabilityServices_ShouldRegisterInteractionAndDispatchPipeline()
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

        services.Should().Contain(x => x.ServiceType == typeof(ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>));
        services.Should().Contain(x => x.ServiceType == typeof(ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowExecutionQueryApplicationService));
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
        options.WorkflowDirectories.Should().Contain(AevatarPaths.RepoRootWorkflows);
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
}
