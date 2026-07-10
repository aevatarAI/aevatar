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
using ApplicationFileArtifactRef = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef;
using ApplicationFileArtifactSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactSourceKind;

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
            provider.GetRequiredService<IFileArtifactOwnershipPort>()
                .Should().BeSameAs(provider.GetRequiredService<IFileArtifactIngressPort>());
            provider.GetRequiredService<IFileArtifactCleanupPort>()
                .Should().BeSameAs(provider.GetRequiredService<IFileArtifactIngressPort>());
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
                x.ServiceType == typeof(IFileArtifactOwnershipPort) &&
                x.ImplementationFactory != null);
            services.Should().Contain(x =>
                x.ServiceType == typeof(IFileArtifactCleanupPort) &&
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
    public void AddWorkflowInfrastructure_ShouldFailClosedForProductionDotnetEnvironmentWhenArtifactBackendIsImplicit()
    {
        using (SetRuntimeEnvironment(dotnetEnvironment: "Production", aspNetCoreEnvironment: null))
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var configuration = new ConfigurationBuilder().Build();

            var act = () => services.AddWorkflowInfrastructure(configuration: configuration);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*WorkflowFileArtifacts:Backend*External*Production*");
        }
    }

    [Fact]
    public void AddWorkflowInfrastructure_ShouldFailClosedForProductionAspNetCoreEnvironmentWhenArtifactBackendIsImplicit()
    {
        using (SetRuntimeEnvironment(dotnetEnvironment: null, aspNetCoreEnvironment: "Production"))
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var configuration = new ConfigurationBuilder().Build();

            var act = () => services.AddWorkflowInfrastructure(configuration: configuration);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*WorkflowFileArtifacts:Backend*External*Production*");
        }
    }

    [Fact]
    public void AddWorkflowInfrastructure_ShouldFailClosedForExternalArtifactBackendWithoutAllPorts()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterRecordingArtifactPorts(services, includeCleanup: false);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowFileArtifacts:Backend"] = "External",
            })
            .Build();

        var act = () => services.AddWorkflowInfrastructure(configuration: configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IFileArtifactCleanupPort*");
    }

    [Fact]
    public void AddWorkflowInfrastructure_ShouldUseExplicitExternalArtifactPortsWhenBackendIsExternal()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterRecordingArtifactPorts(services);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowFileArtifacts:Backend"] = "External",
            })
            .Build();

        services.AddWorkflowInfrastructure(configuration: configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetService<FileSystemFileArtifactPort>().Should().BeNull();
        provider.GetRequiredService<IFileArtifactIngressPort>()
            .Should().BeSameAs(provider.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        provider.GetRequiredService<IFileArtifactCleanupPort>()
            .Should().BeSameAs(provider.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        services.Should().Contain(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(WorkflowFileArtifactCleanupHostedService));
    }

    [Fact]
    public void AddWorkflowInfrastructure_ShouldUseExplicitExternalArtifactPortsInProduction()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterRecordingArtifactPorts(services);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowFileArtifacts:Policies:Environment"] = "Production",
                ["WorkflowFileArtifacts:Backend"] = "External",
            })
            .Build();

        services.AddWorkflowInfrastructure(configuration: configuration);

        using var provider = services.BuildServiceProvider();
        var artifactPort = provider.GetRequiredService<RecordingWorkflowFileArtifactPort>();
        provider.GetService<FileSystemFileArtifactPort>().Should().BeNull();
        provider.GetRequiredService<IFileArtifactIngressPort>().Should().BeSameAs(artifactPort);
        provider.GetRequiredService<IFileArtifactReadPort>().Should().BeSameAs(artifactPort);
        provider.GetRequiredService<IFileArtifactOwnershipPort>().Should().BeSameAs(artifactPort);
        provider.GetRequiredService<IFileArtifactCleanupPort>().Should().BeSameAs(artifactPort);
    }

    [Fact]
    public void AddWorkflowInfrastructure_ShouldRejectUnknownArtifactBackend()
    {
        using (ClearRuntimeEnvironment())
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WorkflowFileArtifacts:Backend"] = "Unknown",
                })
                .Build();

            var act = () => services.AddWorkflowInfrastructure(configuration: configuration);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*WorkflowFileArtifacts:Backend*FileSystem*External*");
        }
    }

    [Fact]
    public async Task FileSystemFileArtifactPort_ShouldCleanupExpiredAndIncompleteArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-workflow-file-cleanup-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.UtcNow;
            var port = new FileSystemFileArtifactPort(
                Options.Create(new FileSystemFileArtifactOptions
                {
                    RootDirectory = root,
                    TimeToLive = TimeSpan.FromMinutes(30),
                    IncompleteArtifactAge = TimeSpan.FromMinutes(10),
                }));

            var live = await port.IngestAsync(new FileArtifactIngressRequest(
                Encoding.UTF8.GetBytes("live"),
                ApplicationFileArtifactSourceKind.ChatInput,
                ExpiresAtUnixMs: now.AddMinutes(30).ToUnixTimeMilliseconds()));
            var expired = await port.IngestAsync(new FileArtifactIngressRequest(
                Encoding.UTF8.GetBytes("expired"),
                ApplicationFileArtifactSourceKind.ChatInput,
                ExpiresAtUnixMs: now.AddMinutes(-1).ToUnixTimeMilliseconds()));
            var staleIncomplete = Path.Combine(root, "wf-file-incomplete-stale");
            var freshIncomplete = Path.Combine(root, "wf-file-incomplete-fresh");
            Directory.CreateDirectory(staleIncomplete);
            Directory.CreateDirectory(freshIncomplete);
            await File.WriteAllTextAsync(Path.Combine(staleIncomplete, "content.bin"), "staged");
            await File.WriteAllTextAsync(Path.Combine(freshIncomplete, "content.bin"), "staged");
            Directory.SetLastWriteTimeUtc(staleIncomplete, now.AddHours(-1).UtcDateTime);
            Directory.SetLastWriteTimeUtc(freshIncomplete, now.UtcDateTime);

            var result = await ((IFileArtifactCleanupPort)port).CleanupAsync(
                new FileArtifactCleanupRequest(now.ToUnixTimeMilliseconds()));

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
        var cleanupPort = await RunCleanupHostedServiceAsync(
            cleanupEnabled: true,
            cleanupOnStart: true,
            cleanupInterval: TimeSpan.Zero);

        cleanupPort.CleanupRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task WorkflowFileArtifactCleanupHostedService_ShouldNotTriggerCleanupWhenDisabled()
    {
        var cleanupPort = await RunCleanupHostedServiceAsync(
            cleanupEnabled: false,
            cleanupOnStart: true,
            cleanupInterval: TimeSpan.Zero);

        cleanupPort.CleanupRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowFileArtifactCleanupHostedService_ShouldNotTriggerStartupCleanupWhenCleanupOnStartIsDisabled()
    {
        var cleanupPort = await RunCleanupHostedServiceAsync(
            cleanupEnabled: true,
            cleanupOnStart: false,
            cleanupInterval: TimeSpan.Zero);

        cleanupPort.CleanupRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowFileArtifactCleanupHostedService_ShouldTriggerPeriodicCleanupAndStopCleanly()
    {
        var cleanupPort = new RecordingWorkflowFileArtifactPort(completeCleanupWhenCanceled: true);
        using var service = CreateCleanupHostedService(
            cleanupPort,
            cleanupEnabled: true,
            cleanupOnStart: false,
            cleanupInterval: TimeSpan.FromMilliseconds(1));

        await service.StartAsync(CancellationToken.None);
        using var cleanupObservedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await cleanupPort.WaitForCleanupAsync(cleanupObservedTimeout.Token);
        var act = async () => await service.StopAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().NotThrowAsync();
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

    private static IDisposable SetRuntimeEnvironment(string? dotnetEnvironment, string? aspNetCoreEnvironment)
    {
        var previousDotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var previousAspnet = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", dotnetEnvironment);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspNetCoreEnvironment);
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

    private static async Task<RecordingWorkflowFileArtifactPort> RunCleanupHostedServiceAsync(
        bool cleanupEnabled,
        bool cleanupOnStart,
        TimeSpan cleanupInterval)
    {
        var cleanupPort = new RecordingWorkflowFileArtifactPort();
        using var service = CreateCleanupHostedService(
            cleanupPort,
            cleanupEnabled,
            cleanupOnStart,
            cleanupInterval);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        return cleanupPort;
    }

    private static WorkflowFileArtifactCleanupHostedService CreateCleanupHostedService(
        RecordingWorkflowFileArtifactPort cleanupPort,
        bool cleanupEnabled,
        bool cleanupOnStart,
        TimeSpan cleanupInterval) =>
        new(
            cleanupPort,
            Options.Create(new WorkflowFileArtifactOptions
            {
                CleanupEnabled = cleanupEnabled,
                CleanupOnStart = cleanupOnStart,
                CleanupInterval = cleanupInterval,
            }),
            NullLogger<WorkflowFileArtifactCleanupHostedService>.Instance);

    private static void RegisterRecordingArtifactPorts(
        IServiceCollection services,
        bool includeCleanup = true)
    {
        services.AddSingleton<RecordingWorkflowFileArtifactPort>();
        services.AddSingleton<IFileArtifactIngressPort>(sp =>
            sp.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        services.AddSingleton<IFileArtifactReadPort>(sp =>
            sp.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        services.AddSingleton<IFileArtifactOwnershipPort>(sp =>
            sp.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        if (includeCleanup)
        {
            services.AddSingleton<IFileArtifactCleanupPort>(sp =>
                sp.GetRequiredService<RecordingWorkflowFileArtifactPort>());
        }
    }

    private sealed class RecordingWorkflowFileArtifactPort(bool completeCleanupWhenCanceled = false) :
        IFileArtifactIngressPort,
        IFileArtifactReadPort,
        IFileArtifactOwnershipPort,
        IFileArtifactCleanupPort
    {
        private readonly object _cleanupRequestLock = new();
        private readonly List<FileArtifactCleanupRequest> _cleanupRequests = [];
        private TaskCompletionSource<FileArtifactCleanupRequest>? _nextCleanupRequest;

        public IReadOnlyList<FileArtifactCleanupRequest> CleanupRequests
        {
            get
            {
                lock (_cleanupRequestLock)
                {
                    return _cleanupRequests.ToArray();
                }
            }
        }

        public ValueTask<FileArtifactIngressResult> IngestAsync(
            FileArtifactIngressRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ApplicationFileArtifactRef> DescribeAsync(
            ApplicationFileArtifactRef fileRef,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileArtifactContent> OpenReadAsync(
            ApplicationFileArtifactRef fileRef,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask BindOwnerAsync(
            ApplicationFileArtifactRef fileRef,
            string ownerRunId,
            string? ownerScopeId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FileArtifactCleanupRequest> WaitForCleanupAsync(CancellationToken cancellationToken)
        {
            lock (_cleanupRequestLock)
            {
                if (_cleanupRequests.Count > 0)
                    return Task.FromResult(_cleanupRequests[^1]);

                _nextCleanupRequest ??= new TaskCompletionSource<FileArtifactCleanupRequest>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return _nextCleanupRequest.Task.WaitAsync(cancellationToken);
            }
        }

        public async ValueTask<FileArtifactCleanupResult> CleanupAsync(
            FileArtifactCleanupRequest request,
            CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<FileArtifactCleanupRequest>? nextCleanupRequest;
            lock (_cleanupRequestLock)
            {
                _cleanupRequests.Add(request);
                nextCleanupRequest = _nextCleanupRequest;
                _nextCleanupRequest = null;
            }

            nextCleanupRequest?.TrySetResult(request);
            if (completeCleanupWhenCanceled)
                await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);

            return new FileArtifactCleanupResult(
                ScannedArtifactCount: 0,
                DeletedExpiredArtifactCount: 0,
                DeletedIncompleteArtifactCount: 0);
        }

        private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                cancellation);

            await cancellation.Task.ConfigureAwait(false);
        }
    }
}
