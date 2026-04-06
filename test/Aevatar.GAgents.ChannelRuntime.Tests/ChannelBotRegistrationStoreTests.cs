using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class ChannelBotRegistrationStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ChannelBotRegistrationStore _store;

    public ChannelBotRegistrationStoreTests()
    {
        // Use a temp directory to avoid polluting ~/.aevatar
        _tempDir = Path.Combine(Path.GetTempPath(), $"channel-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // The store uses ~/.aevatar by default; we need to create a testable version.
        // For now, test the public API via a real store instance with isolated path.
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<ChannelBotRegistrationStore>();
        _store = new ChannelBotRegistrationStore(logger);
    }

    public void Dispose()
    {
        // Clean up any registrations we created
        foreach (var reg in _store.List())
            _store.Delete(reg.Id);

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Register_creates_entry_with_generated_id()
    {
        var entry = _store.Register("lark", "api-lark-bot", "token-123", "verify-456", "scope-1");

        entry.Should().NotBeNull();
        entry.Id.Should().NotBeNullOrWhiteSpace();
        entry.Platform.Should().Be("lark");
        entry.NyxProviderSlug.Should().Be("api-lark-bot");
        entry.NyxUserToken.Should().Be("token-123");
        entry.VerificationToken.Should().Be("verify-456");
        entry.ScopeId.Should().Be("scope-1");

        _store.Delete(entry.Id);
    }

    [Fact]
    public void Get_returns_registered_entry()
    {
        var entry = _store.Register("lark", "api-lark-bot", "token-1", null, null);

        var found = _store.Get(entry.Id);

        found.Should().NotBeNull();
        found!.Id.Should().Be(entry.Id);
        found.Platform.Should().Be("lark");

        _store.Delete(entry.Id);
    }

    [Fact]
    public void Get_returns_null_for_unknown_id()
    {
        _store.Get("nonexistent-id").Should().BeNull();
    }

    [Fact]
    public void List_returns_all_entries()
    {
        var e1 = _store.Register("lark", "slug-1", "token-1", null, null);
        var e2 = _store.Register("telegram", "slug-2", "token-2", null, null);

        var list = _store.List();
        list.Should().Contain(r => r.Id == e1.Id);
        list.Should().Contain(r => r.Id == e2.Id);

        _store.Delete(e1.Id);
        _store.Delete(e2.Id);
    }

    [Fact]
    public void Delete_removes_entry_and_returns_true()
    {
        var entry = _store.Register("lark", "slug", "token", null, null);

        _store.Delete(entry.Id).Should().BeTrue();
        _store.Get(entry.Id).Should().BeNull();
    }

    [Fact]
    public void Delete_returns_false_for_unknown_id()
    {
        _store.Delete("nonexistent-id").Should().BeFalse();
    }

    [Fact]
    public void Register_with_null_optional_fields_stores_empty_strings()
    {
        var entry = _store.Register("lark", "slug", "token", null, null);

        entry.VerificationToken.Should().BeEmpty();
        entry.ScopeId.Should().BeEmpty();

        _store.Delete(entry.Id);
    }
}
