using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Modules;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class VoicePresenceModuleFactoryTests
{
    [Fact]
    public void TryCreate_should_create_registered_module_by_alias()
    {
        var expected = CreateModule("voice_presence_openai");
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new VoicePresenceModuleFactory(services, [
            new VoicePresenceModuleRegistration(
                ["voice_presence", "voice_presence_openai"],
                _ => expected),
        ]);

        var created = factory.TryCreate("voice_presence_openai", out var module);

        created.ShouldBeTrue();
        module.ShouldBeSameAs(expected);
    }

    [Fact]
    public void TryCreate_should_return_false_for_unknown_module_name()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new VoicePresenceModuleFactory(services, [
            new VoicePresenceModuleRegistration(["voice_presence"], _ => CreateModule("voice_presence")),
        ]);

        var created = factory.TryCreate("voice_presence_minicpm", out var module);

        created.ShouldBeFalse();
        module.ShouldBeNull();
    }

    [Fact]
    public void TryCreate_should_pass_requested_alias_into_module_factory()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new VoicePresenceModuleFactory(services, [
            new VoicePresenceModuleRegistration(
                ["voice_presence", "voice_presence_openai"],
                (_, resolvedName) => CreateModule(resolvedName)),
        ]);

        var created = factory.TryCreate("voice_presence_openai", out var module);

        created.ShouldBeTrue();
        module.ShouldBeOfType<VoicePresenceModule>().Name.ShouldBe("voice_presence_openai");
    }

    [Fact]
    public void Ctor_should_reject_duplicate_module_names()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => new VoicePresenceModuleFactory(services, [
            new VoicePresenceModuleRegistration(["voice_presence"], _ => CreateModule("voice_presence")),
            new VoicePresenceModuleRegistration(["voice_presence"], _ => CreateModule("voice_presence")),
        ]));
    }

    private static VoicePresenceModule CreateModule(string name) =>
        new(
            provider: new NoopVoiceProvider(),
            providerConfig: new VoiceProviderConfig { ProviderName = "openai", ApiKey = "test-key" },
            options: new VoicePresenceModuleOptions { Name = name });

    private sealed class NoopVoiceProvider : IRealtimeVoiceProvider
    {
        public Func<VoiceProviderEvent, CancellationToken, Task>? OnEvent { private get; set; }

        public Task ConnectAsync(VoiceProviderConfig config, CancellationToken ct) => Task.CompletedTask;

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct) => Task.CompletedTask;

        public Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct) => Task.CompletedTask;

        public Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct) => Task.CompletedTask;

        public Task CancelResponseAsync(CancellationToken ct) => Task.CompletedTask;

        public Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
