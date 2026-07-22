using System.Net.Http;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioOwnerLLMServiceIdentityResolver(
    IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider = null,
    IUserLlmCatalogPort? userLlmCatalogPort = null)
    : IScheduledInvocationOwnerLLMServiceIdentityResolver
{
    private const string OwnerLLMCapabilityScope = "proxy";

    public async Task<string> ResolveAsync(
        ScheduledInvocationOwnerLLMEvidence evidence,
        AuthenticatedAuthorizationOwnerContext ownerContext,
        CancellationToken ct = default)
    {
        if (callerAccessTokenProvider == null || userLlmCatalogPort == null)
            return string.Empty;

        var bindingId = NormalizeOptional(ownerContext.VerifiedBindingId);
        var platform = NormalizeOptional(ownerContext.SubjectPlatform);
        var externalUserId = NormalizeOptional(ownerContext.SubjectExternalUserId);
        if (bindingId == null || platform == null || externalUserId == null)
            return string.Empty;

        try
        {
            var bearerToken = await callerAccessTokenProvider.IssueAsync(
                new WorkflowCallerNyxIdAuthority
                {
                    Platform = platform,
                    Tenant = NormalizeOptional(ownerContext.SubjectTenant) ?? string.Empty,
                    ExternalUserId = externalUserId,
                    Scope = OwnerLLMCapabilityScope,
                    BindingId = bindingId,
                },
                ct);
            var catalog = await userLlmCatalogPort.GetServicesAsync(bearerToken, ct);
            var route = NormalizeOptional(evidence.NyxIdRoute);
            if (route != null)
            {
                return ResolveSingleServiceId(catalog.Services
                    .Where(service => UserLlmCatalogNormalization.IsReady(service))
                    .Where(service => string.Equals(
                        NormalizeOptional(service.RouteValue),
                        route,
                        StringComparison.OrdinalIgnoreCase)));
            }

            var serviceSlug = NormalizeOptional(evidence.NyxIdServiceSlug);
            if (serviceSlug == null)
                return string.Empty;

            return ResolveSingleServiceId(catalog.Services
                .Where(service => UserLlmCatalogNormalization.IsReady(service))
                .Where(service => string.Equals(
                    NormalizeOptional(service.ServiceSlug),
                    serviceSlug,
                    StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception exception) when (IsExternalResolutionFailure(exception))
        {
            return string.Empty;
        }
    }

    private static bool IsExternalResolutionFailure(Exception exception) =>
        exception is HttpRequestException or UnauthorizedAccessException or InvalidOperationException or TimeoutException;

    private static string ResolveSingleServiceId(IEnumerable<NyxIdLlmService> services)
    {
        var serviceIds = services
            .Select(static service => NormalizeOptional(service.UserServiceId))
            .Where(static serviceId => serviceId != null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return serviceIds.Length == 1 ? serviceIds[0]! : string.Empty;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
