using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Authentication.Abstractions;
using FluentAssertions;

namespace Aevatar.AI.Tests;

// 06-20-observatory-admin-cross-scope (G6): email -> candidate scope ids, fail-closed parsing.
public sealed class NyxIdPlatformUserDirectoryTests
{
    [Fact]
    public void ParseMatches_MapsUsersToScopeCandidates()
    {
        const string raw = """
        {"users":[
            {"id":"u1","email":"a@x.io","role":"admin"},
            {"id":"u2","email":"b@x.io","role":"user"}
        ],"total":2,"page":1,"per_page":50}
        """;

        var matches = NyxIdPlatformUserDirectory.ParseMatches(raw);

        matches.Should().HaveCount(2);
        matches[0].Should().BeEquivalentTo(new PlatformUserMatch("u1", "a@x.io", "admin"));
        matches[1].ScopeId.Should().Be("u2");
    }

    [Fact]
    public void ParseMatches_SkipsEntriesWithoutId()
    {
        const string raw = """{"users":[{"email":"a@x.io","role":"admin"},{"id":"u2","email":"b@x.io"}]}""";

        var matches = NyxIdPlatformUserDirectory.ParseMatches(raw);

        matches.Should().ContainSingle().Which.ScopeId.Should().Be("u2");
    }

    [Theory]
    [InlineData("""{"error":true,"status":403,"body":"forbidden"}""")] // error envelope
    [InlineData("""{"total":0}""")] // no users array
    [InlineData("""{"users":"nope"}""")] // users not an array
    [InlineData("not-json")]
    [InlineData("""["u1"]""")] // non-object root
    [InlineData("")]
    [InlineData("   ")]
    public void ParseMatches_FailsClosedToEmpty(string raw)
    {
        NyxIdPlatformUserDirectory.ParseMatches(raw).Should().BeEmpty();
    }
}
