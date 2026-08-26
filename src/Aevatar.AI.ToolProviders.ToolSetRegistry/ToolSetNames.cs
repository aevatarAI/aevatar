namespace Aevatar.AI.ToolProviders.ToolSetRegistry;

public static class ToolSetNames
{
    public const string ChatCore = "chat.core";
    public const string WebRuntime = "web.runtime";
    public const string SkillRuntime = "skill.runtime";
    public const string SkillAuthoring = "skill.authoring";
    public const string AevatarInvoke = "aevatar.invoke";
    public const string AevatarObserve = "aevatar.observe";
    public const string ResponsesState = "responses.state";
    public const string NyxIdPrivileged = "nyxid.privileged";
    public const string NyxIdExecution = "nyxid.execution";
    public const string StorageRead = "storage.read";
    public const string StorageWrite = "storage.write";
    /// <summary>
    /// Channel-agnostic reply, registration, and delivery-target tools shared by every channel
    /// set. Kept as its own set so a route naming more than one channel materializes them once.
    /// </summary>
    public const string ChannelCore = "channel.core";

    public const string ChannelLark = "channel.lark";
    public const string ChannelTelegram = "channel.telegram";
    public const string WorkspaceDefault = "workspace.default";
    public const string LarkSelfNotify = "lark.self_notify";

    /// <summary>
    /// Baseline NyxID Assistant tools exposed on ordinary, unprofiled chat turns. Intent-only
    /// tool sets are composed into the Agent Profile authority route separately and are never
    /// folded into this default turn surface.
    /// </summary>
    public const string NyxIdChatDefault = "nyxid.chat.default";

    /// <summary>
    /// The reviewed unprofiled-baseline slice of the NyxID chat surface: only
    /// the dependency-light sources that provide the pinned Class-R reads, the
    /// service readiness gate, typed user input, and explicit skill
    /// discovery/loading. Kept separate from the full route ceiling so one
    /// unavailable heavyweight source cannot fail the ordinary turn baseline
    /// closed at resolve time.
    /// </summary>
    public const string NyxIdChatBaseline = "nyxid.chat.baseline";

    /// <summary>
    /// Read-only external capability discovery, readiness, and explicit-request preview used by
    /// workflow authoring surfaces.
    /// </summary>
    public const string WorkflowExternalCapabilityAuthoring =
        "workflow.external-capability-authoring";

    /// <summary>
    /// Studio-owned provisioning, member, binding, schedule, and query tools. This set is
    /// opt-in and must not be included by public/default route tool sets.
    /// </summary>
    public const string StudioLocal = "studio.local";

    /// <summary>
    /// Opt-in tool set that exposes the caller's <c>x-aevatar-tool</c>-marked NyxID
    /// connected-service operations as individual tools. Kept out of
    /// <see cref="WorkspaceDefault"/> so connected services are only injected when a route
    /// policy explicitly selects this set.
    /// </summary>
    public const string NyxIdConnectedServices = "nyxid.connected_services";

    /// <summary>
    /// Pinned local NyxID Assistant tools used to materialize built-in admission intents.
    /// This set deliberately excludes request-local connected-service discovery so an
    /// unavailable external inventory cannot suppress local typed admission actions.
    /// </summary>
    public const string NyxIdAssistantAdmission = "nyxid.assistant.admission";
}
