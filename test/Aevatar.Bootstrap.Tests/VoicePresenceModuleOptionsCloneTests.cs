using System.Reflection;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Foundation.VoicePresence;
using FluentAssertions;

namespace Aevatar.Bootstrap.Tests;

public sealed class VoicePresenceModuleOptionsCloneTests
{
    [Fact]
    public void CloneVoicePresenceModuleOptions_ShouldPreserveConfiguredDrainTimeout()
    {
        var options = new VoicePresenceModuleOptions
        {
            Name = "voice_presence",
            DrainTimeout = TimeSpan.FromSeconds(13),
            DirectExternalEventTypeUrls =
            [
                "type.googleapis.com/example.External",
            ],
        };

        var clone = InvokeCloneVoicePresenceModuleOptions(options, "voice_presence_openai");

        clone.Name.Should().Be("voice_presence_openai");
        clone.DrainTimeout.Should().Be(TimeSpan.FromSeconds(13));
        clone.DirectExternalEventTypeUrls
            .Should()
            .ContainSingle("type.googleapis.com/example.External");
    }

    private static VoicePresenceModuleOptions InvokeCloneVoicePresenceModuleOptions(
        VoicePresenceModuleOptions options,
        string resolvedName)
    {
        var method = typeof(global::Aevatar.Bootstrap.Extensions.AI.ServiceCollectionExtensions).GetMethod(
            "CloneVoicePresenceModuleOptions",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var result = method!.Invoke(null, [options, resolvedName]);
        result.Should().NotBeNull();
        return result.Should().BeOfType<VoicePresenceModuleOptions>().Subject;
    }
}
