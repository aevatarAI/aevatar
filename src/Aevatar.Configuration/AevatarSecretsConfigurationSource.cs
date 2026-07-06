using Microsoft.Extensions.Configuration;

namespace Aevatar.Configuration;

internal sealed class AevatarSecretsConfigurationSource : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new AevatarSecretsConfigurationProvider();
}

internal sealed class AevatarSecretsConfigurationProvider : ConfigurationProvider
{
    public override void Load()
    {
        var store = new AevatarSecretsStore();
        Data = store.GetAll().ToDictionary(
            pair => pair.Key,
            pair => (string?)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }
}
