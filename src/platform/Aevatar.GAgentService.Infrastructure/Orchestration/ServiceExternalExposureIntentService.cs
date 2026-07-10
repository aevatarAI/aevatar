using System.Security.Cryptography;
using System.Text;
using Aevatar.Authentication.ScopeServiceTokens;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Infrastructure.Orchestration;

public sealed class ServiceExternalExposureIntentService : IServiceExternalExposureIntentPort
{
    private readonly IServiceCatalogQueryReader _catalogReader;
    private readonly IServiceCommandPort _commandPort;
    private readonly IScopeServiceTokenKeyProvider? _scopeTokenKeyProvider;
    private readonly ServiceExternalExposureOptions _options;
    private readonly ILogger<ServiceExternalExposureIntentService> _logger;

    public ServiceExternalExposureIntentService(
        IServiceCatalogQueryReader catalogReader,
        IServiceCommandPort commandPort,
        IOptions<ServiceExternalExposureOptions> options,
        IScopeServiceTokenKeyProvider? scopeTokenKeyProvider = null,
        ILogger<ServiceExternalExposureIntentService>? logger = null)
    {
        _catalogReader = catalogReader ?? throw new ArgumentNullException(nameof(catalogReader));
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _scopeTokenKeyProvider = scopeTokenKeyProvider;
        _logger = logger ?? NullLogger<ServiceExternalExposureIntentService>.Instance;
    }

    public async Task ApplyAsync(
        ServiceExternalExposureIntentRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Identity);

        if (request.ExposureDesired)
        {
            var service = BuildDesiredServiceSnapshot(request)
                ?? await _catalogReader.GetAsync(request.Identity, ct)
                ?? request.ExistingService;
            if (service == null)
                return;

            await ReconcileAsync(request.Identity, service, ct);
            return;
        }

        if (request.ExistingService == null)
            return;

        await RetireAsync(
            request.Identity,
            request.ExistingService.ExternalExposure?.DesiredSpecHash ?? string.Empty,
            ct);
    }

    public async Task ReconcileAsync(
        ServiceIdentity identity,
        ServiceCatalogSnapshot service,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(service);
        if (!_options.Enabled)
            return;

        var openApiUrl = BuildOpenApiUrl(service.TenantId, service.AppId, service.Namespace, service.ServiceId);
        if (string.IsNullOrWhiteSpace(openApiUrl))
            return;

        await _commandPort.ReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = openApiUrl,
            DesiredSpecHash = BuildDesiredSpecHash(service, openApiUrl),
            CredentialKid = _scopeTokenKeyProvider?.CurrentSigningKey.Kid ?? string.Empty,
        }, ct);
    }

    public bool ShouldExpose(ServiceCatalogSnapshot service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (service.ExternalExposure?.ExposureDesired == true)
            return true;

        if (_options.RegisterAllPublishedServices)
            return true;

        var policyIds = _options.OptInPolicyIds ?? [];
        return policyIds.Length > 0 &&
               service.PolicyIds.Any(policy => policyIds.Contains(policy, StringComparer.Ordinal));
    }

    public Task RetireAsync(
        ServiceIdentity identity,
        string desiredSpecHash = "",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _commandPort.RetireExternalExposureAsync(new RetireExternalExposureCommand
        {
            Identity = identity.Clone(),
            DesiredSpecHash = desiredSpecHash ?? string.Empty,
        }, ct);
    }

    private static ServiceCatalogSnapshot? BuildDesiredServiceSnapshot(ServiceExternalExposureIntentRequest request)
    {
        if (request.DesiredDefinition == null)
            return null;

        var existing = request.ExistingService;
        return new ServiceCatalogSnapshot(
            existing?.ServiceKey ?? ServiceKeys.Build(request.Identity),
            request.Identity.TenantId,
            request.Identity.AppId,
            request.Identity.Namespace,
            request.Identity.ServiceId,
            request.DesiredDefinition.DisplayName ?? string.Empty,
            existing?.DefaultServingRevisionId ?? string.Empty,
            existing?.ActiveServingRevisionId ?? string.Empty,
            existing?.DeploymentId ?? string.Empty,
            existing?.PrimaryActorId ?? string.Empty,
            existing?.DeploymentStatus ?? string.Empty,
            request.DesiredDefinition.Endpoints.Select(ToEndpointSnapshot).ToArray(),
            request.DesiredDefinition.PolicyIds.ToArray(),
            existing?.UpdatedAt ?? DateTimeOffset.UtcNow,
            request.DesiredDefinition.ExternalExposure == null
                ? existing?.ExternalExposure
                : new ServiceExternalExposureSnapshot(
                    request.DesiredDefinition.ExternalExposure.NyxidSlug,
                    request.DesiredDefinition.ExternalExposure.RegisteredAt?.ToDateTimeOffset(),
                    request.DesiredDefinition.ExternalExposure.Status,
                    request.DesiredDefinition.ExternalExposure.NyxidServiceId,
                    request.DesiredDefinition.ExternalExposure.DesiredSpecHash,
                    request.DesiredDefinition.ExternalExposure.RegisteredSpecHash,
                    request.DesiredDefinition.ExternalExposure.LastError,
                    request.DesiredDefinition.ExternalExposure.Attempt,
                    request.DesiredDefinition.ExternalExposure.NextAttemptAt?.ToDateTimeOffset(),
                    request.DesiredDefinition.ExternalExposure.CredentialKid,
                    request.DesiredDefinition.ExternalExposure.ExposureDesired));
    }

    private static ServiceEndpointSnapshot ToEndpointSnapshot(ServiceEndpointSpec endpoint) =>
        new(
            endpoint.EndpointId,
            endpoint.DisplayName,
            endpoint.Kind.ToString(),
            endpoint.RequestTypeUrl,
            endpoint.ResponseTypeUrl,
            endpoint.Description);

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
        ServiceCatalogSnapshot service,
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
