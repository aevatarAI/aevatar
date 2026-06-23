using System.Security.Cryptography;
using System.Text;
using Aevatar.Authentication.ScopeServiceTokens;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Infrastructure.Orchestration;

public sealed class ServiceExposureReconcileHook : ICommittedStatePublicationHook
{
    private readonly IServiceCatalogQueryReader _catalogReader;
    private readonly IServiceCommandPort _commandPort;
    private readonly IScopeServiceTokenKeyProvider? _scopeTokenKeyProvider;
    private readonly ServiceExternalExposureOptions _options;
    private readonly ILogger<ServiceExposureReconcileHook> _logger;

    public ServiceExposureReconcileHook(
        IServiceCatalogQueryReader catalogReader,
        IServiceCommandPort commandPort,
        IOptions<ServiceExternalExposureOptions> options,
        IScopeServiceTokenKeyProvider? scopeTokenKeyProvider = null,
        ILogger<ServiceExposureReconcileHook>? logger = null)
    {
        _catalogReader = catalogReader ?? throw new ArgumentNullException(nameof(catalogReader));
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _scopeTokenKeyProvider = scopeTokenKeyProvider;
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? NullLogger<ServiceExposureReconcileHook>.Instance;
    }

    public async Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_options.Enabled)
            return;

        var payload = context.Published.StateEvent?.EventData;
        if (payload == null)
            return;

        if (payload.Is(ServiceDeploymentActivatedEvent.Descriptor))
        {
            await HandleActivatedAsync(payload.Unpack<ServiceDeploymentActivatedEvent>(), ct);
            return;
        }

        if (payload.Is(ServiceDeploymentDeactivatedEvent.Descriptor))
            await HandleDeactivatedAsync(payload.Unpack<ServiceDeploymentDeactivatedEvent>(), ct);
    }

    private async Task HandleActivatedAsync(ServiceDeploymentActivatedEvent evt, CancellationToken ct)
    {
        if (evt.Status != ServiceDeploymentStatus.Active || evt.Identity == null)
            return;

        var service = await _catalogReader.GetAsync(evt.Identity, ct);
        if (service == null || !ShouldExpose(service))
            return;

        var openApiUrl = BuildOpenApiUrl(service.TenantId, service.AppId, service.Namespace, service.ServiceId);
        if (string.IsNullOrWhiteSpace(openApiUrl))
            return;

        var desiredHash = BuildDesiredSpecHash(service, openApiUrl);
        await _commandPort.ReconcileExternalExposureAsync(
            new ReconcileExternalExposureCommand
            {
                Identity = evt.Identity.Clone(),
                OpenapiUrl = openApiUrl,
                DesiredSpecHash = desiredHash,
                CredentialKid = _scopeTokenKeyProvider?.CurrentSigningKey.Kid ?? string.Empty,
            },
            ct);
    }

    private async Task HandleDeactivatedAsync(ServiceDeploymentDeactivatedEvent evt, CancellationToken ct)
    {
        if (evt.Identity == null)
            return;

        await _commandPort.RetireExternalExposureAsync(
            new RetireExternalExposureCommand
            {
                Identity = evt.Identity.Clone(),
            },
            ct);
    }

    private bool ShouldExpose(Aevatar.GAgentService.Abstractions.Queries.ServiceCatalogSnapshot service)
    {
        if (service.ExternalExposure?.ExposureDesired == true)
            return true;

        if (_options.RegisterAllPublishedServices)
            return true;

        var policyIds = _options.OptInPolicyIds ?? [];
        return policyIds.Length > 0 &&
               service.PolicyIds.Any(policy => policyIds.Contains(policy, StringComparer.Ordinal));
    }

    private string BuildOpenApiUrl(string tenantId, string appId, string @namespace, string serviceId)
    {
        var baseUrl = _options.PublicBaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            _logger.LogWarning("GAgent service external exposure is enabled but PublicBaseUrl is not a valid absolute URL.");
            return string.Empty;
        }

        return string.Concat(
            baseUrl,
            "/api/services/",
            Uri.EscapeDataString(serviceId),
            "/openapi.json?tenantId=",
            Uri.EscapeDataString(tenantId),
            "&appId=",
            Uri.EscapeDataString(appId),
            "&namespace=",
            Uri.EscapeDataString(@namespace));
    }

    private static string BuildDesiredSpecHash(
        Aevatar.GAgentService.Abstractions.Queries.ServiceCatalogSnapshot service,
        string openApiUrl)
    {
        using var sha = SHA256.Create();
        var buffer = new StringBuilder();
        buffer.Append(service.ServiceKey).Append('|')
            .Append(service.DisplayName).Append('|')
            .Append(openApiUrl).Append('|');
        foreach (var endpoint in service.Endpoints.OrderBy(x => x.EndpointId, StringComparer.Ordinal))
        {
            buffer.Append(endpoint.EndpointId).Append(':')
                .Append(endpoint.Kind).Append(':')
                .Append(endpoint.RequestTypeUrl).Append(':')
                .Append(endpoint.ResponseTypeUrl).Append(';');
        }

        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(buffer.ToString()))).ToLowerInvariant();
    }
}
