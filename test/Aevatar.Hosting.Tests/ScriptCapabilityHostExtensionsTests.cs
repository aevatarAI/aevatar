using Aevatar.Hosting;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Hosting.CapabilityApi;
using Aevatar.Scripting.Hosting.DependencyInjection;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Hosting.Tests;

public class ScriptCapabilityHostExtensionsTests
{
    [Fact]
    public void AddScriptCapability_ShouldRegisterCapabilityAndValidateNull()
    {
        Action act = () => ScriptCapabilityHostBuilderExtensions.AddScriptCapability(null!);
        act.Should().Throw<ArgumentNullException>();

        var builder = WebApplication.CreateBuilder();
        var returned = builder.AddScriptCapability();

        returned.Should().BeSameAs(builder);
        var registrations = builder.Services
            .Where(x => x.ServiceType == typeof(AevatarCapabilityRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AevatarCapabilityRegistration>()
            .ToList();
        registrations.Should().ContainSingle(x => x.Name == "script");
    }

    [Fact]
    public void AddScriptCapabilityServices_ShouldRegisterCoreScriptServices()
    {
        var services = new ServiceCollection();

        services.AddScriptCapability();

        services.Should().Contain(x =>
            x.ServiceType == typeof(ScriptSandboxPolicy) &&
            x.ImplementationType == typeof(ScriptSandboxPolicy));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IScriptAgentCompiler) &&
            x.ImplementationType == typeof(RoslynScriptAgentCompiler));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IAICapability));
    }
}
