// Adapter so config tool API (TryGet/Set/Remove/GetAll) can use Aevatar.Config.AevatarSecretsStore.

namespace Aevatar.Tools.Config;

/// <summary>Secrets store interface used by the config tool API (LLM keys, etc.).</summary>
public interface ISecretsStore
{
    bool TryGet(string key, out string? value);
    void Set(string key, string value);
    bool Remove(string key);
    IReadOnlyDictionary<string, string> GetAll();
}

/// <summary>Wraps Aevatar.Config.AevatarSecretsStore for the config tool.</summary>
public sealed class SecretsStoreAdapter : ISecretsStore
{
    private readonly Aevatar.Config.AevatarSecretsStore _store;

    public SecretsStoreAdapter(Aevatar.Config.AevatarSecretsStore store) => _store = store;

    public bool TryGet(string key, out string? value)
    {
        value = _store.Get(key);
        return value != null;
    }

    public void Set(string key, string value) => _store.Set(key, value);

    public bool Remove(string key)
    {
        if (!_store.GetAll().ContainsKey(key)) return false;
        _store.Remove(key);
        return true;
    }

    public IReadOnlyDictionary<string, string> GetAll() => _store.GetAll();
}
