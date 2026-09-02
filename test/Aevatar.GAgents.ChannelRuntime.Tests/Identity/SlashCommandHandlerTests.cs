using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Slash;
using FluentAssertions;
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
    };

    [Fact]
    public async Task Init_ReturnsBindingCard_ForUnboundSenderInPrivateChat()
    {
        var broker = new InMemoryCapabilityBroker();
        var handler = new InitChannelSlashCommandHandler(broker, NullLogger<InitChannelSlashCommandHandler>.Instance);

        var reply = await handler.HandleAsync(Context(), default);

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
        var handler = new InitChannelSlashCommandHandler(broker, NullLogger<InitChannelSlashCommandHandler>.Instance);

        var reply = await handler.HandleAsync(Context(privateChat: false), default);

        reply.Should().NotBeNull();
        reply!.Actions.Should().BeEmpty();
        reply.Text.Should().Contain("私聊");
    }

    [Fact]
    public async Task Init_OffersServiceAuthorizationRenewal_ForBoundSender()
    {
        var broker = new InMemoryCapabilityBroker();
        broker.SeedBinding(Subject(), new BindingId { Value = "bnd_existing" });
        var handler = new InitChannelSlashCommandHandler(broker, NullLogger<InitChannelSlashCommandHandler>.Instance);

        var reply = await handler.HandleAsync(Context(bindingValue: "bnd_existing"), default);

        reply.Should().NotBeNull();
        reply!.Actions.Should().ContainSingle(action =>
            action.Kind == ActionElementKind.Link &&
            action.Label == "更新服务授权");
        reply.Text.Should().Contain("重新确认并更新");
        reply.Text.Should().NotContain("查看当前已授权服务");
        reply.Text.Should().NotContain("/unbind");
    }

    [Fact]
    public async Task Whoami_BoundSender_ReturnsMaskedBindingId()
    {
        var handler = new WhoamiChannelSlashCommandHandler();

        var ctx = new ChannelSlashCommandContext
        {
            CommandName = "whoami",
            ArgumentText = string.Empty,
            Subject = Subject(),
            BindingIdValue = "bnd_1234567890abcdef",
            RegistrationId = "reg-1",
            RegistrationScopeId = "scope-1",
            SenderId = "ou_user_y",
            SenderName = "Eric",
            IsPrivateChat = true,
        };

        var reply = await handler.HandleAsync(ctx, default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("已绑定");
        reply.Text.Should().Contain("Eric");
        reply.Text.Should().Contain("bnd_…cdef");
        reply.Text.Should().NotContain("1234567890abcdef");
    }

    [Fact]
    public async Task Whoami_DoesNotRequireBinding_AndReturnsUnboundHintForUnboundSender()
    {
        // Issue #513 phase 6: /whoami must reach its handler regardless of
        // binding state so the user can introspect "am I bound?" without the
        // turn runner short-circuiting them to the /init prompt instead.
        var handler = new WhoamiChannelSlashCommandHandler();
        handler.RequiresBinding.Should().BeFalse();

        var ctx = new ChannelSlashCommandContext
        {
            CommandName = "whoami",
            ArgumentText = string.Empty,
            Subject = Subject(),
            BindingIdValue = null,
            RegistrationId = "reg-1",
            RegistrationScopeId = "scope-1",
            SenderId = "ou_user_y",
            SenderName = "Eric",
            IsPrivateChat = true,
        };

        var reply = await handler.HandleAsync(ctx, default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("未绑定");
        reply.Text.Should().Contain("/init");
        reply.Text.Should().Contain("Eric");
        // Must not invent a binding-id placeholder — masked output is only
        // for actual bindings.
        reply.Text.Should().NotContain("Binding ID");
    }

    [Fact]
    public void InitHandler_DoesNotRequireBinding()
    {
        var broker = new InMemoryCapabilityBroker();
        var handler = new InitChannelSlashCommandHandler(broker, NullLogger<InitChannelSlashCommandHandler>.Instance);
        handler.RequiresBinding.Should().BeFalse();
    }
}
