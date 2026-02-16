using Aevatar.Context.Abstractions;
using FluentAssertions;
using Xunit;

namespace Aevatar.Context.Core.Tests;

public sealed class AevatarUriTests
{
    // ─── Parse ───

    [Fact]
    public void Parse_SkillsRoot_ReturnsCorrectScopeAndPath()
    {
        var uri = AevatarUri.Parse("aevatar://skills/");
        uri.Scope.Should().Be("skills");
        uri.Path.Should().BeEmpty();
        uri.IsDirectory.Should().BeTrue();
        uri.Name.Should().Be("skills");
    }

    [Fact]
    public void Parse_SkillFile_ReturnsCorrectComponents()
    {
        var uri = AevatarUri.Parse("aevatar://skills/web-search/SKILL.md");
        uri.Scope.Should().Be("skills");
        uri.Path.Should().Be("web-search/SKILL.md");
        uri.IsDirectory.Should().BeFalse();
        uri.Name.Should().Be("SKILL.md");
    }

    [Fact]
    public void Parse_ResourceDirectory_DetectsDirectory()
    {
        var uri = AevatarUri.Parse("aevatar://resources/my-project/docs/");
        uri.Scope.Should().Be("resources");
        uri.Path.Should().Be("my-project/docs");
        uri.IsDirectory.Should().BeTrue();
        uri.Name.Should().Be("docs");
    }

    [Fact]
    public void Parse_UserMemory_ParsesCompletePath()
    {
        var uri = AevatarUri.Parse("aevatar://user/u123/memories/preferences/");
        uri.Scope.Should().Be("user");
        uri.Path.Should().Be("u123/memories/preferences");
        uri.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void Parse_AgentMemory_ParsesCompletePath()
    {
        var uri = AevatarUri.Parse("aevatar://agent/a456/memories/cases/");
        uri.Scope.Should().Be("agent");
        uri.Path.Should().Be("a456/memories/cases");
        uri.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void Parse_SessionFile_ParsesCorrectly()
    {
        var uri = AevatarUri.Parse("aevatar://session/run789/messages.jsonl");
        uri.Scope.Should().Be("session");
        uri.Path.Should().Be("run789/messages.jsonl");
        uri.IsDirectory.Should().BeFalse();
        uri.Name.Should().Be("messages.jsonl");
    }

    [Fact]
    public void Parse_ScopeOnly_TreatsAsDirectory()
    {
        var uri = AevatarUri.Parse("aevatar://resources");
        uri.Scope.Should().Be("resources");
        uri.Path.Should().BeEmpty();
        uri.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void Parse_CaseInsensitiveScheme()
    {
        var uri = AevatarUri.Parse("AEVATAR://Skills/test");
        uri.Scope.Should().Be("skills");
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://skills/test")]
    [InlineData("aevatar://")]
    public void TryParse_InvalidUri_ReturnsFalse(string input)
    {
        AevatarUri.TryParse(input, out _).Should().BeFalse();
    }

    [Fact]
    public void Parse_InvalidUri_ThrowsFormatException()
    {
        var act = () => AevatarUri.Parse("invalid://test");
        act.Should().Throw<FormatException>();
    }

    // ─── Create ───

    [Fact]
    public void Create_BuildsUri()
    {
        var uri = AevatarUri.Create("skills", "web-search/SKILL.md", false);
        uri.Scope.Should().Be("skills");
        uri.Path.Should().Be("web-search/SKILL.md");
        uri.IsDirectory.Should().BeFalse();
    }

    // ─── Factory helpers ───

    [Fact]
    public void SkillsRoot_ReturnsExpected()
    {
        var uri = AevatarUri.SkillsRoot();
        uri.ToString().Should().Be("aevatar://skills/");
    }

    [Fact]
    public void UserRoot_ReturnsExpected()
    {
        var uri = AevatarUri.UserRoot("u123");
        uri.ToString().Should().Be("aevatar://user/u123/");
    }

    // ─── Parent ───

    [Fact]
    public void Parent_OfFile_ReturnsParentDirectory()
    {
        var uri = AevatarUri.Parse("aevatar://skills/web-search/SKILL.md");
        var parent = uri.Parent;
        parent.Scope.Should().Be("skills");
        parent.Path.Should().Be("web-search");
        parent.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void Parent_OfNestedDirectory_ReturnsParent()
    {
        var uri = AevatarUri.Parse("aevatar://resources/my-project/docs/");
        var parent = uri.Parent;
        parent.Path.Should().Be("my-project");
    }

    [Fact]
    public void Parent_OfScopeRoot_ReturnsSelf()
    {
        var uri = AevatarUri.Parse("aevatar://skills/");
        var parent = uri.Parent;
        parent.Should().Be(uri);
    }

    // ─── Join ───

    [Fact]
    public void Join_AppendsSegment()
    {
        var root = AevatarUri.Parse("aevatar://skills/");
        var child = root.Join("web-search/");
        child.ToString().Should().Be("aevatar://skills/web-search/");
        child.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void Join_AppendsFile()
    {
        var dir = AevatarUri.Parse("aevatar://skills/web-search/");
        var file = dir.Join("SKILL.md");
        file.ToString().Should().Be("aevatar://skills/web-search/SKILL.md");
        file.IsDirectory.Should().BeFalse();
    }

    [Fact]
    public void Join_EmptySegment_ReturnsSelf()
    {
        var uri = AevatarUri.Parse("aevatar://skills/");
        uri.Join("").Should().Be(uri);
    }

    // ─── IsAncestorOf ───

    [Fact]
    public void IsAncestorOf_DirectChild_ReturnsTrue()
    {
        var parent = AevatarUri.Parse("aevatar://skills/");
        var child = AevatarUri.Parse("aevatar://skills/web-search/SKILL.md");
        parent.IsAncestorOf(child).Should().BeTrue();
    }

    [Fact]
    public void IsAncestorOf_DifferentScope_ReturnsFalse()
    {
        var a = AevatarUri.Parse("aevatar://skills/test");
        var b = AevatarUri.Parse("aevatar://resources/test");
        a.IsAncestorOf(b).Should().BeFalse();
    }

    [Fact]
    public void IsAncestorOf_Self_ReturnsFalse()
    {
        var uri = AevatarUri.Parse("aevatar://skills/web-search/");
        uri.IsAncestorOf(uri).Should().BeFalse();
    }

    // ─── ToString roundtrip ───

    [Theory]
    [InlineData("aevatar://skills/")]
    [InlineData("aevatar://skills/web-search/SKILL.md")]
    [InlineData("aevatar://resources/my-project/docs/")]
    [InlineData("aevatar://session/run789/messages.jsonl")]
    public void ToString_RoundTrip_PreservesUri(string original)
    {
        var uri = AevatarUri.Parse(original);
        uri.ToString().Should().Be(original);
    }

    // ─── Equality ───

    [Fact]
    public void Equality_SameUri_AreEqual()
    {
        var a = AevatarUri.Parse("aevatar://skills/web-search/SKILL.md");
        var b = AevatarUri.Parse("aevatar://skills/web-search/SKILL.md");
        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Equality_DifferentUri_AreNotEqual()
    {
        var a = AevatarUri.Parse("aevatar://skills/a");
        var b = AevatarUri.Parse("aevatar://skills/b");
        a.Should().NotBe(b);
    }
}
