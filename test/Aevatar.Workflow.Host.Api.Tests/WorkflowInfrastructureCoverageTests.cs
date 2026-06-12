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
using System.Security.Cryptography;
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
    public async Task FileSystemWorkflowFileIngressPort_ShouldRoundTripDescriptorAndContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-file-ingress-roundtrip-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var content = Encoding.UTF8.GetBytes("descriptor text");
            var expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();

            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                content,
                ApplicationWorkflowFileSourceKind.FormUpload,
                SourceMessageId: " message-1 ",
                SourceResourceKey: " invoice ",
                FileName: " input.txt ",
                MediaType: " text/plain ",
                ExpiresAtUnixMs: expiresAt,
                OwnerRunId: " run-1 ",
                OwnerScopeId: " scope-1 "));

            var descriptor = await port.DescribeAsync(new ApplicationWorkflowFileRef
            {
                ArtifactId = result.FileRef.ArtifactId,
            });
            var artifact = await port.OpenReadAsync(new ApplicationWorkflowFileRef
            {
                FileId = result.FileRef.FileId,
                Sha256 = result.FileRef.Sha256,
                SizeBytes = content.Length,
            });
            await using var stream = artifact.Content;
            using var reader = new StreamReader(stream, Encoding.UTF8);

            descriptor.FileId.Should().Be(result.FileRef.FileId);
            descriptor.ArtifactId.Should().Be($"workflow-file://{result.FileRef.FileId}");
            descriptor.SourceKind.Should().Be(ApplicationWorkflowFileSourceKind.FormUpload);
            descriptor.SourceMessageId.Should().Be("message-1");
            descriptor.SourceResourceKey.Should().Be("invoice");
            descriptor.FileName.Should().Be("input.txt");
            descriptor.MediaType.Should().Be("text/plain");
            descriptor.SizeBytes.Should().Be(content.Length);
            descriptor.Sha256.Should().Be(ExpectedSha256(content));
            descriptor.ExpiresAtUnixMs.Should().Be(expiresAt);
            descriptor.OwnerRunId.Should().Be("run-1");
            descriptor.OwnerScopeId.Should().Be("scope-1");
            artifact.FileRef.Should().BeEquivalentTo(descriptor);
            (await reader.ReadToEndAsync()).Should().Be("descriptor text");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task FileSystemWorkflowFileIngressPort_ShouldRejectInvalidDescriptorRequests()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-file-ingress-validation-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("validated"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "validated.txt",
                MediaType: "text/plain"));
            var expired = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("expired"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "expired.txt",
                MediaType: "text/plain",
                ExpiresAtUnixMs: DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()));

            var invalidArtifact = async () => await port.DescribeAsync(result.FileRef with
            {
                ArtifactId = "https://files.example/not-managed",
            });
            var escapedFileId = async () => await port.DescribeAsync(new ApplicationWorkflowFileRef
            {
                FileId = "wf-file-../escape",
            });
            var mismatchedId = async () => await port.DescribeAsync(result.FileRef with
            {
                FileId = "wf-file-mismatch",
            });
            var mismatchedHash = async () => await port.DescribeAsync(result.FileRef with
            {
                Sha256 = "deadbeef",
            });
            var mismatchedSize = async () => await port.DescribeAsync(result.FileRef with
            {
                SizeBytes = result.FileRef.SizeBytes + 1,
            });
            var expiredArtifact = async () => await port.DescribeAsync(expired.FileRef);

            await invalidArtifact.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*not managed*");
            await escapedFileId.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*file id is invalid*");
            await mismatchedId.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*does not match its artifact id*");
            await mismatchedHash.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*hash does not match*");
            await mismatchedSize.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*size does not match*");
            await expiredArtifact.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*has expired*");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task FileSystemWorkflowFileIngressPort_ShouldRejectMissingAndTamperedContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-file-ingress-integrity-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var missing = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("missing"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "missing.txt",
                MediaType: "text/plain"));
            var tamperedLength = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("length"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "length.txt",
                MediaType: "text/plain"));
            var tamperedHash = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("hash"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "hash.txt",
                MediaType: "text/plain"));

            File.Delete(ResolveContentPath(root, missing.FileRef));
            await File.WriteAllTextAsync(ResolveContentPath(root, tamperedLength.FileRef), "longer-content");
            await File.WriteAllTextAsync(ResolveContentPath(root, tamperedHash.FileRef), "HASH");

            var missingContent = async () => await port.OpenReadAsync(missing.FileRef);
            var lengthMismatch = async () => await port.OpenReadAsync(tamperedLength.FileRef);
            var hashMismatch = async () => await port.OpenReadAsync(tamperedHash.FileRef);

            await missingContent.Should().ThrowAsync<FileNotFoundException>();
            await lengthMismatch.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*length does not match*");
            await hashMismatch.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*hash does not match*");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
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

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldReturnArgumentErrors()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-argument-error-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var file = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("valid"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "valid.txt",
                MediaType: "text/plain"));
            var tool = await GetDocumentExtractToolAsync(port);

            var missing = await ExecuteDocumentExtractAsync(tool, "{}");
            var array = await ExecuteDocumentExtractAsync(tool, "[]");
            var nonObjectFileRef = await ExecuteDocumentExtractAsync(tool, """{"fileRef":[]}""");
            var nonIntegerMaxChars = await ExecuteDocumentExtractAsync(
                tool,
                """{"fileRef":{"fileId":"wf-file-test"},"maxChars":"many"}""");
            var zeroMaxChars = await ExecuteDocumentExtractAsync(
                tool,
                BuildDocumentExtractArguments(file.FileRef, maxChars: 0));

            AssertDocumentExtractError(
                missing,
                "invalid_arguments",
                "requires a fileRef object or exactly one input file ref");
            AssertDocumentExtractError(
                array,
                "invalid_arguments",
                "arguments must be a JSON object");
            AssertDocumentExtractError(
                nonObjectFileRef,
                "invalid_arguments",
                "fileRef must be an object");
            AssertDocumentExtractError(
                nonIntegerMaxChars,
                "invalid_arguments",
                "maxChars must be an integer");
            AssertDocumentExtractError(
                zeroMaxChars,
                "invalid_arguments",
                "maxChars must be greater than zero");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldReturnMediaAndArtifactErrors()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-media-error-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var missingMediaType = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("missing-media"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "missing-media.bin"));
            var unsupportedMediaType = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("png"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "image.png",
                MediaType: "image/png"));
            var missingContent = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("deleted"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "deleted.txt",
                MediaType: "text/plain"));
            File.Delete(ResolveContentPath(root, missingContent.FileRef));
            var tool = await GetDocumentExtractToolAsync(port);

            var missingMedia = await ExecuteDocumentExtractAsync(
                tool,
                BuildDocumentExtractArguments(missingMediaType.FileRef));
            var unsupported = await ExecuteDocumentExtractAsync(
                tool,
                BuildDocumentExtractArguments(unsupportedMediaType.FileRef));
            var unavailable = await ExecuteDocumentExtractAsync(
                tool,
                BuildDocumentExtractArguments(missingContent.FileRef));

            AssertDocumentExtractError(
                missingMedia,
                "unsupported_media_type",
                "media type is required");
            AssertDocumentExtractError(
                unsupported,
                "unsupported_media_type",
                "image/png");
            AssertDocumentExtractError(
                unavailable,
                "artifact_unavailable",
                "artifact content was not found");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldTruncateUtf8TextAndRejectInvalidEncoding()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-utf8-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var text = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("abcdef"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "text.txt",
                MediaType: "text/plain; charset=utf-8"));
            var invalidEncoding = await port.IngestAsync(new WorkflowFileIngressRequest(
                new byte[] { 0xC3, 0x28 },
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "invalid.txt",
                MediaType: "text/plain"));
            var tool = await GetDocumentExtractToolAsync(port);

            var truncated = await ExecuteDocumentExtractAsync(
                tool,
                BuildDocumentExtractArguments(text.FileRef, maxChars: 3));
            var invalid = await ExecuteDocumentExtractAsync(
                tool,
                BuildDocumentExtractArguments(invalidEncoding.FileRef));

            truncated.GetProperty("extraction_kind").GetString().Should().Be("utf8_text");
            truncated.GetProperty("media_type").GetString().Should().Be("text/plain");
            truncated.GetProperty("text").GetString().Should().Be("abc");
            truncated.GetProperty("truncated").GetBoolean().Should().BeTrue();
            truncated.GetProperty("extracted_chars").GetInt32().Should().Be(3);
            AssertDocumentExtractError(
                invalid,
                "invalid_text_encoding",
                "valid UTF-8");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldExtractPdfText()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-pdf-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var pdf = await port.IngestAsync(new WorkflowFileIngressRequest(
                BuildSimplePdf("PDF hello"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "input.pdf",
                MediaType: "application/pdf"));
            var tool = await GetDocumentExtractToolAsync(port);

            var result = await ExecuteDocumentExtractAsync(
                tool,
                BuildDocumentExtractArguments(pdf.FileRef));

            result.GetProperty("extraction_kind").GetString().Should().Be("pdf_text");
            result.GetProperty("media_type").GetString().Should().Be("application/pdf");
            result.GetProperty("text").GetString().Should().Contain("PDF hello");
            result.GetProperty("truncated").GetBoolean().Should().BeFalse();
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

    private static async Task<JsonElement> ExecuteDocumentExtractAsync(
        IWorkflowTool tool,
        string argumentsJson,
        IReadOnlyList<ProtoWorkflowFileRef>? inputFileRefs = null)
    {
        var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(argumentsJson, inputFileRefs));
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    private static void AssertDocumentExtractError(
        JsonElement result,
        string error,
        string detail)
    {
        result.GetProperty("error").GetString().Should().Be(error);
        result.GetProperty("detail").GetString().Should().Contain(detail);
    }

    private static string ResolveContentPath(string root, ApplicationWorkflowFileRef fileRef) =>
        Path.Combine(root, fileRef.FileId!, "content.bin");

    private static string ExpectedSha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static byte[] BuildSimplePdf(string text)
    {
        var escapedText = text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
        var content = $"BT /F1 24 Tf 100 700 Td ({escapedText}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var builder = new StringBuilder();
        var offsets = new List<int>();
        builder.Append("%PDF-1.4\n");
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(i + 1)
                .Append(" 0 obj\n")
                .Append(objects[i])
                .Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ")
            .Append(objects.Length + 1)
            .Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            builder.Append(offset.ToString("D10"))
                .Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ")
            .Append(objects.Length + 1)
            .Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset)
            .Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
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
