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

    /// <summary>
    /// When <c>true</c>, <c>invoke_service</c> returns <c>RequiresApproval=false</c>
    /// so the local tool approval middleware executes the invocation immediately.
    /// Defaults to false because service commands can advance actor-owned business state.
    /// </summary>
    public bool BypassInvokeApproval { get; set; }

    /// <summary>When true, tools resolve scope dynamically from AgentToolRequestContext at execution time,
    /// allowing registration even when TenantId/AppId/Namespace are not statically configured.</summary>
    public bool EnableDynamicScopeResolution { get; set; }
}
