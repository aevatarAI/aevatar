using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.Foundation.Abstractions.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAevatarConfig_ShouldRegisterCredentialProviderServices()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aevatar-config-tests-{Guid.NewGuid():N}");
        using var scope = new EnvironmentVariableScope(AevatarPaths.HomeEnv, tempRoot);
        var services = new ServiceCollection();

        services.AddAevatarConfig();

        services.Any(descriptor =>
                descriptor.ServiceType == typeof(ICredentialProvider) &&
                descriptor.ImplementationType == typeof(SecretsStoreCredentialProvider))
            .ShouldBeTrue();
        services.Any(descriptor => descriptor.ServiceType == typeof(SecretsStoreCredentialProvider))
            .ShouldBeTrue();
        Directory.Exists(Path.Combine(tempRoot, "agents")).ShouldBeTrue();
        Directory.Exists(Path.Combine(tempRoot, "skills")).ShouldBeTrue();
    }

    [Fact]
    public void AddAevatarConfig_ByDefault_ShouldRegisterFileBackedSecretsStore()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aevatar-config-tests-{Guid.NewGuid():N}");
        using var scope = new EnvironmentVariableScope(AevatarPaths.HomeEnv, tempRoot);
        var services = new ServiceCollection();

        services.AddAevatarConfig();

        services.Any(d =>
                d.ServiceType == typeof(IAevatarSecretsStore) &&
                d.ImplementationType == typeof(AevatarSecretsStore))
            .ShouldBeTrue();
        services.Any(d => d.ImplementationType == typeof(EnvironmentSecretsStore))
            .ShouldBeFalse();
    }

    [Fact]
    public void AddAevatarConfig_WhenLocalFileStoreDisabled_ShouldRegisterEnvironmentSecretsStore()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aevatar-config-tests-{Guid.NewGuid():N}");
        using var scope = new EnvironmentVariableScope(AevatarPaths.HomeEnv, tempRoot);
        var services = new ServiceCollection();

        services.AddAevatarConfig(allowLocalFileStore: false);

        services.Any(d =>
                d.ServiceType == typeof(IAevatarSecretsStore) &&
                d.ImplementationType == typeof(EnvironmentSecretsStore))
            .ShouldBeTrue();
        services.Any(d => d.ImplementationType == typeof(AevatarSecretsStore))
            .ShouldBeFalse();
    }

    [Fact]
    public void AddAevatarConfig_WhenLocalFileStoreDisabled_ShouldNotCreateLocalDirectoryTree()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aevatar-config-tests-{Guid.NewGuid():N}");
        using var scope = new EnvironmentVariableScope(AevatarPaths.HomeEnv, tempRoot);
        var services = new ServiceCollection();

        services.AddAevatarConfig(allowLocalFileStore: false);

        Directory.Exists(tempRoot).ShouldBeFalse();
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previous);
        }
    }
}
