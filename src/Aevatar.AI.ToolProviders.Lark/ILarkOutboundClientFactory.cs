using Aevatar.AI.ToolProviders.NyxId;

namespace Aevatar.AI.ToolProviders.Lark;

/// <summary>
/// Builds Lark CardKit / im clients bound to a specific NyxID outbound proxy slug.
/// </summary>
/// <remarks>
/// The Lark wire clients (<see cref="ILarkCardKitClient"/> / <see cref="ILarkNyxClient"/>) bake the
/// proxy slug into their options, so the DI-registered singletons are pinned to one slug (the
/// configured default, <c>api-lark-bot</c>). A relayed reply must instead proxy through the slug of
/// the channel-bot that received the inbound turn (e.g. <c>api-lark-bot-4</c>), otherwise a DM to one
/// bot is delivered by a sibling bot under the same NyxID account. This factory lets the per-turn
/// reply path obtain wire clients bound to that inbound slug while keeping the single shared wire
/// implementation. When the slug is null/empty the factory yields the default-slug clients.
/// </remarks>
public interface ILarkOutboundClientFactory
{
    /// <summary>
    /// Returns a CardKit client whose proxy calls target <paramref name="providerSlug"/>. A
    /// null/blank slug yields the configured-default client.
    /// </summary>
    ILarkCardKitClient ResolveCardKitClient(string? providerSlug);

    /// <summary>
    /// Returns an im/messages client whose proxy calls target <paramref name="providerSlug"/>. A
    /// null/blank slug yields the configured-default client.
    /// </summary>
    ILarkNyxClient ResolveNyxClient(string? providerSlug);
}

/// <summary>
/// Default <see cref="ILarkOutboundClientFactory"/>. Reuses the configured-default singleton clients
/// when the requested slug matches the configured default (or is absent), and otherwise constructs a
/// per-slug client over the shared <see cref="NyxIdApiClient"/>. Construction is the only
/// per-slug work; no business state is held, so building on demand keeps the slug as a pure routing
/// derivation rather than a process-local fact source.
/// </summary>
public sealed class LarkOutboundClientFactory : ILarkOutboundClientFactory
{
    private readonly LarkToolOptions _options;
    private readonly NyxIdApiClient _nyxClient;
    private readonly ILarkCardKitClient _defaultCardKitClient;
    private readonly ILarkNyxClient _defaultNyxClient;

    public LarkOutboundClientFactory(
        LarkToolOptions options,
        NyxIdApiClient nyxClient,
        ILarkCardKitClient defaultCardKitClient,
        ILarkNyxClient defaultNyxClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _defaultCardKitClient = defaultCardKitClient ?? throw new ArgumentNullException(nameof(defaultCardKitClient));
        _defaultNyxClient = defaultNyxClient ?? throw new ArgumentNullException(nameof(defaultNyxClient));
    }

    public ILarkCardKitClient ResolveCardKitClient(string? providerSlug) =>
        UsesDefaultSlug(providerSlug)
            ? _defaultCardKitClient
            : new LarkCardKitClient(WithSlug(providerSlug!), _nyxClient);

    public ILarkNyxClient ResolveNyxClient(string? providerSlug) =>
        UsesDefaultSlug(providerSlug)
            ? _defaultNyxClient
            : new LarkNyxClient(WithSlug(providerSlug!), _nyxClient);

    private bool UsesDefaultSlug(string? providerSlug)
    {
        var slug = providerSlug?.Trim();
        return string.IsNullOrEmpty(slug) ||
               string.Equals(slug, _options.ProviderSlug?.Trim(), StringComparison.Ordinal);
    }

    private LarkToolOptions WithSlug(string providerSlug) => new()
    {
        ProviderSlug = providerSlug.Trim(),
        EnableMessageSend = _options.EnableMessageSend,
        EnableMessageReply = _options.EnableMessageReply,
        EnableMessageReactionCreate = _options.EnableMessageReactionCreate,
        EnableMessageReactionList = _options.EnableMessageReactionList,
        EnableMessageReactionDelete = _options.EnableMessageReactionDelete,
        EnableMessageBatchGet = _options.EnableMessageBatchGet,
        EnableChatLookup = _options.EnableChatLookup,
        EnableSheetsAppendRows = _options.EnableSheetsAppendRows,
        EnableApprovalsList = _options.EnableApprovalsList,
        EnableApprovalsGet = _options.EnableApprovalsGet,
        EnableApprovalsAct = _options.EnableApprovalsAct,
        EnableDocxCreate = _options.EnableDocxCreate,
        EnableBaseCreate = _options.EnableBaseCreate,
        EnableResourceGrant = _options.EnableResourceGrant,
        EnableWorkflowFileSubmit = _options.EnableWorkflowFileSubmit,
    };
}
