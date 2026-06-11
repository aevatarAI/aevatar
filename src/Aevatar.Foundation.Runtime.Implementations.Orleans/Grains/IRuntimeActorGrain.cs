using Orleans.Concurrency;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

public interface IRuntimeActorGrain : IGrainWithStringKey
{
    /// <summary>
    /// Initializes the grain with a runtime CLR full name. Preserved for
    /// callers (tests, bootstrap harnesses) that have not yet migrated to
    /// kind-based identity; the grain still routes the lookup through
    /// <c>IAgentKindRegistry</c> + the transitional reflection fallback.
    /// New code should prefer <see cref="InitializeAgentByKindAsync(string)"/>.
    /// </summary>
    Task<bool> InitializeAgentAsync(string agentTypeName);

    /// <summary>
    /// Initializes the grain with a stable business <c>AgentKind</c> token.
    /// Persists the kind on <c>RuntimeActorIdentity</c> directly, so future
    /// activations do not consult the legacy CLR-name fallback.
    /// </summary>
    Task<bool> InitializeAgentByKindAsync(string kind);

    [AlwaysInterleave]
    Task<bool> IsInitializedAsync();

    Task HandleEnvelopeAsync(byte[] envelopeBytes);

    Task AddChildAsync(string childId);

    Task RemoveChildAsync(string childId);

    Task SetParentAsync(string parentId);

    Task ClearParentAsync();

    Task<IReadOnlyList<string>> GetChildrenAsync();

    Task<string?> GetParentAsync();

    Task<string> GetDescriptionAsync();

    Task<string> GetAgentTypeNameAsync();

    /// <summary>
    /// Returns the resolved business kind once the grain has initialized
    /// (either via <see cref="InitializeAgentByKindAsync(string)"/> or via
    /// the lazy-tag pass on legacy <c>AgentTypeName</c> data). Empty when
    /// no implementation is bound or when the legacy CLR-name fallback was
    /// used without a registered kind.
    /// </summary>
    Task<string> GetAgentKindAsync();

    Task DeactivateAsync();

    Task PurgeAsync();
}
