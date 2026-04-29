namespace Aevatar.GAgents.Channel.Abstractions.Slash;

/// <summary>
/// Pluggable slash-command handler registered through DI. Producers (e.g.
/// Channel.Identity for /init / /unbind / /whoami, NyxidChat for /model) ship
/// their own implementations; the inbound runner discovers them via
/// <c>IEnumerable&lt;IChannelSlashCommandHandler&gt;</c> and dispatches by name.
/// Unknown commands fall through to the LLM path so the legacy bot-owner-shared
/// experience keeps working.
/// </summary>
public interface IChannelSlashCommandHandler
{
    /// <summary>
    /// Canonical command name without the leading slash, e.g. <c>"init"</c>.
    /// Matched case-insensitively.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Optional case-insensitive aliases (without the leading slash). Default
    /// is empty.
    /// </summary>
    IReadOnlyList<string> Aliases => Array.Empty<string>();

    /// <summary>
    /// True when the handler must only run for senders with an active NyxID
    /// binding. <c>/init</c> and <c>/unbind</c> are bootstrap commands that
    /// must run while unbound; <c>/whoami</c>, <c>/model</c>, etc. need a
    /// binding so user-scoped state has somewhere to attach.
    /// </summary>
    bool RequiresBinding { get; }

    /// <summary>
    /// Produce a reply for the matched command. Returning <c>null</c> tells
    /// the runner the handler observed the command but chose not to act —
    /// the runner falls through as if no handler matched. Throwing surfaces
    /// as a user-visible error reply.
    /// </summary>
    Task<MessageContent?> HandleAsync(ChannelSlashCommandContext context, CancellationToken ct);
}

/// <summary>
/// Inputs passed to <see cref="IChannelSlashCommandHandler.HandleAsync"/>.
/// Designed to avoid pulling <c>InboundMessage</c> (Channel.Runtime) or
/// <c>BindingId</c> (Channel.Identity.Abstractions) into the abstraction
/// layer — handlers that need richer context resolve services via
/// <see cref="Services"/>.
/// </summary>
public sealed class ChannelSlashCommandContext
{
    /// <summary>
    /// Canonical command name (without leading slash, lowercased).
    /// </summary>
    public required string CommandName { get; init; }

    /// <summary>
    /// Text after the command and the first space, trimmed. Empty when the
    /// inbound was just <c>/cmd</c> with no further arguments.
    /// </summary>
    public required string ArgumentText { get; init; }

    /// <summary>
    /// Resolved external subject (platform / tenant / external_user_id) for
    /// the inbound sender. Always populated — the runner refuses to route a
    /// slash command when the subject cannot be resolved (so handlers do not
    /// need to defend against a null subject).
    /// </summary>
    public required ExternalSubjectRef Subject { get; init; }

    /// <summary>
    /// Active binding-id (NyxID-issued opaque string) for the sender, or
    /// <c>null</c> when the sender has no binding yet. <see cref="IChannelSlashCommandHandler.RequiresBinding"/>
    /// guarantees this is non-null when the handler runs with that flag set.
    /// </summary>
    public string? BindingIdValue { get; init; }

    /// <summary>
    /// Originating bot registration id — useful when a handler needs to look
    /// up registration-scoped state (e.g. /model querying provider list for
    /// a specific bot).
    /// </summary>
    public required string RegistrationId { get; init; }

    /// <summary>
    /// Originating bot registration scope id (typically the bot owner's
    /// NyxID scope). Handlers reading bot-owner config (e.g. /model showing
    /// the inheriting default) start from this scope.
    /// </summary>
    public required string RegistrationScopeId { get; init; }

    /// <summary>
    /// Raw sender identifier from the inbound (platform-specific, e.g. Lark
    /// open_id). Already trimmed.
    /// </summary>
    public required string SenderId { get; init; }

    /// <summary>
    /// Sender display name as carried by the platform, or empty.
    /// </summary>
    public required string SenderName { get; init; }

    /// <summary>
    /// True when the inbound originated from a 1:1 chat with the bot. /init
    /// in particular refuses to emit the authorize URL outside private chats
    /// to avoid leaking the sealed state token to other group members.
    /// </summary>
    public required bool IsPrivateChat { get; init; }

    /// <summary>
    /// Request-scoped service provider so handlers can resolve heavy
    /// dependencies (broker, query ports, prefs store) without inflating
    /// constructor surface.
    /// </summary>
    public required IServiceProvider Services { get; init; }
}
