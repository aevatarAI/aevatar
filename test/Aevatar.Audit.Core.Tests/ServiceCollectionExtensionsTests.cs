using System.Text;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Core.DependencyInjection;
using Aevatar.Audit.Core.Identity;
using Aevatar.Audit.Core.Projection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Aevatar.Audit.Core.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAuditTrailCore_RegistersHasherAndDevelopmentStoreExplicitly()
    {
        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes("audit identity key material for tests"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AuditActorIdentityHasherOptions.SectionName}:ActiveKeyId"] = "key-1",
                [$"{AuditActorIdentityHasherOptions.SectionName}:Keys:0:KeyId"] = "key-1",
                [$"{AuditActorIdentityHasherOptions.SectionName}:Keys:0:KeyBase64"] = key
            })
            .Build();

        var provider = new ServiceCollection()
            .AddAuditTrailCore(configuration)
            .AddInMemoryAuditTrailForDevelopment()
            .BuildServiceProvider();

        provider.GetRequiredService<IAuditActorIdentityHasher>().ShouldNotBeNull();
        provider.GetRequiredService<IAuditTrailAppender>().ShouldBeSameAs(provider.GetRequiredService<IAuditTrailQueryPort>());
        provider.GetRequiredService<IAuditTrailArtifactStore>().ShouldBeSameAs(provider.GetRequiredService<IAuditTrailAppender>());
    }

    [Fact]
    public void AddAuditTrailCore_FailsClosed_WhenHasherOptionsAreMissing()
    {
        var provider = new ServiceCollection()
            .AddAuditTrailCore(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() =>
            provider.GetRequiredService<IAuditActorIdentityHasher>());
    }
}
