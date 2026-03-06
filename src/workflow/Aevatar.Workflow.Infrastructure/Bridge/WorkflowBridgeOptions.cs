using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Infrastructure.Bridge;

public sealed class WorkflowBridgeOptions
{
    public string BridgeActorId { get; set; } = "bridge:default";

    /// <summary>
    /// Optional bridge actor CLR type name used by /api/bridge/callbacks.
    /// Supports assembly-qualified type names and loaded-type aliases (simple name/full name).
    /// Defaults to <see cref="BridgeGAgent"/>.
    /// </summary>
    public string BridgeAgentType { get; set; } = string.Empty;

    public string TokenSigningKey { get; set; } = string.Empty;

    public int DefaultTokenTtlMs { get; set; } = 60_000;

    public int MaxTokenTtlMs { get; set; } = 3_600_000;

    public bool RequireSourceAllowList { get; set; } = true;

    public List<string> AllowedSources { get; set; } =
    [
        "telegram.openclaw",
    ];
}
