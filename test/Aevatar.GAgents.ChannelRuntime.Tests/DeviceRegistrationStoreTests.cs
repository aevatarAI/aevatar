using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class DeviceRegistrationStoreTests : IDisposable
{
    private readonly DeviceRegistrationStore _store;

    public DeviceRegistrationStoreTests()
    {
        var logger = new NullLogger<DeviceRegistrationStore>();
        _store = new DeviceRegistrationStore(logger);
    }

    public void Dispose()
    {
        foreach (var reg in _store.List())
            _store.Delete(reg.Id);
    }

    [Fact]
    public void Register_creates_entry_with_generated_id()
    {
        var entry = _store.Register("scope-1", "hmac-secret", "conv-123", "My sensor hub");

        entry.Should().NotBeNull();
        entry.Id.Should().NotBeNullOrWhiteSpace();
        entry.ScopeId.Should().Be("scope-1");
        entry.HmacKey.Should().Be("hmac-secret");
        entry.NyxConversationId.Should().Be("conv-123");
        entry.Description.Should().Be("My sensor hub");

        _store.Delete(entry.Id);
    }

    [Fact]
    public void Register_requires_non_empty_scope_id()
    {
        var act = () => _store.Register("", null, null, null);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("scopeId");
    }

    [Fact]
    public void Register_requires_non_whitespace_scope_id()
    {
        var act = () => _store.Register("   ", null, null, null);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("scopeId");
    }

    [Fact]
    public void Get_returns_null_when_not_found()
    {
        _store.Get("nonexistent-id").Should().BeNull();
    }

    [Fact]
    public void Get_returns_entry_when_exists()
    {
        var entry = _store.Register("scope-2", "key-2", null, null);

        var found = _store.Get(entry.Id);

        found.Should().NotBeNull();
        found!.Id.Should().Be(entry.Id);
        found.ScopeId.Should().Be("scope-2");
        found.HmacKey.Should().Be("key-2");

        _store.Delete(entry.Id);
    }

    [Fact]
    public void List_returns_all_registrations()
    {
        var e1 = _store.Register("scope-a", null, null, "device-a");
        var e2 = _store.Register("scope-b", null, null, "device-b");
        var e3 = _store.Register("scope-c", null, null, "device-c");

        var list = _store.List();

        list.Should().Contain(r => r.Id == e1.Id);
        list.Should().Contain(r => r.Id == e2.Id);
        list.Should().Contain(r => r.Id == e3.Id);

        _store.Delete(e1.Id);
        _store.Delete(e2.Id);
        _store.Delete(e3.Id);
    }

    [Fact]
    public void Delete_removes_entry()
    {
        var entry = _store.Register("scope-del", "key", null, null);

        _store.Delete(entry.Id).Should().BeTrue();
        _store.Get(entry.Id).Should().BeNull();
    }

    [Fact]
    public void Delete_returns_false_when_not_found()
    {
        _store.Delete("nonexistent-id").Should().BeFalse();
    }

    [Fact]
    public void Register_with_null_optional_fields_stores_empty_strings()
    {
        var entry = _store.Register("scope-nulls", null, null, null);

        entry.HmacKey.Should().BeEmpty();
        entry.NyxConversationId.Should().BeEmpty();
        entry.Description.Should().BeEmpty();

        _store.Delete(entry.Id);
    }

    [Fact]
    public void Register_sets_created_at_timestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var entry = _store.Register("scope-ts", null, null, null);
        var after = DateTimeOffset.UtcNow;

        entry.CreatedAt.Should().NotBeNull();
        var createdAt = entry.CreatedAt.ToDateTimeOffset();
        createdAt.Should().BeOnOrAfter(before);
        createdAt.Should().BeOnOrBefore(after);

        _store.Delete(entry.Id);
    }
}
