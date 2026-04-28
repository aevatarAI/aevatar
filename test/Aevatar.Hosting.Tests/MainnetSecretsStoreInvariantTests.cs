using Aevatar.Bootstrap.Hosting;
using Aevatar.Configuration;
using Aevatar.Mainnet.Host.Api.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Hosting.Tests;

public sealed class MainnetSecretsStoreInvariantTests
{
    [Fact]
    public void AddAevatarMainnetHost_ShouldRegisterEnvironmentSecretsStore_RegardlessOfCallerOverride()
    {
        using var home = new TemporaryAevatarHomeScope();
        using var runtimeProvider = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__Provider", "InMemory");

        var builder = CreateBuilder();

        // Caller deliberately tries to flip the flag back on. The mainnet
        // bootstrap must enforce false AFTER configureHost runs so this is
        // ignored — otherwise mainnet would silently re-enable the file store.
        builder.AddAevatarMainnetHost(options =>
        {
            options.AllowLocalFileSecretsStore = true;
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        var hostOptions = builder.Services
            .BuildServiceProvider()
            .GetRequiredService<AevatarDefaultHostOptions>();
        hostOptions.AllowLocalFileSecretsStore.Should().BeFalse();

        // The DI registration must reflect the enforced invariant.
        var descriptor = builder.Services.Single(d => d.ServiceType == typeof(IAevatarSecretsStore));
        descriptor.ImplementationType.Should().Be(typeof(EnvironmentSecretsStore));
    }

    private static WebApplicationBuilder CreateBuilder() =>
        WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

    private sealed class TemporaryAevatarHomeScope : IDisposable
    {
        private readonly string? _previous;
        private readonly string _path;

        public TemporaryAevatarHomeScope()
        {
            _previous = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
            _path = Path.Combine(Path.GetTempPath(), $"aevatar-mainnet-invariant-{Guid.NewGuid():N}");
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, _path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, _previous);
            if (Directory.Exists(_path))
                Directory.Delete(_path, recursive: true);
        }
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
