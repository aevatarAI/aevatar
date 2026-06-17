using System.Text;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ApplicationWorkflowFileRef = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileRef;
using ApplicationWorkflowFileSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileSourceKind;

namespace Aevatar.Workflow.Host.Api.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
public sealed class WorkflowFileArtifactLifecycleTests
{
    [Fact]
    public void AddWorkflowInfrastructure_ShouldRegisterFilesystemCleanupPortForLocalBackend()
    {
        using (ClearRuntimeEnvironment())
        {
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddWorkflowInfrastructure();

            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<IWorkflowFileArtifactOwnershipPort>()
                .Should().BeSameAs(provider.GetRequiredService<IWorkflowFileIngressPort>());
            provider.GetRequiredService<IWorkflowFileArtifactCleanupPort>()
                .Should().BeSameAs(provider.GetRequiredService<IWorkflowFileIngressPort>());
            services.Should().Contain(x =>
                x.ServiceType == typeof(IHostedService) &&
                x.ImplementationType == typeof(WorkflowFileArtifactCleanupHostedService));
        }
    }

    [Fact]
    public void AddWorkflowCapabilityServices_ShouldRegisterFilesystemArtifactLifecyclePorts()
    {
        using (ClearRuntimeEnvironment())
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var configuration = new ConfigurationBuilder().Build();

            services.AddWorkflowCapability(configuration);

            services.Should().Contain(x =>
                x.ServiceType == typeof(IWorkflowFileArtifactOwnershipPort) &&
                x.ImplementationFactory != null);
            services.Should().Contain(x =>
                x.ServiceType == typeof(IWorkflowFileArtifactCleanupPort) &&
                x.ImplementationFactory != null);
            services.Should().Contain(x =>
                x.ServiceType == typeof(IHostedService) &&
                x.ImplementationType == typeof(WorkflowFileArtifactCleanupHostedService));
        }
    }

    [Fact]
    public void AddWorkflowInfrastructure_ShouldFailClosedForProductionWhenArtifactBackendIsImplicit()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowFileArtifacts:Policies:Environment"] = "Production",
            })
            .Build();

        var act = () => services.AddWorkflowInfrastructure(configuration: configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WorkflowFileArtifacts:Backend*External*Production*");
    }

    [Fact]
    public void AddWorkflowInfrastructure_ShouldFailClosedForExternalArtifactBackendWithoutAllPorts()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<RecordingWorkflowFileArtifactPort>();
        services.AddSingleton<IWorkflowFileIngressPort>(sp =>
            sp.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        services.AddSingleton<IWorkflowFileArtifactReadPort>(sp =>
            sp.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        services.AddSingleton<IWorkflowFileArtifactOwnershipPort>(sp =>
            sp.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowFileArtifacts:Backend"] = "External",
            })
            .Build();

        var act = () => services.AddWorkflowInfrastructure(configuration: configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IWorkflowFileArtifactCleanupPort*");
    }

    [Fact]
    public void AddWorkflowInfrastructure_ShouldUseExplicitExternalArtifactPortsWhenBackendIsExternal()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<RecordingWorkflowFileArtifactPort>();
        services.AddSingleton<IWorkflowFileIngressPort>(sp =>
            sp.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        services.AddSingleton<IWorkflowFileArtifactReadPort>(sp =>
            sp.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        services.AddSingleton<IWorkflowFileArtifactOwnershipPort>(sp =>
            sp.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        services.AddSingleton<IWorkflowFileArtifactCleanupPort>(sp =>
            sp.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowFileArtifacts:Backend"] = "External",
            })
            .Build();

        services.AddWorkflowInfrastructure(configuration: configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetService<FileSystemWorkflowFileIngressPort>().Should().BeNull();
        provider.GetRequiredService<IWorkflowFileIngressPort>()
            .Should().BeSameAs(provider.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        provider.GetRequiredService<IWorkflowFileArtifactCleanupPort>()
            .Should().BeSameAs(provider.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        services.Should().Contain(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(WorkflowFileArtifactCleanupHostedService));
    }

    [Fact]
    public async Task FileSystemWorkflowFileIngressPort_ShouldCleanupExpiredAndIncompleteArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-file-cleanup-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.UtcNow;
            var port = new FileSystemWorkflowFileIngressPort(
                Options.Create(new FileSystemWorkflowFileIngressOptions
                {
                    RootDirectory = root,
                    TimeToLive = TimeSpan.FromMinutes(30),
                    IncompleteArtifactAge = TimeSpan.FromMinutes(10),
                }));

            var live = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("live"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                ExpiresAtUnixMs: now.AddMinutes(30).ToUnixTimeMilliseconds()));
            var expired = await port.IngestAsync(new WorkflowFileIngressRequest(
                Encoding.UTF8.GetBytes("expired"),
                ApplicationWorkflowFileSourceKind.ChatInput,
                ExpiresAtUnixMs: now.AddMinutes(-1).ToUnixTimeMilliseconds()));
            var staleIncomplete = Path.Combine(root, "wf-file-incomplete-stale");
            var freshIncomplete = Path.Combine(root, "wf-file-incomplete-fresh");
            Directory.CreateDirectory(staleIncomplete);
            Directory.CreateDirectory(freshIncomplete);
            await File.WriteAllTextAsync(Path.Combine(staleIncomplete, "content.bin"), "staged");
            await File.WriteAllTextAsync(Path.Combine(freshIncomplete, "content.bin"), "staged");
            Directory.SetLastWriteTimeUtc(staleIncomplete, now.AddHours(-1).UtcDateTime);
            Directory.SetLastWriteTimeUtc(freshIncomplete, now.UtcDateTime);

            var result = await ((IWorkflowFileArtifactCleanupPort)port).CleanupAsync(
                new WorkflowFileArtifactCleanupRequest(now.ToUnixTimeMilliseconds()));

            result.ScannedArtifactCount.Should().Be(4);
            result.DeletedExpiredArtifactCount.Should().Be(1);
            result.DeletedIncompleteArtifactCount.Should().Be(1);
            result.DeletedArtifactCount.Should().Be(2);
            Directory.Exists(Path.Combine(root, live.FileRef.FileId!)).Should().BeTrue();
            Directory.Exists(Path.Combine(root, expired.FileRef.FileId!)).Should().BeFalse();
            Directory.Exists(staleIncomplete).Should().BeFalse();
            Directory.Exists(freshIncomplete).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkflowFileArtifactCleanupHostedService_ShouldTriggerCleanupOnStart()
    {
        var cleanupPort = new RecordingWorkflowFileArtifactPort();
        var service = new WorkflowFileArtifactCleanupHostedService(
            cleanupPort,
            Options.Create(new WorkflowFileArtifactOptions
            {
                CleanupEnabled = true,
                CleanupOnStart = true,
                CleanupInterval = TimeSpan.Zero,
            }),
            NullLogger<WorkflowFileArtifactCleanupHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        cleanupPort.CleanupRequests.Should().ContainSingle();
    }

    private static IDisposable ClearRuntimeEnvironment()
    {
        var previousDotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var previousAspnet = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
        return new RestoreEnvironment(previousDotnet, previousAspnet);
    }

    private sealed class RestoreEnvironment(string? dotnetEnvironment, string? aspnetCoreEnvironment) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", dotnetEnvironment);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspnetCoreEnvironment);
        }
    }

    private sealed class RecordingWorkflowFileArtifactPort :
        IWorkflowFileIngressPort,
        IWorkflowFileArtifactReadPort,
        IWorkflowFileArtifactOwnershipPort,
        IWorkflowFileArtifactCleanupPort
    {
        public List<WorkflowFileArtifactCleanupRequest> CleanupRequests { get; } = [];

        public ValueTask<WorkflowFileIngressResult> IngestAsync(
            WorkflowFileIngressRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ApplicationWorkflowFileRef> DescribeAsync(
            ApplicationWorkflowFileRef fileRef,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<WorkflowFileArtifactContent> OpenReadAsync(
            ApplicationWorkflowFileRef fileRef,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask BindOwnerAsync(
            ApplicationWorkflowFileRef fileRef,
            string ownerRunId,
            string? ownerScopeId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<WorkflowFileArtifactCleanupResult> CleanupAsync(
            WorkflowFileArtifactCleanupRequest request,
            CancellationToken cancellationToken = default)
        {
            CleanupRequests.Add(request);
            return ValueTask.FromResult(new WorkflowFileArtifactCleanupResult(
                ScannedArtifactCount: 0,
                DeletedExpiredArtifactCount: 0,
                DeletedIncompleteArtifactCount: 0));
        }
    }
}
