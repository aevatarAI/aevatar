using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelMessageComposerRegistryTests
{
    [Fact]
    public void Get_returns_composer_for_registered_channel()
    {
        var composer = CreateComposer("lark");
        var registry = new ChannelMessageComposerRegistry(
            new[] { composer },
            Array.Empty<IChannelNativeMessageProducer>());

        registry.Get(ChannelId.From("lark")).Should().BeSameAs(composer);
        registry.Get(ChannelId.From("LARK")).Should().BeSameAs(composer);
    }

    [Fact]
    public void Get_returns_null_for_unregistered_channel()
    {
        var registry = new ChannelMessageComposerRegistry(
            Array.Empty<IMessageComposer>(),
            Array.Empty<IChannelNativeMessageProducer>());

        registry.Get(ChannelId.From("telegram")).Should().BeNull();
    }

    [Fact]
    public void GetNativeProducer_returns_producer_for_registered_channel()
    {
        var producer = CreateNativeProducer("lark");
        var registry = new ChannelMessageComposerRegistry(
            Array.Empty<IMessageComposer>(),
            new[] { producer });

        registry.GetNativeProducer(ChannelId.From("lark")).Should().BeSameAs(producer);
    }

    [Fact]
    public void GetNativeProducer_returns_null_for_unregistered_channel()
    {
        var registry = new ChannelMessageComposerRegistry(
            Array.Empty<IMessageComposer>(),
            Array.Empty<IChannelNativeMessageProducer>());

        registry.GetNativeProducer(ChannelId.From("slack")).Should().BeNull();
    }

    [Fact]
    public void Registry_is_case_insensitive_on_channel_lookup()
    {
        var producer = CreateNativeProducer("lark");
        var registry = new ChannelMessageComposerRegistry(
            Array.Empty<IMessageComposer>(),
            new[] { producer });

        registry.GetNativeProducer(ChannelId.From("Lark")).Should().BeSameAs(producer);
    }

    private static IMessageComposer CreateComposer(string channel)
    {
        var composer = Substitute.For<IMessageComposer>();
        composer.Channel.Returns(ChannelId.From(channel));
        return composer;
    }

    private static IChannelNativeMessageProducer CreateNativeProducer(string channel)
    {
        var producer = Substitute.For<IChannelNativeMessageProducer>();
        producer.Channel.Returns(ChannelId.From(channel));
        return producer;
    }
}
