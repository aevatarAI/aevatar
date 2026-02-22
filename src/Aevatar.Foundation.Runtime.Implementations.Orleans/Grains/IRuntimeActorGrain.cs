namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

public interface IRuntimeActorGrain : IGrainWithStringKey
{
    Task<bool> InitializeAgentAsync(string agentTypeName);

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

    Task DeactivateAsync();
}
