using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.Identity.Slash;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Pins the user-visible behaviour of the per-binding slash command handlers
/// added in issue #513 (Phase 1 /init Lark card, Phase 4 /whoami, Phase 6
/// RequiresBinding metadata). The handlers run inside the channel turn runner
/// and produce <see cref="MessageContent"/>; tests assert on that content
/// directly rather than reaching through the runner.
/// </summary>
public sealed class SlashCommandHandlerTests
{
    private static ExternalSubjectRef Subject() => new()
    {
        Platform = "lark",
        Tenant = "ou_tenant_x",
        ExternalUserId = "ou_user_y",
    };

    private static ChannelSlashCommandContext Context(
        InMemoryCapabilityBroker broker,
        string? bindingValue = null,
        bool privateChat = true) => new()
    {
        CommandName = "init",
        ArgumentText = string.Empty,
        Subject = Subject(),
        BindingIdValue = bindingValue,
        RegistrationId = "reg-1",
        RegistrationScopeId = "scope-1",
        SenderId = "ou_user_y",
        SenderName = "Eric",
        IsPrivateChat = privateChat,
        Services = new ServiceCollection().AddSingleton(broker).BuildServiceProvider(),
    };

    [Fact]
    public async Task Init_ReturnsBindingCard_ForUnboundSenderInPrivateChat()
    {
        var broker = new InMemoryCapabilityBroker();
        var handler = new InitChannelSlashCommandHandler(broker, broker, NullLogger<InitChannelSlashCommandHandler>.Instance);

        var reply = await handler.HandleAsync(Context(broker), default);

        reply.Should().NotBeNull();
        reply!.Cards.Should().HaveCount(1);
        reply.Actions.Should().Contain(action =>
            action.Kind == ActionElementKind.Link &&
            action.IsPrimary &&
            action.Value.Contains("test-nyxid.local/oauth/authorize"));
        // text fallback retains the URL so non-card transports keep working.
        reply.Text.Should().Contain("test-nyxid.local/oauth/authorize");
    }

    [Fact]
    public async Task Init_RefusesGroupChat()
    {
        var broker = new InMemoryCapabilityBroker();
        var handler = new InitChannelSlashCommandHandler(broker, broker, NullLogger<InitChannelSlashCommandHandler>.Instance);

        var reply = await handler.HandleAsync(Context(broker, privateChat: false), default);

        reply.Should().NotBeNull();
        reply!.Actions.Should().BeEmpty();
        reply.Text.Should().Contain("私聊");
    }

    [Fact]
    public async Task Init_TellsAlreadyBoundSender_ToUnbindFirst()
    {
        var broker = new InMemoryCapabilityBroker();
        var handler = new InitChannelSlashCommandHandler(broker, broker, NullLogger<InitChannelSlashCommandHandler>.Instance);

        var reply = await handler.HandleAsync(Context(broker, bindingValue: "bnd_existing"), default);

        reply.Should().NotBeNull();
        reply!.Actions.Should().BeEmpty();
        reply.Text.Should().Contain("/unbind");
    }

    [Fact]
    public async Task Whoami_RequiresBinding_AndReturnsMaskedBindingId()
    {
        var handler = new WhoamiChannelSlashCommandHandler();
        handler.RequiresBinding.Should().BeTrue();

        var broker = new InMemoryCapabilityBroker();
        var ctx = Context(broker, bindingValue: "bnd_1234567890abcdef");
        ctx = new ChannelSlashCommandContext
        {
            CommandName = "whoami",
            ArgumentText = ctx.ArgumentText,
            Subject = ctx.Subject,
            BindingIdValue = ctx.BindingIdValue,
            RegistrationId = ctx.RegistrationId,
            RegistrationScopeId = ctx.RegistrationScopeId,
            SenderId = ctx.SenderId,
            SenderName = ctx.SenderName,
            IsPrivateChat = ctx.IsPrivateChat,
            Services = ctx.Services,
        };

        var reply = await handler.HandleAsync(ctx, default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("Eric");
        reply.Text.Should().Contain("bnd_…cdef");
        reply.Text.Should().NotContain("1234567890abcdef");
    }

    [Fact]
    public void InitHandler_DoesNotRequireBinding()
    {
        var broker = new InMemoryCapabilityBroker();
        var handler = new InitChannelSlashCommandHandler(broker, broker, NullLogger<InitChannelSlashCommandHandler>.Instance);
        handler.RequiresBinding.Should().BeFalse();
    }
}
