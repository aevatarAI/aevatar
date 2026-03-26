namespace Aevatar.AppPlatform.Infrastructure.Configuration;

public sealed class AppPlatformOptions
{
    public const string SectionName = "AppPlatform";

    public List<ConfiguredAppDefinitionOptions> Apps { get; set; } = [];
}

public sealed class ConfiguredAppDefinitionOptions
{
    public string AppId { get; set; } = string.Empty;

    public string OwnerScopeId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Visibility { get; set; } = string.Empty;

    public string DefaultReleaseId { get; set; } = string.Empty;

    public List<ConfiguredAppReleaseOptions> Releases { get; set; } = [];

    public List<ConfiguredAppRouteOptions> Routes { get; set; } = [];
}

public sealed class ConfiguredAppReleaseOptions
{
    public string ReleaseId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public List<ConfiguredAppServiceRefOptions> Services { get; set; } = [];

    public List<ConfiguredAppEntryRefOptions> Entries { get; set; } = [];

    public List<ConfiguredAppConnectorRefOptions> Connectors { get; set; } = [];

    public List<ConfiguredAppSecretRefOptions> Secrets { get; set; } = [];
}

public sealed class ConfiguredAppServiceRefOptions
{
    public string TenantId { get; set; } = string.Empty;

    public string AppId { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public string ServiceId { get; set; } = string.Empty;

    public string RevisionId { get; set; } = string.Empty;

    public string ImplementationKind { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;
}

public sealed class ConfiguredAppEntryRefOptions
{
    public string EntryId { get; set; } = string.Empty;

    public string ServiceId { get; set; } = string.Empty;

    public string EndpointId { get; set; } = string.Empty;
}

public sealed class ConfiguredAppConnectorRefOptions
{
    public string ResourceId { get; set; } = string.Empty;

    public string ConnectorName { get; set; } = string.Empty;
}

public sealed class ConfiguredAppSecretRefOptions
{
    public string ResourceId { get; set; } = string.Empty;

    public string SecretName { get; set; } = string.Empty;
}

public sealed class ConfiguredAppRouteOptions
{
    public string RoutePath { get; set; } = string.Empty;

    public string ReleaseId { get; set; } = string.Empty;

    public string EntryId { get; set; } = string.Empty;
}
