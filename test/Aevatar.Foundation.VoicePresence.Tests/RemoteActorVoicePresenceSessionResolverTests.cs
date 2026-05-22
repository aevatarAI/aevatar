using Aevatar.Foundation.VoicePresence.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class RemoteActorVoicePresenceSessionResolverTests
{
    [Fact]
    public async Task ResolveAsync_should_return_null_because_disabled_remote_fallback_shell_was_deleted()
    {
        var resolver = new RemoteActorVoicePresenceSessionResolver(new ServiceCollection().BuildServiceProvider());

        var session = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1", "voice_presence"));

        session.ShouldBeNull();
    }

    [Fact]
    public void Remote_voice_resolver_source_should_not_reintroduce_fallback_bridge_state()
    {
        var resolverSource = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "Aevatar.Foundation.VoicePresence",
            "Hosting",
            "RemoteActorVoicePresenceSessionResolver.cs")));

        resolverSource.ShouldNotContain("RemoteActorVoicePresenceSessionBridge");
        resolverSource.ShouldNotContain("IActorDispatchPort");
        resolverSource.ShouldNotContain("BuildDirectEnvelope");
        resolverSource.ShouldNotContain("VoiceRemoteAudioInputReceived");
        resolverSource.ShouldContain("Task.FromResult<VoicePresenceSession?>(null)");
    }
}
