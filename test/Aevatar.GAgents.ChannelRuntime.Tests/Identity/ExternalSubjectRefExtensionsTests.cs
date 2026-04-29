using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Pins behaviour of <see cref="ExternalSubjectRefExtensions"/>: actor-id
/// formatting, required-field validation, and the colon-separator invariant
/// (the actor-id format relies on ':' as a separator, so field values must
/// not contain it).
/// </summary>
public class ExternalSubjectRefExtensionsTests
{
    [Fact]
    public void ToActorId_ProducesCanonicalColonJoinedFormat()
    {
        var subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "ou_tenant_x",
            ExternalUserId = "ou_user_y",
        };

        subject.ToActorId().Should().Be("external-identity-binding:lark:ou_tenant_x:ou_user_y");
    }

    [Fact]
    public void ToActorId_AllowsEmptyTenant()
    {
        // Telegram-style platforms have no tenant scope; the field is empty
        // but valid. The colon stays in place so the actor-id structure is
        // uniform across platforms.
        var subject = new ExternalSubjectRef
        {
            Platform = "telegram",
            Tenant = string.Empty,
            ExternalUserId = "12345",
        };

        subject.ToActorId().Should().Be("external-identity-binding:telegram::12345");
    }

    [Fact]
    public void EnsureValid_AcceptsValidSubject()
    {
        var subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "ou_tenant_x",
            ExternalUserId = "ou_user_y",
        };

        var act = () => ExternalSubjectRefExtensions.EnsureValid(subject);
        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureValid_RejectsEmptyPlatform()
    {
        var subject = new ExternalSubjectRef
        {
            Platform = string.Empty,
            Tenant = "ou_tenant_x",
            ExternalUserId = "ou_user_y",
        };

        var act = () => ExternalSubjectRefExtensions.EnsureValid(subject);
        act.Should().Throw<ArgumentException>().WithMessage("*platform is required*");
    }

    [Fact]
    public void EnsureValid_RejectsEmptyExternalUserId()
    {
        var subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "ou_tenant_x",
            ExternalUserId = string.Empty,
        };

        var act = () => ExternalSubjectRefExtensions.EnsureValid(subject);
        act.Should().Throw<ArgumentException>().WithMessage("*external_user_id is required*");
    }

    [Theory]
    [InlineData("lark:nested", "tenant", "user")]
    [InlineData("lark", "tenant:nested", "user")]
    [InlineData("lark", "tenant", "user:nested")]
    public void EnsureValid_RejectsColonInAnyField(string platform, string tenant, string user)
    {
        var subject = new ExternalSubjectRef
        {
            Platform = platform,
            Tenant = tenant,
            ExternalUserId = user,
        };

        var act = () => ExternalSubjectRefExtensions.EnsureValid(subject);
        act.Should().Throw<ArgumentException>().WithMessage("*must not contain ':'*");
    }

    [Fact]
    public void EnsureValid_ThrowsOnNull()
    {
        var act = () => ExternalSubjectRefExtensions.EnsureValid(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
