namespace Aevatar.GAgentService.Abstractions.Services;

public static class ServiceKeys
{
    public static string Build(ServiceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Build(
            identity.TenantId,
            identity.AppId,
            identity.Namespace,
            identity.ServiceId);
    }

    public static string Build(
        string tenantId,
        string appId,
        string @namespace,
        string serviceId)
    {
        var normalizedTenantId = Normalize(tenantId, nameof(tenantId));
        var normalizedAppId = Normalize(appId, nameof(appId));
        var normalizedNamespace = Normalize(@namespace, nameof(@namespace));
        var normalizedServiceId = Normalize(serviceId, nameof(serviceId));
        return string.Join(":", normalizedTenantId, normalizedAppId, normalizedNamespace, normalizedServiceId);
    }

    private static string Normalize(string value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");

        return normalized;
    }
}
