// ─────────────────────────────────────────────────────────────
// Constants - shared constants for the Orleans runtime.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Orleans;

/// <summary>Shared constants for Orleans runtime configuration.</summary>
public static class Constants
{
    /// <summary>Grain state storage provider name.</summary>
    public const string GrainStorageName = "aevatar-grain-state";

    /// <summary>Metadata key for publisher chain (loop protection).</summary>
    public const string PublishersMetadataKey = "__publishers";

    /// <summary>MassTransit queue name prefix for agent events.</summary>
    public const string AgentEventQueuePrefix = "aevatar-agent-";

    /// <summary>MassTransit receive endpoint name for agent events.</summary>
    public const string AgentEventEndpoint = "aevatar-agent-events";
}
