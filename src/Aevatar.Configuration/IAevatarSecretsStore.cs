namespace Aevatar.Configuration;

public interface IAevatarSecretsStore
{
    string? Get(string key);
    string? GetApiKey(string providerName);
    string? GetDefaultProvider();
    IReadOnlyDictionary<string, string> GetAll();
    void Set(string key, string value);
    void Remove(string key);
}
