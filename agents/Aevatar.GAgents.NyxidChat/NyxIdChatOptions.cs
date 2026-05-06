namespace Aevatar.GAgents.NyxidChat;

public sealed class NyxIdChatOptions
{
    /// <summary>
    /// Enables per-turn remote skill auto-loading for Lark/Feishu inbound chat.
    /// The loaded skills are kept in the current LLM turn only.
    /// </summary>
    public bool LarkRemoteSkillAutoLoadEnabled { get; set; } = true;

    /// <summary>
    /// Maximum number of remote skills to pull into a single Lark/Feishu LLM turn.
    /// </summary>
    public int LarkRemoteSkillAutoLoadMaxSkills { get; set; } = 2;

    /// <summary>
    /// Remote skill search mode used by Lark/Feishu auto-loading.
    /// Supported values follow the remote provider contract: keyword or semantic.
    /// </summary>
    public string LarkRemoteSkillAutoLoadSearchMode { get; set; } = "semantic";

    /// <summary>
    /// Timeout for the best-effort remote skill auto-load phase before the LLM call.
    /// </summary>
    public int LarkRemoteSkillAutoLoadTimeoutSeconds { get; set; } = 3;
}
