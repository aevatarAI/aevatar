using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Configuration;
using Aevatar.Hosting;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Queries;
using Aevatar.Workflow.Application.Reporting;
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
    public void AddWorkflowCapabilityBundle_ShouldValidateBuilder_AndRegisterCapability()
    {
        Action act = () => WorkflowCapabilityHostBuilderExtensions.AddWorkflowCapabilityBundle(null!);
        act.Should().Throw<ArgumentNullException>();

        var builder = WebApplication.CreateBuilder();
        var returned = builder.AddWorkflowCapabilityBundle();

        returned.Should().BeSameAs(builder);
        builder.Services
            .Where(x => x.ServiceType == typeof(AevatarCapabilityRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AevatarCapabilityRegistration>()
            .Should()
            .Contain(x => x.Name == "workflow-bundle");
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
            TryDeleteDirectory(tempDir);
        }
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
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowActorBindingReader));
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

    [Fact]
    public async Task RegistryWorkflowDefinitionResolver_ShouldTrimLookup_AndReturnNullForBlank()
    {
        var registry = new WorkflowDefinitionRegistry();
        registry.Register("direct", "name: direct");
        var resolver = new RegistryWorkflowDefinitionResolver(registry);

        (await resolver.GetWorkflowYamlAsync(" direct ", CancellationToken.None)).Should().Contain("name: direct");
        (await resolver.GetWorkflowYamlAsync("   ", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task RegistryWorkflowDefinitionResolver_ShouldHonorCancellation()
    {
        var resolver = new RegistryWorkflowDefinitionResolver(new WorkflowDefinitionRegistry());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await resolver.GetWorkflowYamlAsync("direct", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FileSystemWorkflowExecutionReportArtifactSink_ShouldSkipWhenDisabled_AndWriteToConfiguredDirectory()
    {
        var disabledDir = Path.Combine(Path.GetTempPath(), "wf-report-disabled-" + Guid.NewGuid().ToString("N"));
        var enabledDir = Path.Combine(Path.GetTempPath(), "wf-report-enabled-" + Guid.NewGuid().ToString("N"));

        try
        {
            var disabledSink = new FileSystemWorkflowExecutionReportArtifactSink(
                Options.Create(new WorkflowExecutionReportArtifactOptions
                {
                    Enabled = false,
                    OutputDirectory = disabledDir,
                }),
                NullLogger<FileSystemWorkflowExecutionReportArtifactSink>.Instance);

            await disabledSink.PersistAsync(BuildReport(), CancellationToken.None);
            Directory.Exists(disabledDir).Should().BeFalse();

            var enabledSink = new FileSystemWorkflowExecutionReportArtifactSink(
                Options.Create(new WorkflowExecutionReportArtifactOptions
                {
                    Enabled = true,
                    OutputDirectory = enabledDir,
                }),
                NullLogger<FileSystemWorkflowExecutionReportArtifactSink>.Instance);

            await enabledSink.PersistAsync(BuildReport(), CancellationToken.None);

            Directory.Exists(enabledDir).Should().BeTrue();
            Directory.EnumerateFiles(enabledDir, "*.json").Should().ContainSingle();
            Directory.EnumerateFiles(enabledDir, "*.html").Should().ContainSingle();
            var jsonPath = Directory.EnumerateFiles(enabledDir, "*.json").Single();
            var json = await File.ReadAllTextAsync(jsonPath);
            json.Should().Contain("\"commandId\": \"cmd-1\"");
        }
        finally
        {
            TryDeleteDirectory(disabledDir);
            TryDeleteDirectory(enabledDir);
        }
    }

    [Fact]
    public async Task WorkflowDefinitionBootstrapHostedService_ShouldLoadConfiguredDirectories_AndHonorCancellation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wf-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "review.yaml"), "name: review");
            var registry = new WorkflowDefinitionRegistry();
            var options = new WorkflowDefinitionFileSourceOptions
            {
                DuplicatePolicy = WorkflowDefinitionDuplicatePolicy.Override,
            };
            options.WorkflowDirectories.Add(tempDir);
            var service = new WorkflowDefinitionBootstrapHostedService(
                registry,
                new WorkflowDefinitionFileLoader(),
                Options.Create(options),
                NullLogger<WorkflowDefinitionBootstrapHostedService>.Instance);

            await service.StartAsync(CancellationToken.None);

            registry.GetYaml("review").Should().Contain("name: review");
            await service.StopAsync(CancellationToken.None);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var act = async () => await service.StartAsync(cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
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

    private static WorkflowRunReport BuildReport()
    {
        var started = DateTimeOffset.UtcNow;
        return new WorkflowRunReport
        {
            WorkflowName = "workflow-report",
            RootActorId = "root-1",
            CommandId = "cmd-1",
            StartedAt = started,
            EndedAt = started.AddSeconds(1),
            DurationMs = 1000,
            Success = true,
            Input = "input",
            FinalOutput = "done",
            FinalError = "",
            Summary = new WorkflowRunStatistics
            {
                TotalSteps = 1,
                RequestedSteps = 1,
                CompletedSteps = 1,
                RoleReplyCount = 0,
            },
        };
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        Directory.Delete(path, recursive: true);
    }
}
