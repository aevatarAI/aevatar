namespace Aevatar.AI.ToolProviders.ServiceInvoke;

public sealed class ServiceInvokeOptions
{
    /// <summary>Default tenant ID for service discovery scope.</summary>
    public string? TenantId { get; set; }

    /// <summary>Default app ID for service discovery scope.</summary>
    public string? AppId { get; set; }

    /// <summary>Default namespace for service discovery scope.</summary>
    public string? Namespace { get; set; }

    /// <summary>Maximum services to return from list_services.</summary>
    public int MaxListResults { get; set; } = 200;

    /// <summary>Whether to enable the invoke_service tool.</summary>
    public bool EnableInvoke { get; set; } = true;
}
