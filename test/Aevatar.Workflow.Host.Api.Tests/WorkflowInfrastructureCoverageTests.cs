using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Configuration;
using Aevatar.Hosting;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Application.Queries;
using Aevatar.Workflow.Application.Reporting;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Infrastructure.Capabilities;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Reporting;
using Aevatar.Workflow.Infrastructure.Runs;
using Aevatar.Workflow.Infrastructure.Workflows;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Workflows;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Core.Modules;
using ApplicationWorkflowFileRef = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileRef;
using ApplicationWorkflowFileSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileSourceKind;
using ProtoWorkflowFileRef = Aevatar.Workflow.Abstractions.WorkflowFileRef;
using ProtoWorkflowFileSourceKind = Aevatar.Workflow.Abstractions.WorkflowFileSourceKind;

namespace Aevatar.Workflow.Host.Api.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
public sealed class WorkflowInfrastructureCoverageTests
{
    [Fact]
    public async Task AddWorkflowInfrastructure_ShouldReplaceReportSink_AndRegisterPorts()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkflowRunReportExportPort, FakeReportExporter>();

        services.AddWorkflowInfrastructure(options =>
        {
            options.Enabled = false;
            options.OutputDirectory = "/tmp/workflow-reports";
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WorkflowRunReportExportOptions>>().Value;

        options.Enabled.Should().BeFalse();
        options.OutputDirectory.Should().Be("/tmp/workflow-reports");
        provider.GetRequiredService<IWorkflowRunReportExportPort>()
            .Should().BeOfType<FileSystemWorkflowRunReportExporter>();
        provider.GetRequiredService<IWorkflowFileIngressPort>()
            .Should().BeOfType<FileSystemWorkflowFileIngressPort>();
        provider.GetRequiredService<IWorkflowFileArtifactReadPort>()
            .Should().BeSameAs(provider.GetRequiredService<IWorkflowFileIngressPort>());
        var toolNames = new List<string>();
        foreach (var toolSource in provider.GetServices<IWorkflowToolSource>())
        {
            var tools = await toolSource.GetToolsAsync();
            toolNames.AddRange(tools.Select(x => x.Name));
        }

        toolNames.Should().Contain("document_extract");
        services.Should().Contain(x =>
            x.ServiceType == typeof(WorkflowRunActorPort) &&
            x.ImplementationType == typeof(WorkflowRunActorPort));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowDefinitionProvisioningPort));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowRunProvisioningPort));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowDefinitionParser));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowDefinitionResolver) &&
            x.ImplementationType == typeof(RegistryWorkflowDefinitionResolver));
    }

    [Fact]
    public void AddWorkflowDefinitionFileSource_ShouldRegisterLoaderAndHostedService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowDefinitionCatalog>(new WorkflowDefinitionCatalog());

        services.AddWorkflowDefinitionFileSource(options =>
        {
            options.WorkflowDirectories.Add("/tmp/a");
            options.WorkflowDirectories.Add("/tmp/b");
        });

        services.Should().Contain(x => x.ServiceType == typeof(FileBackedWorkflowCatalogPort));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowCatalogPort));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowCapabilitiesPort));
        services.Should().Contain(x => x.ImplementationFactory != null &&
            x.ServiceType == typeof(IWorkflowCatalogPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(WorkflowDefinitionBootstrapHostedService));
    }

    [Fact]
    public void AddWorkflowCapabilityServices_ShouldRegisterRealtimeEntryPointWithoutGenericBypass()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddWorkflowCapability(configuration);

        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowChatRunInteractionPort));
        services.Should().NotContain(x =>
            x.ServiceType == typeof(ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>));
        services.Should().Contain(x => x.ServiceType == typeof(ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>));
    }

    [Fact]
    public void MapWorkflowCapabilityEndpoints_WhenScheduleDependenciesAreMissing_ShouldSkipScheduleRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddLogging();
        builder.Services.AddWorkflowCapability(new ConfigurationBuilder().Build());
        var app = builder.Build();

        app.MapWorkflowCapabilityEndpoints();

        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .Should()
            .NotContain(route => route != null && route.Contains("workflow-schedules", StringComparison.Ordinal));
    }

    [Fact]
    public void MapWorkflowCapabilityEndpoints_WhenScheduleDependenciesAreRegistered_ShouldMapScheduleRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddLogging();
        builder.Services.AddGAgentServiceCapability(new ConfigurationBuilder().Build());
        var app = builder.Build();

        app.MapWorkflowCapabilityEndpoints();

        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .Should()
            .Contain(route => route != null && route.Contains("workflow-schedules", StringComparison.Ordinal));
    }

    [Fact]
    public void AddScheduledDispatchCapability_ShouldSupplyWorkflowScheduleDependenciesWithoutFullCapability()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActorRuntime, RecordingActorRuntime>();
        services.AddSingleton<IActorDispatchPort, RecordingActorDispatchPort>();
        services.AddSingleton<IScriptRuntimeCommandPort, RecordingScriptRuntimeCommandPort>();
        services.AddSingleton<IWorkflowRunProvisioningPort, RecordingWorkflowRunProvisioningPort>();

        services.AddScheduledDispatchCapability(new ConfigurationBuilder().Build());
        services.AddWorkflowCapability(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        provider.GetRequiredService<IWorkflowScheduleApplicationService>().Should().NotBeNull();
        provider.GetRequiredService<IScheduledServiceInvocationDispatchPort>().Should().NotBeNull();
        provider.GetRequiredService<IServiceInvocationPort>().Should().NotBeNull();
        services.Should().NotContain(x => x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType != null &&
            x.ImplementationType.Name.Contains("GAgentServiceDemo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileBackedWorkflowCatalogPort_ShouldMaterializeStartupDefinitions()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var port = new FileBackedWorkflowCatalogPort(
            runtime,
            dispatch,
            NullLogger<FileBackedWorkflowCatalogPort>.Instance);

        await port.MaterializeAsync(
        [
            new WorkflowDefinitionRegistration(
                "repo_install",
                "name: repo_install",
                "workflow-definition:repo_install",
                "repo"),
        ]);

        runtime.Created.Should().ContainSingle(x => x.ActorId == "workflow-definition:repo_install" && x.AgentType == typeof(Aevatar.Workflow.Core.WorkflowGAgent));
        dispatch.Envelopes.Should().ContainSingle();
        var request = dispatch.Envelopes[0].Envelope.Payload!.Unpack<Aevatar.Workflow.Abstractions.BindWorkflowDefinitionEvent>();
        request.WorkflowName.Should().Be("repo_install");
        request.WorkflowYaml.Should().Be("name: repo_install");
        request.SourceKind.Should().Be("repo");
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
                ["WorkflowRunReportExport:Enabled"] = "true",
            })
            .Build();

        services.AddWorkflowCapability(configuration);

        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowChatRunInteractionPort));
        services.Should().NotContain(x => x.ServiceType == typeof(ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>));
        services.Should().Contain(x => x.ServiceType == typeof(ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowExecutionQueryApplicationService));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowActorBindingReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowRunReportExportPort) &&
            x.ImplementationType == typeof(FileSystemWorkflowRunReportExporter));
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
        options.WorkflowDirectories.Should().NotContain(
            Path.Combine(AevatarPaths.RepoRoot, "workflows", "turing-completeness"));
    }

    [Fact]
    public void AddWorkflowCapabilityServices_ShouldNotLoadRemovedRepositoryExamplesIntoGenericHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().Build();

        services.AddWorkflowCapability(configuration);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowDefinitionCatalog>();
        var loader = provider.GetRequiredService<WorkflowDefinitionFileLoader>();
        var options = provider.GetRequiredService<IOptions<WorkflowDefinitionFileSourceOptions>>().Value;

        loader.LoadInto(
            registry,
            options.WorkflowDirectories,
            NullLogger.Instance,
            options.DuplicatePolicy);

        registry.GetYaml("direct").Should().NotBeNull();
        registry.GetYaml("demo_template").Should().BeNull();
    }

    [Fact]
    public async Task RegistryWorkflowDefinitionResolver_ShouldTrimLookup_AndReturnNullForBlank()
    {
        var registry = new WorkflowDefinitionCatalog();
        registry.Register("direct", "name: direct");
        var resolver = new RegistryWorkflowDefinitionResolver(registry);

        (await resolver.GetWorkflowYamlAsync(" direct ", CancellationToken.None)).Should().Contain("name: direct");
        (await resolver.GetWorkflowYamlAsync("   ", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task RegistryWorkflowDefinitionResolver_ShouldHonorCancellation()
    {
        var resolver = new RegistryWorkflowDefinitionResolver(new WorkflowDefinitionCatalog());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await resolver.GetWorkflowYamlAsync("direct", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FileSystemWorkflowRunReportExporter_ShouldSkipWhenDisabled_AndWriteToConfiguredDirectory()
    {
        var disabledDir = Path.Combine(Path.GetTempPath(), "wf-report-disabled-" + Guid.NewGuid().ToString("N"));
        var enabledDir = Path.Combine(Path.GetTempPath(), "wf-report-enabled-" + Guid.NewGuid().ToString("N"));

        try
        {
            var disabledExporter = new FileSystemWorkflowRunReportExporter(
                Options.Create(new WorkflowRunReportExportOptions
                {
                    Enabled = false,
                    OutputDirectory = disabledDir,
                }),
                NullLogger<FileSystemWorkflowRunReportExporter>.Instance);

            await disabledExporter.ExportAsync(BuildReport(), CancellationToken.None);
            Directory.Exists(disabledDir).Should().BeFalse();

            var enabledExporter = new FileSystemWorkflowRunReportExporter(
                Options.Create(new WorkflowRunReportExportOptions
                {
                    Enabled = true,
                    OutputDirectory = enabledDir,
                }),
                NullLogger<FileSystemWorkflowRunReportExporter>.Instance);

            await enabledExporter.ExportAsync(BuildReport(), CancellationToken.None);

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
                    NullLogger<FileBackedWorkflowCatalogPort>.Instance),
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

    [Fact]
    public async Task WorkflowInfrastructureCapabilitiesProvider_ShouldComposePrimitiveConnectorAndWorkflowCapabilities()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "wf-capabilities-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);
        var previousHome = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
        Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, tempHome);
        try
        {
            await File.WriteAllTextAsync(
                AevatarPaths.ConnectorsJson,
                """
                {
                  "connectors": [
                    {
                      "name": "http_news",
                      "type": " HTTP ",
                      "enabled": true,
                      "timeoutMs": 5000,
                      "retry": 2,
                      "http": {
                        "allowedInputKeys": [" query ", "query", "", "limit"],
                        "allowedMethods": ["POST", "get", "GET"]
                      }
                    },
                    {
                      "name": "cli_runner",
                      "type": "cli",
                      "enabled": true,
                      "cli": {
                        "allowedInputKeys": ["prompt"],
                        "allowedOperations": ["run", "RUN"],
                        "fixedArguments": [" --json ", "--json", ""]
                      }
                    },
                    {
                      "name": "mcp_tools",
                      "type": "mcp",
                      "enabled": true,
                      "mcp": {
                        "allowedInputKeys": ["topic"],
                        "allowedTools": ["search", ""],
                        "defaultTool": "search"
                      }
                    },
                    {
                      "name": "custom_sink",
                      "type": "custom",
                      "enabled": true
                    }
                  ]
                }
                """);
            var updatedAt = DateTimeOffset.Parse("2026-03-17T12:00:00+00:00");
            var catalogPort = new WorkflowCatalogReadModelQueryPort(
                new RecordingDocumentReader<WorkflowCatalogCurrentStateDocument>
                {
                    Items =
                    [
                        new WorkflowCatalogCurrentStateDocument
                        {
                            Id = "alpha",
                            WorkflowName = "alpha",
                            Source = "builtin",
                            UpdatedAt = updatedAt,
                            StateVersion = 7,
                            Primitives = ["assign"],
                        },
                    ],
                },
                new WorkflowCatalogReadModelMapper());
            var provider = new WorkflowInfrastructureCapabilitiesProvider(
                [new CustomModulePack()],
                catalogPort,
                new WorkflowCatalogReadModelMapper());

            var document = await provider.GetCapabilitiesAsync(CancellationToken.None);

            document.GeneratedAtUtc.Should().BeAfter(DateTimeOffset.MinValue);
            document.ProjectionWatermark.Should().BeOnOrAfter(updatedAt);
            document.Workflows.Should().ContainSingle(workflow =>
                workflow.Name == "alpha" &&
                workflow.AuthorityStateVersion == 7);
            var primitiveNames = document.Primitives.Select(primitive => primitive.Name).ToList();
            primitiveNames.Should().Contain("connector_call");
            primitiveNames.Should().Contain("llm_call");
            primitiveNames.Should().Contain("tool_call");
            document.Primitives.Should().Contain(primitive =>
                primitive.Name == "custom_assign" &&
                primitive.Aliases.Contains("custom_assign") &&
                primitive.RuntimeModule == nameof(CustomAssignModule));
            typeof(WorkflowPrimitiveCapability)
                .GetProperty("ClosedWorldBlocked")
                .Should().BeNull();
            document.Connectors.Select(connector => connector.Name)
                .Should().Equal("cli_runner", "custom_sink", "http_news", "mcp_tools");
            document.Connectors.Single(connector => connector.Name == "http_news")
                .AllowedInputKeys.Should().Equal("limit", "query");
            document.Connectors.Single(connector => connector.Name == "http_news")
                .AllowedOperations.Should().Equal("get", "POST");
            document.Connectors.Single(connector => connector.Name == "cli_runner")
                .FixedArguments.Should().Equal("--json");
            document.Connectors.Single(connector => connector.Name == "mcp_tools")
                .AllowedOperations.Should().Equal("search");
            document.Connectors.Single(connector => connector.Name == "custom_sink")
                .AllowedOperations.Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, previousHome);
            TryDeleteDirectory(tempHome);
        }
    }

    [Fact]
    public void WorkflowProjectionTransportProto_ShouldNotExposeStartupCapabilityArtifactMessages()
    {
        // Refactor (iter161-cluster-001 #1257-first):
        //   Old pattern: proto kept WorkflowCapabilitiesStartupArtifact and capability entry messages as unused typed artifact surface.
        //   New principle: no persisted capability artifact exists until a durable consumer contract is designed.
        var messageNames = WorkflowCatalogCurrentStateDocument.Descriptor.File.MessageTypes
            .Select(message => message.Name);

        messageNames.Should().NotContain([
            "WorkflowCapabilitiesStartupArtifact",
            "WorkflowPrimitiveCapabilityReadModel",
            "WorkflowPrimitiveParameterCapabilityReadModel",
            "WorkflowConnectorCapabilityReadModel"
        ]);
    }

    [Fact]
    public async Task WorkflowInfrastructureCapabilitiesProvider_ShouldHonorCancellation()
    {
        var provider = new WorkflowInfrastructureCapabilitiesProvider(
            [],
            new WorkflowCatalogReadModelQueryPort(
                new RecordingDocumentReader<WorkflowCatalogCurrentStateDocument>(),
                new WorkflowCatalogReadModelMapper()),
            new WorkflowCatalogReadModelMapper());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await provider.GetCapabilitiesAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldFallbackToSingleInputFileRef()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-input-ref-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("single input file"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "single.txt",
                MediaType: "text/plain"));
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                "{}",
                InputFileRefs: [ToProtoWorkflowFileRef(result.FileRef)]));

            using var document = JsonDocument.Parse(output);
            document.RootElement.GetProperty("text").GetString().Should().Be("single input file");
            document.RootElement.GetProperty("file").GetProperty("file_id").GetString().Should().Be(result.FileRef.FileId);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldPreferExplicitFileRefOverInputFileRefs()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-explicit-ref-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var explicitFile = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("explicit file"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "explicit.txt",
                MediaType: "text/plain"));
            var inputFile = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("input file"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "input.txt",
                MediaType: "text/plain"));
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(explicitFile.FileRef),
                InputFileRefs: [ToProtoWorkflowFileRef(inputFile.FileRef)]));

            using var document = JsonDocument.Parse(output);
            document.RootElement.GetProperty("text").GetString().Should().Be("explicit file");
            document.RootElement.GetProperty("file").GetProperty("file_id").GetString().Should().Be(explicitFile.FileRef.FileId);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldFailClosedWhenInputFileRefsAreAmbiguous()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-ambiguous-ref-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var first = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("first"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "first.txt",
                MediaType: "text/plain"));
            var second = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("second"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "second.txt",
                MediaType: "text/plain"));
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                "{}",
                InputFileRefs: [ToProtoWorkflowFileRef(first.FileRef), ToProtoWorkflowFileRef(second.FileRef)]));

            using var document = JsonDocument.Parse(output);
            document.RootElement.GetProperty("error").GetString().Should().Be("invalid_arguments");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("multiple input file refs");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static FileSystemWorkflowFileIngressPort CreateFileArtifactPort(string root) =>
        new(Options.Create(new FileSystemWorkflowFileIngressOptions
        {
            RootDirectory = root,
        }));

    private static async Task<IWorkflowTool> GetDocumentExtractToolAsync(IWorkflowFileArtifactReadPort port)
    {
        var source = new WorkflowDocumentExtractToolSource(port);
        var tools = await source.GetToolsAsync();
        return tools.Should().ContainSingle(tool => tool.Name == "document_extract").Subject;
    }

    private static string BuildDocumentExtractArguments(
        ApplicationWorkflowFileRef fileRef,
        int? maxChars = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["fileRef"] = new Dictionary<string, object?>
            {
                ["fileId"] = fileRef.FileId,
                ["artifactId"] = fileRef.ArtifactId,
                ["sourceKind"] = fileRef.SourceKind.ToString(),
                ["sourceMessageId"] = fileRef.SourceMessageId,
                ["sourceResourceKey"] = fileRef.SourceResourceKey,
                ["fileName"] = fileRef.FileName,
                ["mediaType"] = fileRef.MediaType,
                ["sizeBytes"] = fileRef.SizeBytes,
                ["sha256"] = fileRef.Sha256,
                ["createdAtUnixMs"] = fileRef.CreatedAtUnixMs,
                ["expiresAtUnixMs"] = fileRef.ExpiresAtUnixMs,
                ["ownerRunId"] = fileRef.OwnerRunId,
                ["ownerScopeId"] = fileRef.OwnerScopeId,
            },
        };
        if (maxChars.HasValue)
            payload["maxChars"] = maxChars.Value;

        return JsonSerializer.Serialize(payload);
    }

    private static ProtoWorkflowFileRef ToProtoWorkflowFileRef(ApplicationWorkflowFileRef source) =>
        new()
        {
            FileId = source.FileId ?? string.Empty,
            ArtifactId = source.ArtifactId ?? string.Empty,
            SourceKind = source.SourceKind switch
            {
                ApplicationWorkflowFileSourceKind.ChatInput => ProtoWorkflowFileSourceKind.ChatInput,
                ApplicationWorkflowFileSourceKind.FormUpload => ProtoWorkflowFileSourceKind.FormUpload,
                ApplicationWorkflowFileSourceKind.ConnectedServiceResource =>
                    ProtoWorkflowFileSourceKind.ConnectedServiceResource,
                ApplicationWorkflowFileSourceKind.ExternalResource => ProtoWorkflowFileSourceKind.ExternalResource,
                ApplicationWorkflowFileSourceKind.Generated => ProtoWorkflowFileSourceKind.Generated,
                _ => ProtoWorkflowFileSourceKind.Unspecified,
            },
            SourceMessageId = source.SourceMessageId ?? string.Empty,
            SourceResourceKey = source.SourceResourceKey ?? string.Empty,
            FileName = source.FileName ?? string.Empty,
            MediaType = source.MediaType ?? string.Empty,
            SizeBytes = source.SizeBytes,
            Sha256 = source.Sha256 ?? string.Empty,
            CreatedAtUnixMs = source.CreatedAtUnixMs,
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
            OwnerRunId = source.OwnerRunId ?? string.Empty,
            OwnerScopeId = source.OwnerScopeId ?? string.Empty,
        };

    private sealed class FakeReportExporter : IWorkflowRunReportExportPort
    {
        public Task ExportAsync(WorkflowRunReport report, CancellationToken ct = default)
        {
            _ = report;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
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

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Envelopes.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingScriptRuntimeCommandPort : IScriptRuntimeCommandPort
    {
        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Google.Protobuf.WellKnownTypes.Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorkflowRunProvisioningPort : IWorkflowRunProvisioningPort
    {
        public Task<WorkflowRunCreationReceipt> CreateRunAsync(
            WorkflowDefinitionBinding definition,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new WorkflowRunCreationReceipt("workflow-run-1", "definition-1", []));
        }

        public Task DestroyAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id)
        {
            Id = id;
        }

        public string Id { get; }
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingDocumentReader<TReadModel> : IProjectionDocumentReader<TReadModel, string>
        where TReadModel : class, IProjectionReadModel
    {
        public IReadOnlyList<TReadModel> Items { get; init; } = [];

        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            _ = key;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<TReadModel?>(null);
        }

        public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            _ = query;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectionDocumentQueryResult<TReadModel>
            {
                Items = Items,
            });
        }
    }

    private sealed class CustomModulePack : IWorkflowModulePack
    {
        public string Name => "custom";
        public IReadOnlyList<WorkflowModuleRegistration> Modules =>
        [
            WorkflowModuleRegistration.Create<CustomAssignModule>("custom_assign", "   "),
        ];
        public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders => [];
        public IReadOnlyList<IWorkflowModuleConfigurator> Configurators => [];
    }

    private sealed class CustomAssignModule : IEventModule<IWorkflowExecutionContext>
    {
        public string Name => "custom_assign";
        public int Priority => 0;
        public bool CanHandle(EventEnvelope envelope) => false;
        public Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct) =>
            Task.CompletedTask;
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
