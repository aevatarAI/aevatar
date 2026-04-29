using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Pins startup-time uniqueness validation for slash command handlers
/// (PR #521 review v4-pro). Two handlers cannot register the same name
/// or alias — duplicate registration must throw fail-fast at the time the
/// registry is constructed, not silently first-wins on first dispatch.
/// </summary>
public sealed class ChannelSlashCommandRegistryTests
{
    [Fact]
    public void FindsByCanonicalName()
    {
        var registry = new ChannelSlashCommandRegistry(new IChannelSlashCommandHandler[]
        {
            new StubHandler("init"),
            new StubHandler("whoami"),
        });

        registry.Find("init").Should().NotBeNull();
        registry.Find("INIT").Should().NotBeNull();
        registry.Find("whoami").Should().NotBeNull();
        registry.Find("model").Should().BeNull();
    }

    [Fact]
    public void FindsByAlias()
    {
        var registry = new ChannelSlashCommandRegistry(new IChannelSlashCommandHandler[]
        {
            new StubHandler("model", aliases: new[] { "m", "models" }),
        });

        registry.Find("m").Should().NotBeNull();
        registry.Find("MODELS").Should().NotBeNull();
    }

    [Fact]
    public void Throws_OnDuplicateName()
    {
        var act = () => new ChannelSlashCommandRegistry(new IChannelSlashCommandHandler[]
        {
            new StubHandler("model"),
            new StubHandler("model"),
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate slash command*");
    }

    [Fact]
    public void Throws_WhenAliasShadowsAnotherName()
    {
        var act = () => new ChannelSlashCommandRegistry(new IChannelSlashCommandHandler[]
        {
            new StubHandler("init"),
            new StubHandler("custom", aliases: new[] { "init" }),
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate slash command*");
    }

    [Fact]
    public void Throws_OnBlankName()
    {
        var act = () => new ChannelSlashCommandRegistry(new IChannelSlashCommandHandler[]
        {
            new StubHandler(string.Empty),
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*must not be blank*");
    }

    private sealed class StubHandler : IChannelSlashCommandHandler
    {
        private readonly string _name;
        private readonly IReadOnlyList<string> _aliases;

        public StubHandler(string name, IReadOnlyList<string>? aliases = null)
        {
            _name = name;
            _aliases = aliases ?? Array.Empty<string>();
        }

        public string Name => _name;
        public IReadOnlyList<string> Aliases => _aliases;
        public bool RequiresBinding => false;

        public Task<MessageContent?> HandleAsync(ChannelSlashCommandContext context, CancellationToken ct) =>
            Task.FromResult<MessageContent?>(null);
    }
}
