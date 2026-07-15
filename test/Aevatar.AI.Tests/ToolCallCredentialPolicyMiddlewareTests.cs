using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Middleware;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ToolCallCredentialPolicyMiddlewareTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeAsync_WhenSenderTokenExists_ShouldRunToolUnderSenderCredentials(bool isReadOnly)
    {
        var middleware = new ToolCallCredentialPolicyMiddleware();
        var context = NewContext(new StubTool(isReadOnly: isReadOnly), "{}");
        using var _ = AgentToolContextScope.Push(SenderBoundContext(
            ownerToken: "owner-token",
            senderToken: " sender-token "));
        AgentToolExecutionContext? observed = null;

        await middleware.InvokeAsync(context, () =>
        {
            observed = AgentToolRequestContext.Current;
            return Task.CompletedTask;
        });

        observed.Should().NotBeNull();
        observed!.Credentials.CredentialRef.Should().Be("sender-token");
        observed.Credentials.OrganizationCredentialRef.Should().Be("sender-token");
        observed.Credentials.SenderCredentialRef.Should().Be("sender-token");
        context.CredentialSource.Should().Be(AgentToolCredentialSource.ChannelRegistration);
        context.Terminate.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false, "", null)]
    [InlineData(true, true, "", null)]
    [InlineData(true, false, "external_write", null)]
    [InlineData(true, false, "", true)]
    public async Task InvokeAsync_WhenSenderBoundMutationHasNoSenderToken_ShouldDenyAndTerminate(
        bool isReadOnly,
        bool isDestructive,
        string sideEffectKind,
        bool? requiresApproval)
    {
        var middleware = new ToolCallCredentialPolicyMiddleware();
        var context = NewContext(new StubTool(
            isReadOnly: isReadOnly,
            isDestructive: isDestructive,
            sideEffectKind: sideEffectKind,
            requiresApproval: requiresApproval), "{}");
        using var _ = AgentToolContextScope.Push(SenderBoundContext(
            ownerToken: "owner-token",
            senderToken: null));
        var nextCalled = false;

        await middleware.InvokeAsync(context, () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        nextCalled.Should().BeFalse();
        context.CredentialSource.Should().Be(AgentToolCredentialSource.ChannelRegistration);
        context.Terminate.Should().BeTrue();
        context.TerminationKind.Should().Be(ToolCallTerminationKind.MiddlewareTerminated);
        context.Result.Should().Contain("credential_denied");
        context.Result.Should().Contain("Owner credentials were not used");
        context.Result.Should().Contain("/init");
        using var result = JsonDocument.Parse(context.Result!);
        result.RootElement.GetProperty("error").GetString().Should().Be("credential_denied");
    }

    [Fact]
    public async Task InvokeAsync_WhenSenderBoundReadOnlyHasNoSenderToken_ShouldNotBlock()
    {
        var middleware = new ToolCallCredentialPolicyMiddleware();
        var context = NewContext(new StubTool(isReadOnly: true), "{}");
        using var _ = AgentToolContextScope.Push(SenderBoundContext(
            ownerToken: "owner-token",
            senderToken: null));
        var nextCalled = false;

        await middleware.InvokeAsync(context, () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        nextCalled.Should().BeTrue();
        context.CredentialSource.Should().Be(AgentToolCredentialSource.BearerToken);
        context.Terminate.Should().BeFalse();
        AgentToolRequestContext.CredentialRef.Should().Be("owner-token");
    }

    [Fact]
    public async Task InvokeAsync_WhenNoChannelAndNoSenderBinding_ShouldLeaveOwnerCredentialsUnchanged()
    {
        // No Channel context at all == a direct/API caller, not a channel-relayed third
        // party. There is no distinct "sender" to isolate from the owner here, so the
        // owner-credential fallback (including for mutations) is intentional and unchanged.
        var middleware = new ToolCallCredentialPolicyMiddleware();
        var context = NewContext(new StubTool(isReadOnly: false), "{}");
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials("owner-token", "owner-org-token", null),
        });
        AgentToolExecutionContext? observed = null;

        await middleware.InvokeAsync(context, () =>
        {
            observed = AgentToolRequestContext.Current;
            return Task.CompletedTask;
        });

        observed.Should().NotBeNull();
        observed!.Credentials.CredentialRef.Should().Be("owner-token");
        observed.Credentials.OrganizationCredentialRef.Should().Be("owner-org-token");
        context.CredentialSource.Should().Be(AgentToolCredentialSource.BearerToken);
        context.Terminate.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_WhenDirectCallerHasNoBearerToken_ShouldSetSystemCredentialSource()
    {
        var middleware = new ToolCallCredentialPolicyMiddleware();
        var context = NewContext(new StubTool(isReadOnly: false), "{}");
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty);
        var nextCalled = false;

        await middleware.InvokeAsync(context, () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        nextCalled.Should().BeTrue();
        context.CredentialSource.Should().Be(AgentToolCredentialSource.System);
        context.Terminate.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_WhenScheduleContextExists_ShouldSetScheduledRunCredentialSource()
    {
        var middleware = new ToolCallCredentialPolicyMiddleware();
        var context = NewContext(new StubTool(isReadOnly: false), "{}");
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials("owner-token", "owner-org-token", null),
            Schedule = new AgentToolScheduleContext(" schedule-1 "),
        });
        var nextCalled = false;

        await middleware.InvokeAsync(context, () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        nextCalled.Should().BeTrue();
        context.CredentialSource.Should().Be(AgentToolCredentialSource.ScheduledRun);
        context.Terminate.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_WhenExecutionContextHasExplicitCredentialSource_ShouldPreserveIt()
    {
        var middleware = new ToolCallCredentialPolicyMiddleware();
        var context = NewContext(new StubTool(isReadOnly: false), "{}");
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials("owner-token", "owner-org-token", null),
            CredentialSource = AgentToolCredentialSource.ServiceAccount,
        });
        var nextCalled = false;

        await middleware.InvokeAsync(context, () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        nextCalled.Should().BeTrue();
        context.CredentialSource.Should().Be(AgentToolCredentialSource.ServiceAccount);
        context.Terminate.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_WhenChannelSenderUnboundMutation_ShouldDenyAndTerminate()
    {
        // Regression test: an addressable channel sender (e.g. a Lark group member who
        // @-mentions the bot, or DMs it) who never ran /init must not get their mutating
        // tool calls executed under the bot owner's NyxID credentials.
        var middleware = new ToolCallCredentialPolicyMiddleware();
        var context = NewContext(new StubTool(isReadOnly: false), "{}");
        using var _ = AgentToolContextScope.Push(ChannelUnboundContext(ownerToken: "owner-token"));
        var nextCalled = false;

        await middleware.InvokeAsync(context, () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        nextCalled.Should().BeFalse();
        context.CredentialSource.Should().Be(AgentToolCredentialSource.ChannelRegistration);
        context.Terminate.Should().BeTrue();
        context.TerminationKind.Should().Be(ToolCallTerminationKind.MiddlewareTerminated);
        context.Result.Should().Contain("credential_denied");
        context.Result.Should().Contain("Owner credentials were not used");
        context.Result.Should().Contain("/init");
        using var result = JsonDocument.Parse(context.Result!);
        result.RootElement.GetProperty("error").GetString().Should().Be("credential_denied");
    }

    [Fact]
    public async Task InvokeAsync_WhenChannelSenderUnboundReadOnly_ShouldNotBlock()
    {
        // Read-only tool calls from an unbound channel sender still run under the owner's
        // credentials (the bot can still answer for anyone it's addressable to) — only
        // mutations require a bound sender identity.
        var middleware = new ToolCallCredentialPolicyMiddleware();
        var context = NewContext(new StubTool(isReadOnly: true), "{}");
        using var _ = AgentToolContextScope.Push(ChannelUnboundContext(ownerToken: "owner-token"));
        var nextCalled = false;

        await middleware.InvokeAsync(context, () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        nextCalled.Should().BeTrue();
        context.CredentialSource.Should().Be(AgentToolCredentialSource.BearerToken);
        context.Terminate.Should().BeFalse();
        AgentToolRequestContext.CredentialRef.Should().Be("owner-token");
    }

    private static AgentToolExecutionContext ChannelUnboundContext(string ownerToken) =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(ownerToken, ownerToken, null),
            Channel = new AgentToolChannelContext(
                Platform: "lark",
                SenderId: "ou_stranger",
                RegistrationScopeId: "scope-1",
                MessageId: "msg-1",
                PlatformMessageId: null),
        };

    private static ToolCallContext NewContext(IAgentTool tool, string argumentsJson) => new()
    {
        Tool = tool,
        ToolName = tool.Name,
        ToolCallId = "call-1",
        ArgumentsJson = argumentsJson,
    };

    private static AgentToolExecutionContext SenderBoundContext(string ownerToken, string? senderToken) =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(ownerToken, ownerToken, senderToken),
            SenderBinding = new AgentToolSenderBindingContext("binding-1"),
        };

    private sealed class StubTool(
        bool isReadOnly,
        bool isDestructive = false,
        string sideEffectKind = "",
        bool? requiresApproval = null) : IAgentTool
    {
        public string Name => "test_tool";
        public string Description => "test";
        public string ParametersSchema => "{}";
        public bool IsReadOnly => isReadOnly;
        public bool IsDestructive => isDestructive;
        public string SideEffectKind => sideEffectKind;
        public bool? RequiresApproval(string argumentsJson) => requiresApproval;
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("""{"ok":true}""");
    }
}
