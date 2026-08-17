using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.NyxidChat.WorkflowDraftRun;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelWorkflowDraftRunSlashCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenListed_ShouldReturnTheRunCommandForEveryScopeWorkflow()
    {
        var handler = new ChannelWorkflowDraftRunSlashCommandHandler(
            new StubScopeWorkflowQueryPort(
                "scope-alpha",
                [
                    Summary("wf-alpha", "HR onboarding email approval [delivery-alpha]"),
                    Summary("wf-beta", "Budget variance monitor [delivery-beta]"),
                ]));

        var reply = await handler.HandleAsync(Context("list"), CancellationToken.None);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("HR onboarding email approval [delivery-alpha]");
        reply.Text.Should().Contain("/workflow run wf-alpha");
        reply.Text.Should().Contain("/workflow run wf-beta");
    }

    [Fact]
    public async Task HandleAsync_WhenScopeHasNoWorkflows_ShouldSaySoInsteadOfClaimingAnOutage()
    {
        var handler = new ChannelWorkflowDraftRunSlashCommandHandler(
            new StubScopeWorkflowQueryPort("scope-alpha", []));

        var reply = await handler.HandleAsync(Context("list"), CancellationToken.None);

        reply!.Text.Should().Contain("没有已发布的 workflow");
    }

    [Fact]
    public async Task HandleAsync_WhenListedWithoutAQueryPort_ShouldReportTheServiceIsUnavailable()
    {
        var handler = new ChannelWorkflowDraftRunSlashCommandHandler();

        var reply = await handler.HandleAsync(Context("list"), CancellationToken.None);

        reply!.Text.Should().Contain("Workflow 查询服务暂不可用");
    }

    [Theory]
    [InlineData("")]
    [InlineData("run")]
    [InlineData("nonsense")]
    public async Task HandleAsync_WhenTheFormIsNotARunRequest_ShouldReturnUsageRatherThanATransientOutage(
        string argumentText)
    {
        var handler = new ChannelWorkflowDraftRunSlashCommandHandler(
            new StubScopeWorkflowQueryPort("scope-alpha", []));

        var reply = await handler.HandleAsync(Context(argumentText), CancellationToken.None);

        reply!.Text.Should().Contain("/workflow list");
        reply.Text.Should().Contain("/workflow run <workflow-id>");
        reply.Text.Should().NotContain("请稍后重试");
    }

    [Fact]
    public async Task HandleAsync_WhenRegistrationScopeIsMissing_ShouldRefuseToGuessAScope()
    {
        var handler = new ChannelWorkflowDraftRunSlashCommandHandler(
            new StubScopeWorkflowQueryPort("scope-alpha", []));

        var reply = await handler.HandleAsync(Context("list", registrationScopeId: "  "), CancellationToken.None);

        reply!.Text.Should().Contain("无法确定当前 NyxID scope");
    }

    [Fact]
    public void Usage_ShouldAdvertiseBothListAndRun()
    {
        new ChannelWorkflowDraftRunSlashCommandHandler().Usage.ArgumentSyntax
            .Should().Be("list | run <workflow-id>");
    }

    private static ChannelSlashCommandContext Context(
        string argumentText,
        string registrationScopeId = "scope-alpha") =>
        new()
        {
            CommandName = "workflow",
            ArgumentText = argumentText,
            Subject = new ExternalSubjectRef
            {
                Platform = "lark",
                Tenant = string.Empty,
                ExternalUserId = "sender-alpha",
            },
            RegistrationId = "registration-alpha",
            RegistrationScopeId = registrationScopeId,
            SenderId = "ou_sender_alpha",
            SenderName = "Sender Alpha",
            IsPrivateChat = true,
        };

    private static ScopeWorkflowSummary Summary(string workflowId, string displayName) =>
        new(
            "scope-alpha",
            workflowId,
            displayName,
            $"service-{workflowId}",
            $"name-{workflowId}",
            $"actor-{workflowId}",
            $"revision-{workflowId}",
            $"deployment-{workflowId}",
            "active",
            DateTimeOffset.Parse("2026-08-16T06:00:00Z"));

    private sealed class StubScopeWorkflowQueryPort(
        string expectedScopeId,
        IReadOnlyList<ScopeWorkflowSummary> workflows) : IScopeWorkflowQueryPort
    {
        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(
            string scopeId,
            CancellationToken ct = default)
        {
            scopeId.Should().Be(expectedScopeId);
            return Task.FromResult(workflows);
        }

        public Task<ScopeWorkflowLookupResult> LookupByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(
            string scopeId,
            string actorId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
