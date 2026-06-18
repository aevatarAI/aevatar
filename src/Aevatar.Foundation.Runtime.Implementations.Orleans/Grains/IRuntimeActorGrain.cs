using Orleans.Concurrency;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

public interface IRuntimeActorGrain : IGrainWithStringKey
{
    /// <summary>
    /// Initializes the grain with a stable business <c>AgentKind</c> token.
    /// Persists the kind on <c>RuntimeActorIdentity</c> directly, so future
    /// activations do not consult runtime-incidental CLR type names.
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

    /// <summary>
    /// Returns the resolved business kind once the grain has initialized.
    /// Empty when no implementation is bound.
    /// </summary>
    Task<string> GetAgentKindAsync();

    Task DeactivateAsync();

    Task PurgeAsync();
}
