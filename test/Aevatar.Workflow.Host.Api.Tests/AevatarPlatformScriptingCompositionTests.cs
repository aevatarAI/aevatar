using Aevatar.Workflow.Extensions.Hosting;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class AevatarPlatformScriptingCompositionTests
{
    [Fact]
    public void AevatarPlatformCompositionOptions_ShouldDisableScriptingByDefault()
    {
        // Security lockdown: scripting executes tenant-supplied C# in-process via Roslyn,
        // so composing it must be an explicit host opt-in, never the platform default.
        new AevatarPlatformCompositionOptions().EnableScriptingCapability.Should().BeFalse();
    }
}
