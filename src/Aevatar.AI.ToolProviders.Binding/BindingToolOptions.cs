namespace Aevatar.AI.ToolProviders.Binding;

/// <summary>Configuration for binding agent tools.</summary>
public sealed class BindingToolOptions
{
    /// <summary>Maximum results returned by binding_list (default: 100).</summary>
    public int MaxListResults { get; set; } = 100;
}
