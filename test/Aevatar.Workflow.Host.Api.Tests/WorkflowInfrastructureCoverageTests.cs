using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Configuration;
using Aevatar.Hosting;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.HumanInteraction;
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
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Projection;
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
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;
using ApplicationWorkflowFileRef = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileRef;
using ApplicationWorkflowFileSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileSourceKind;
using ProtoWorkflowCallerCredential = Aevatar.Workflow.Abstractions.WorkflowCallerCredential;

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
    public void MapWorkflowCapabilityEndpoints_ShouldMapWorkflowRunForkRoute()
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
            .Contain("/api/workflow/runs/fork");
    }

    [Fact]
    public void MapWorkflowCapabilityEndpoints_ShouldMapWorkflowWebhookRoute()
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
            .Contain("/api/workflow-webhooks/{routeKey}");
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
        services.Should().Contain(x =>
            x.ServiceType == typeof(IChannelInteractionNotificationPort) &&
            x.ImplementationType == typeof(NullChannelInteractionNotificationPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionProjector<WorkflowExecutionProjectionContext>) &&
            x.ImplementationType == typeof(WorkflowInteractionNotificationProjector));
        services.Should().NotContain(x => x.ServiceType == typeof(ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>));
        services.Should().Contain(x => x.ServiceType == typeof(ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowExecutionQueryApplicationService));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowActorBindingReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowRunReportExportPort) &&
            x.ImplementationType == typeof(FileSystemWorkflowRunReportExporter));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowFileIngressPort) &&
            x.ImplementationFactory != null);
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowFileArtifactReadPort) &&
            x.ImplementationFactory != null);
        services.Should().Contain(x =>
            x.ServiceType == typeof(WorkflowWebhookIngressRequestBuilder));
        services.Should().NotContain(x =>
            x.ServiceType == typeof(IWorkflowWebhookReplayStore) &&
            x.ImplementationType == typeof(InMemoryWorkflowWebhookReplayStore));
        services.Should().NotContain(x =>
            x.ServiceType == typeof(IWorkflowWebhookReplayStore) &&
            x.ImplementationType == typeof(RedisWorkflowWebhookReplayStore));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(WorkflowDefinitionBootstrapHostedService));
    }

    [Fact]
    public void AddWorkflowCapabilityServices_ShouldRegisterInMemoryWebhookReplayStoreOnlyWhenExplicitlyConfigured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowWebhookIngressOptions.SectionName}:Enabled"] = "true",
                [$"{WorkflowWebhookIngressOptions.SectionName}:UseInMemoryReplayStore"] = "true",
            })
            .Build();

        services.AddWorkflowCapability(configuration);

        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowWebhookReplayStore) &&
            x.ImplementationType == typeof(InMemoryWorkflowWebhookReplayStore));
    }

    [Fact]
    public void AddWorkflowCapabilityServices_ShouldRegisterRedisWebhookReplayStore_WhenConnectionStringConfigured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowWebhookIngressOptions.SectionName}:RedisConnectionString"] = "localhost:6379,abortConnect=false",
                [$"{WorkflowWebhookIngressOptions.SectionName}:UseInMemoryReplayStore"] = "true",
            })
            .Build();

        services.AddWorkflowCapability(configuration);

        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowWebhookReplayStore) &&
            x.ImplementationType == typeof(RedisWorkflowWebhookReplayStore));
        services.Should().NotContain(x =>
            x.ServiceType == typeof(IWorkflowWebhookReplayStore) &&
            x.ImplementationType == typeof(InMemoryWorkflowWebhookReplayStore));
    }

    [Fact]
    public async Task FileSystemWorkflowFileIngressPort_ShouldStoreBytesAndReturnDescriptor()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-file-ingress-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = new FileSystemWorkflowFileIngressPort(
                Options.Create(new FileSystemWorkflowFileIngressOptions
                {
                    RootDirectory = root,
                    TimeToLive = TimeSpan.FromMinutes(30),
                }));

            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("hello"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "hello.png",
                MediaType: "image/png"));

            var descriptor = result.FileRef;
            descriptor.FileId.Should().StartWith("wf-file-");
            descriptor.ArtifactId.Should().Be($"workflow-file://{descriptor.FileId}");
            descriptor.SourceKind.Should().Be(ApplicationWorkflowFileSourceKind.ChatInput);
            descriptor.FileName.Should().Be("hello.png");
            descriptor.MediaType.Should().Be("image/png");
            descriptor.SizeBytes.Should().Be(5);
            descriptor.Sha256.Should().Be("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
            descriptor.CreatedAtUnixMs.Should().BeGreaterThan(0);
            descriptor.ExpiresAtUnixMs.Should().BeGreaterThan(descriptor.CreatedAtUnixMs);

            var storedPath = Path.Combine(root, descriptor.FileId!, "content.bin");
            File.Exists(storedPath).Should().BeTrue();
            (await File.ReadAllTextAsync(storedPath)).Should().Be("hello");
            File.Exists(Path.Combine(root, descriptor.FileId!, "descriptor.pb")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FileSystemWorkflowFileIngressPort_ShouldDescribeAndOpenStoredContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-file-read-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = new FileSystemWorkflowFileIngressPort(
                Options.Create(new FileSystemWorkflowFileIngressOptions
                {
                    RootDirectory = root,
                    TimeToLive = TimeSpan.FromMinutes(30),
                }));

            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("stored document"),
                ApplicationWorkflowFileSourceKind.ConnectedServiceResource,
                SourceMessageId: "om_123",
                SourceResourceKey: "file_key_123",
                FileName: "invoice.pdf",
                MediaType: "application/pdf"));

            var readPort = (IWorkflowFileArtifactReadPort)port;
            var secondPort = new FileSystemWorkflowFileIngressPort(
                Options.Create(new FileSystemWorkflowFileIngressOptions
                {
                    RootDirectory = root,
                    TimeToLive = TimeSpan.FromMinutes(30),
                }));
            var descriptor = await ((IWorkflowFileArtifactReadPort)secondPort).DescribeAsync(new ApplicationWorkflowFileRef
            {
                ArtifactId = result.FileRef.ArtifactId,
                Sha256 = result.FileRef.Sha256,
                SizeBytes = result.FileRef.SizeBytes,
            });

            descriptor.Should().BeEquivalentTo(result.FileRef);

            var opened = await readPort.OpenReadAsync(result.FileRef);
            opened.FileRef.Should().BeEquivalentTo(result.FileRef);
            await using (opened.Content)
            using (var reader = new StreamReader(opened.Content, Encoding.UTF8))
            {
                (await reader.ReadToEndAsync()).Should().Be("stored document");
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FileSystemWorkflowFileIngressPort_ShouldRejectMismatchedOrExpiredRefs()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-file-reject-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = new FileSystemWorkflowFileIngressPort(
                Options.Create(new FileSystemWorkflowFileIngressOptions
                {
                    RootDirectory = root,
                    TimeToLive = TimeSpan.FromMinutes(30),
                }));

            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("sealed"),
                ApplicationWorkflowFileSourceKind.ChatInput));
            var readPort = (IWorkflowFileArtifactReadPort)port;

            await readPort.Invoking(x => x.DescribeAsync(new ApplicationWorkflowFileRef
                {
                    FileId = result.FileRef.FileId,
                    ArtifactId = "workflow-file://wf-file-other",
                }).AsTask())
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*does not match*");

            await readPort.Invoking(x => x.DescribeAsync(new ApplicationWorkflowFileRef
                {
                    FileId = "../wf-file-escape",
                }).AsTask())
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*invalid*");

            var expired = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("old"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                ExpiresAtUnixMs: DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()));

            await readPort.Invoking(x => x.OpenReadAsync(expired.FileRef).AsTask())
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*expired*");

            var missing = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("missing"),
                ApplicationWorkflowFileSourceKind.ChatInput));
            File.Delete(Path.Combine(root, missing.FileRef.FileId!, "content.bin"));
            await readPort.Invoking(x => x.OpenReadAsync(missing.FileRef).AsTask())
                .Should().ThrowAsync<FileNotFoundException>();

            var tampered = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("original"),
                ApplicationWorkflowFileSourceKind.ChatInput));
            await File.WriteAllTextAsync(Path.Combine(root, tampered.FileRef.FileId!, "content.bin"), "mutated!");
            await readPort.Invoking(x => x.OpenReadAsync(tampered.FileRef).AsTask())
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*hash*");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldExtractUtf8TextFromStoredFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-text-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("invoice total: 42"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "invoice.txt",
                MediaType: "text/plain; charset=utf-8"));
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            var rootElement = document.RootElement;
            rootElement.GetProperty("extraction_kind").GetString().Should().Be("utf8_text");
            rootElement.GetProperty("media_type").GetString().Should().Be("text/plain");
            rootElement.GetProperty("text").GetString().Should().Be("invoice total: 42");
            rootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
            rootElement.GetProperty("extracted_chars").GetInt32().Should().Be(17);
            rootElement.GetProperty("file").GetProperty("file_id").GetString().Should().Be(result.FileRef.FileId);
            rootElement.GetProperty("file").GetProperty("sha256").GetString().Should().Be(result.FileRef.Sha256);
            output.ResultJson.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
            output.ResultJson.Contains("data_base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldTruncateTextAtRequestedLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-truncate-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("abcdef"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "long.txt",
                MediaType: "text/plain"));
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef, maxChars: 3),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("text").GetString().Should().Be("abc");
            document.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
            document.RootElement.GetProperty("extracted_chars").GetInt32().Should().Be(3);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldExtractPdfTextFromStoredFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-pdf-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var pdfBytes = BuildSimplePdf("pdf invoice total 42");
            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                pdfBytes,
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "invoice.pdf",
                MediaType: "application/pdf"));
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("extraction_kind").GetString().Should().Be("pdf_text");
            document.RootElement.GetProperty("media_type").GetString().Should().Be("application/pdf");
            document.RootElement.GetProperty("text").GetString().Should().Contain("pdf invoice total 42");
            document.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
            output.ResultJson.Contains("base64", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectUnsupportedMediaType()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-unsupported-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                new byte[] { 1, 2, 3 },
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "image.png",
                MediaType: "image/png"));
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("unsupported_media_type");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("image/png");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectMalformedArguments()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-arguments-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var tool = await GetDocumentExtractToolAsync(CreateFileArtifactPort(root));

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                """{"file_ref":{}}""",
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("invalid_arguments");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("fileId or artifactId");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowDocumentExtractTool_ShouldRejectInvalidUtf8()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-document-extract-utf8-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var port = CreateFileArtifactPort(root);
            var result = await port.IngestAsync(new WorkflowFileIngressRequest(
                new byte[] { 0xC3, 0x28 },
                ApplicationWorkflowFileSourceKind.ChatInput,
                FileName: "bad.txt",
                MediaType: "text/plain"));
            var tool = await GetDocumentExtractToolAsync(port);

            var output = await tool.ExecuteAsync(new WorkflowToolExecutionRequest(
                BuildDocumentExtractArguments(result.FileRef),
                "run-1",
                "extract",
                "exec-1",
                "call-1",
                "scope-1",
                new ProtoWorkflowCallerCredential()));

            using var document = JsonDocument.Parse(output.ResultJson);
            document.RootElement.GetProperty("error").GetString().Should().Be("invalid_text_encoding");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
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
                      "name": "host_router",
                      "type": "host_callback",
                      "enabled": true,
                      "hostCallback": {
                        "handler": "github_router",
                        "allowedOperations": ["classify_pr", "sync_labels", "classify_pr"],
                        "allowedInputKeys": ["issue", "repo", "issue"]
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
            var transform = document.Primitives.Single(primitive => primitive.Name == "transform");
            transform.Description.Should().Contain("decimal-only");
            transform.Parameters.Single(parameter => parameter.Name == "op")
                .Enum.Should().Contain(["sum", "subtract", "multiply", "divide", "round", "min", "max", "group_by"]);
            var aggregateParameter = transform.Parameters.Single(parameter => parameter.Name == "aggregate");
            aggregateParameter.Default.Should().Be("sum");
            aggregateParameter.Enum.Should().Equal("sum", "min", "max", "count");
            typeof(WorkflowPrimitiveCapability)
                .GetProperty("ClosedWorldBlocked")
                .Should().BeNull();
            document.Connectors.Select(connector => connector.Name)
                .Should().Equal("cli_runner", "custom_sink", "host_router", "http_news", "mcp_tools");
            document.Connectors.Single(connector => connector.Name == "http_news")
                .AllowedInputKeys.Should().Equal("limit", "query");
            document.Connectors.Single(connector => connector.Name == "http_news")
                .AllowedOperations.Should().Equal("get", "POST");
            document.Connectors.Single(connector => connector.Name == "cli_runner")
                .FixedArguments.Should().Equal("--json");
            document.Connectors.Single(connector => connector.Name == "mcp_tools")
                .AllowedOperations.Should().Equal("search");
            document.Connectors.Single(connector => connector.Name == "host_router")
                .AllowedOperations.Should().Equal("classify_pr", "sync_labels");
            document.Connectors.Single(connector => connector.Name == "host_router")
                .AllowedInputKeys.Should().Equal("issue", "repo");
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

    private static FileSystemWorkflowFileIngressPort CreateFileArtifactPort(string root) =>
        new(Options.Create(new FileSystemWorkflowFileIngressOptions
        {
            RootDirectory = root,
            TimeToLive = TimeSpan.FromMinutes(30),
        }));

    private static async Task<IWorkflowTool> GetDocumentExtractToolAsync(IWorkflowFileArtifactReadPort readPort)
    {
        var source = new WorkflowDocumentExtractToolSource(readPort);
        var tools = await source.GetToolsAsync();
        return tools.Should().ContainSingle(x => x.Name == "document_extract").Subject;
    }

    private static string BuildDocumentExtractArguments(ApplicationWorkflowFileRef fileRef, int? maxChars = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["file_ref"] = new Dictionary<string, object?>
            {
                ["file_id"] = fileRef.FileId,
                ["artifact_id"] = fileRef.ArtifactId,
                ["source_kind"] = fileRef.SourceKind.ToString(),
                ["source_message_id"] = fileRef.SourceMessageId,
                ["source_resource_key"] = fileRef.SourceResourceKey,
                ["file_name"] = fileRef.FileName,
                ["media_type"] = fileRef.MediaType,
                ["size_bytes"] = fileRef.SizeBytes,
                ["sha256"] = fileRef.Sha256,
                ["created_at_unix_ms"] = fileRef.CreatedAtUnixMs,
                ["expires_at_unix_ms"] = fileRef.ExpiresAtUnixMs,
            },
        };
        if (maxChars != null)
            payload["max_chars"] = maxChars.Value;

        return JsonSerializer.Serialize(payload);
    }

    private static byte[] BuildSimplePdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(text, 12, new PdfPoint(50, 750), font);
        return builder.Build();
    }

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
            Usage = new WorkflowRunUsageMetrics
            {
                PromptTokens = 12,
                CompletionTokens = 34,
                TotalTokens = 46,
                Model = "gpt-5.4",
                Cost = 0.56,
                LatencyMs = 789,
            },
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
