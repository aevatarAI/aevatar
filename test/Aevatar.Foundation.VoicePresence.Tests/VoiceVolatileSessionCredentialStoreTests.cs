using Aevatar.Foundation.VoicePresence.Hosting;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public sealed class VoiceVolatileSessionCredentialStoreTests
{
    [Fact]
    public void Set_then_TryGet_returns_token_when_not_expired()
    {
        var store = new VoiceVolatileSessionCredentialStore();
        store.Set("s1", "tok-1", DateTimeOffset.UtcNow.AddMinutes(5));

        store.TryGet("s1", out var token).ShouldBeTrue();
        token.ShouldBe("tok-1");
    }

    [Fact]
    public void TryGet_returns_false_and_evicts_when_expired()
    {
        var store = new VoiceVolatileSessionCredentialStore();
        store.Set("s1", "tok-1", DateTimeOffset.UtcNow.AddSeconds(-1));

        store.TryGet("s1", out _).ShouldBeFalse();
        store.TryGet("s1", out _).ShouldBeFalse(); // stays evicted
    }

    [Fact]
    public void Remove_evicts_token()
    {
        var store = new VoiceVolatileSessionCredentialStore();
        store.Set("s1", "tok-1", DateTimeOffset.UtcNow.AddMinutes(5));

        store.Remove("s1");

        store.TryGet("s1", out _).ShouldBeFalse();
    }

    [Fact]
    public void Set_ignores_empty_session_or_token()
    {
        var store = new VoiceVolatileSessionCredentialStore();
        store.Set("", "tok", DateTimeOffset.UtcNow.AddMinutes(5));
        store.Set("s1", "", DateTimeOffset.UtcNow.AddMinutes(5));

        store.TryGet("", out _).ShouldBeFalse();
        store.TryGet("s1", out _).ShouldBeFalse();
    }

    [Fact]
    public void Unknown_session_returns_false()
    {
        new VoiceVolatileSessionCredentialStore().TryGet("nope", out _).ShouldBeFalse();
    }
}
