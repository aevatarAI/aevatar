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
        observed!.Credentials.NyxIdAccessToken.Should().Be("sender-token");
        observed.Credentials.NyxIdOrgToken.Should().Be("sender-token");
        observed.Credentials.SenderNyxIdAccessToken.Should().Be("sender-token");
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
        context.Terminate.Should().BeFalse();
        AgentToolRequestContext.NyxIdAccessToken.Should().Be("owner-token");
    }

    [Fact]
    public async Task InvokeAsync_WhenNoSenderBinding_ShouldLeaveOwnerCredentialsUnchanged()
    {
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
        observed!.Credentials.NyxIdAccessToken.Should().Be("owner-token");
        observed.Credentials.NyxIdOrgToken.Should().Be("owner-org-token");
        context.Terminate.Should().BeFalse();
    }

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
