using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Hosting.Demo;

internal sealed class GAgentServiceDemoBootstrapHostedService : IHostedService
{
    private readonly IServiceCommandPort _commandPort;
    private readonly IServiceLifecycleQueryPort _lifecycleQueryPort;
    private readonly IServiceServingQueryPort _servingQueryPort;
    private readonly IOptions<GAgentServiceDemoOptions> _options;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<GAgentServiceDemoBootstrapHostedService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<DateTimeOffset> _utcNow;

    public GAgentServiceDemoBootstrapHostedService(
        IServiceCommandPort commandPort,
        IServiceLifecycleQueryPort lifecycleQueryPort,
        IServiceServingQueryPort servingQueryPort,
        IOptions<GAgentServiceDemoOptions> options,
        IHostEnvironment hostEnvironment,
        ILogger<GAgentServiceDemoBootstrapHostedService> logger)
        : this(
            commandPort,
            lifecycleQueryPort,
            servingQueryPort,
            options,
            hostEnvironment,
            logger,
            Task.Delay,
            () => DateTimeOffset.UtcNow)
    {
    }

    internal GAgentServiceDemoBootstrapHostedService(
        IServiceCommandPort commandPort,
        IServiceLifecycleQueryPort lifecycleQueryPort,
        IServiceServingQueryPort servingQueryPort,
        IOptions<GAgentServiceDemoOptions> options,
        IHostEnvironment hostEnvironment,
        ILogger<GAgentServiceDemoBootstrapHostedService> logger,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<DateTimeOffset> utcNow)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _lifecycleQueryPort = lifecycleQueryPort ?? throw new ArgumentNullException(nameof(lifecycleQueryPort));
        _servingQueryPort = servingQueryPort ?? throw new ArgumentNullException(nameof(servingQueryPort));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _hostEnvironment = hostEnvironment ?? throw new ArgumentNullException(nameof(hostEnvironment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = _options.Value;
        if (!ShouldBootstrap(options))
        {
            _logger.LogDebug("Skip GAgentService demo bootstrap because it is disabled.");
            return;
        }

        var bootstrapped = new List<string>(GAgentServiceDemoDefinitions.All.Count);
        foreach (var definition in GAgentServiceDemoDefinitions.All)
        {
            await EnsureDemoServiceAsync(definition, options, cancellationToken);
            bootstrapped.Add(definition.ServiceId);
        }

        _logger.LogInformation(
            "Bootstrapped GAgentService demo services for {TenantId}/{AppId}/{Namespace}: {ServiceIds}",
            options.TenantId,
            options.AppId,
            options.Namespace,
            string.Join(", ", bootstrapped));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool ShouldBootstrap(GAgentServiceDemoOptions options) =>
        options.Enabled ?? _hostEnvironment.IsDevelopment();

    private async Task EnsureDemoServiceAsync(
        GAgentServiceDemoDefinition definition,
        GAgentServiceDemoOptions options,
        CancellationToken ct)
    {
        var identity = CreateIdentity(options, definition.ServiceId);
        var endpoint = GAgentServiceDemoDefinitions.CreateEndpointSpec(definition);
        var expectedEndpointId = endpoint.EndpointId;

        var service = await _lifecycleQueryPort.GetServiceAsync(identity, ct);
        if (service == null)
        {
            await _commandPort.CreateServiceAsync(
                new CreateServiceDefinitionCommand
                {
                    Spec = BuildServiceDefinition(identity, definition, endpoint),
                },
                ct);
        }
        else if (NeedsServiceUpdate(service, definition, endpoint))
        {
            await _commandPort.UpdateServiceAsync(
                new UpdateServiceDefinitionCommand
                {
                    Spec = BuildServiceDefinition(identity, definition, endpoint),
                },
                ct);
        }

        var revisions = await _lifecycleQueryPort.GetServiceRevisionsAsync(identity, ct);
        var revision = revisions?.Revisions.FirstOrDefault(x => string.Equals(x.RevisionId, definition.RevisionId, StringComparison.Ordinal));
        if (revision == null)
        {
            await _commandPort.CreateRevisionAsync(
                new CreateServiceRevisionCommand
                {
                    Spec = BuildRevision(identity, definition),
                },
                ct);
            revision = new ServiceRevisionSnapshot(
                definition.RevisionId,
                ServiceImplementationKind.Workflow.ToString(),
                ServiceRevisionStatus.Created.ToString(),
                string.Empty,
                string.Empty,
                [],
                null,
                null,
                null,
                null);
        }

        if (string.Equals(revision.Status, ServiceRevisionStatus.Retired.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Demo service '{ServiceKeys.Build(identity)}' revision '{definition.RevisionId}' is retired.");
        }

        if (string.Equals(revision.Status, ServiceRevisionStatus.PreparationFailed.ToString(), StringComparison.Ordinal) ||
            string.Equals(revision.Status, ServiceRevisionStatus.Created.ToString(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(revision.Status))
        {
            await _commandPort.PrepareRevisionAsync(
                new PrepareServiceRevisionCommand
                {
                    Identity = identity.Clone(),
                    RevisionId = definition.RevisionId,
                },
                ct);
        }

        if (!string.Equals(revision.Status, ServiceRevisionStatus.Published.ToString(), StringComparison.Ordinal))
        {
            await _commandPort.PublishRevisionAsync(
                new PublishServiceRevisionCommand
                {
                    Identity = identity.Clone(),
                    RevisionId = definition.RevisionId,
                },
                ct);
        }

        if (!string.Equals(service?.DefaultServingRevisionId, definition.RevisionId, StringComparison.Ordinal))
        {
            await _commandPort.SetDefaultServingRevisionAsync(
                new SetDefaultServingRevisionCommand
                {
                    Identity = identity.Clone(),
                    RevisionId = definition.RevisionId,
                },
                ct);
        }

        var deployments = await _lifecycleQueryPort.GetServiceDeploymentsAsync(identity, ct);
        if (!HasActiveDeployment(deployments, definition.RevisionId))
        {
            await _commandPort.ActivateServiceRevisionAsync(
                new ActivateServiceRevisionCommand
                {
                    Identity = identity.Clone(),
                    RevisionId = definition.RevisionId,
                },
                ct);
        }

        await WaitForServingTrafficViewAsync(identity, definition.RevisionId, expectedEndpointId, options, ct);
    }

    private async Task WaitForServingTrafficViewAsync(
        ServiceIdentity identity,
        string revisionId,
        string endpointId,
        GAgentServiceDemoOptions options,
        CancellationToken ct)
    {
        var serviceKey = ServiceKeys.Build(identity);
        var timeout = ResolvePositiveDuration(options.ServingReadinessTimeoutSeconds, 30, value => TimeSpan.FromSeconds(value));
        var interval = ResolvePositiveDuration(options.ServingReadinessPollIntervalMilliseconds, 250, value => TimeSpan.FromMilliseconds(value));
        var deadline = _utcNow() + timeout;
        ServiceTrafficViewSnapshot? lastTrafficView = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lastTrafficView = await _servingQueryPort.GetServiceTrafficViewAsync(identity, ct);
            if (HasActiveTrafficTarget(lastTrafficView, revisionId, endpointId))
                return;

            var remaining = deadline - _utcNow();
            if (remaining <= TimeSpan.Zero)
                throw new InvalidOperationException(
                    $"Demo service '{serviceKey}' revision '{revisionId}' did not expose an active serving traffic view for endpoint '{endpointId}' within {timeout.TotalSeconds:F0}s. Last traffic view: {DescribeTrafficView(lastTrafficView)}");

            await _delayAsync(remaining < interval ? remaining : interval, ct);
        }
    }

    private static ServiceIdentity CreateIdentity(GAgentServiceDemoOptions options, string serviceId) =>
        new()
        {
            TenantId = options.TenantId.Trim(),
            AppId = options.AppId.Trim(),
            Namespace = options.Namespace.Trim(),
            ServiceId = serviceId,
        };

    private static ServiceDefinitionSpec BuildServiceDefinition(
        ServiceIdentity identity,
        GAgentServiceDemoDefinition definition,
        ServiceEndpointSpec endpoint)
    {
        var spec = new ServiceDefinitionSpec
        {
            Identity = identity.Clone(),
            DisplayName = definition.DisplayName,
        };
        spec.Endpoints.Add(endpoint.Clone());
        return spec;
    }

    private static ServiceRevisionSpec BuildRevision(
        ServiceIdentity identity,
        GAgentServiceDemoDefinition definition) =>
        new()
        {
            Identity = identity.Clone(),
            RevisionId = definition.RevisionId,
            ImplementationKind = ServiceImplementationKind.Workflow,
            WorkflowSpec = new WorkflowServiceRevisionSpec
            {
                WorkflowName = definition.WorkflowName,
                WorkflowYaml = definition.WorkflowYaml,
            },
        };

    private static bool HasActiveDeployment(
        ServiceDeploymentCatalogSnapshot? snapshot,
        string revisionId)
    {
        if (snapshot == null)
            return false;

        return snapshot.Deployments.Any(x =>
            string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(x.DeploymentId) &&
            !string.IsNullOrWhiteSpace(x.PrimaryActorId) &&
            string.Equals(x.Status, ServiceDeploymentStatus.Active.ToString(), StringComparison.Ordinal));
    }

    private static bool HasActiveTrafficTarget(
        ServiceTrafficViewSnapshot? snapshot,
        string revisionId,
        string endpointId)
    {
        if (snapshot == null)
            return false;

        return snapshot.Endpoints.Any(endpoint =>
            string.Equals(endpoint.EndpointId, endpointId, StringComparison.Ordinal) &&
            endpoint.Targets.Any(target =>
                string.Equals(target.RevisionId, revisionId, StringComparison.Ordinal) &&
                target.AllocationWeight > 0 &&
                !string.IsNullOrWhiteSpace(target.DeploymentId) &&
                !string.IsNullOrWhiteSpace(target.PrimaryActorId) &&
                string.Equals(target.ServingState, ServiceServingState.Active.ToString(), StringComparison.Ordinal)));
    }

    private static TimeSpan ResolvePositiveDuration(
        int configuredValue,
        int fallbackValue,
        Func<int, TimeSpan> factory) =>
        factory(configuredValue > 0 ? configuredValue : fallbackValue);

    private static string DescribeTrafficView(ServiceTrafficViewSnapshot? snapshot)
    {
        if (snapshot == null)
            return "<missing>";

        var endpoints = snapshot.Endpoints.Select(endpoint =>
            $"{endpoint.EndpointId}=[{string.Join(", ", endpoint.Targets.Select(target =>
                $"{target.RevisionId}:{target.ServingState}:{target.AllocationWeight}:{target.DeploymentId}:{target.PrimaryActorId}"))}]");
        return $"generation={snapshot.Generation}; endpoints={string.Join("; ", endpoints)}";
    }

    private static bool NeedsServiceUpdate(
        ServiceCatalogSnapshot snapshot,
        GAgentServiceDemoDefinition definition,
        ServiceEndpointSpec endpoint)
    {
        if (!string.Equals(snapshot.DisplayName, definition.DisplayName, StringComparison.Ordinal))
            return true;

        if (snapshot.Endpoints.Count != 1)
            return true;

        var existing = snapshot.Endpoints[0];
        return !string.Equals(existing.EndpointId, endpoint.EndpointId, StringComparison.Ordinal) ||
               !string.Equals(existing.DisplayName, endpoint.DisplayName, StringComparison.Ordinal) ||
               !string.Equals(existing.Kind, endpoint.Kind.ToString(), StringComparison.Ordinal) ||
               !string.Equals(existing.RequestTypeUrl, endpoint.RequestTypeUrl, StringComparison.Ordinal) ||
               !string.Equals(existing.ResponseTypeUrl, endpoint.ResponseTypeUrl, StringComparison.Ordinal) ||
               !string.Equals(existing.Description, endpoint.Description, StringComparison.Ordinal);
    }
}
