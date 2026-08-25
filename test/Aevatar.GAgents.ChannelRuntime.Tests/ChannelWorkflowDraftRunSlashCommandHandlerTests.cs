using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.NyxidChat.WorkflowDraftRun;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelWorkflowDraftRunSlashCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenListed_ShouldReturnRunCommandsOnlyForRunnableScopeWorkflows()
    {
        var queryPort = new StubScopeWorkflowQueryPort(
            "scope-alpha",
            [
                Summary("wf-alpha", "Resource status watcher [delivery-alpha]"),
                Summary("wf-beta", "Document archive sync [delivery-beta]"),
            ],
            new Dictionary<string, ScopeWorkflowLookupStatus>
            {
                ["wf-alpha"] = ScopeWorkflowLookupStatus.Runnable,
                ["wf-beta"] = ScopeWorkflowLookupStatus.NotReady,
            });
        var handler = new ChannelWorkflowDraftRunSlashCommandHandler(
            queryPort,
            AuthorizedScopeResolver("scope-alpha"));

        var reply = await handler.HandleAsync(Context("list"), CancellationToken.None);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("Resource status watcher [delivery-alpha]");
        reply.Text.Should().Contain("/workflow run wf-alpha");
        reply.Text.Should().NotContain("Document archive sync [delivery-beta]");
        reply.Text.Should().NotContain("/workflow run wf-beta");
        queryPort.LookupRequests.Should().Equal(
            ("scope-alpha", "wf-alpha"),
            ("scope-alpha", "wf-beta"));
    }

    [Fact]
    public async Task HandleAsync_WhenMoreThanTwentyWorkflowsAreRunnable_ShouldPreserveOrderAndCapTheReply()
    {
        var workflows = Enumerable.Range(1, 21)
            .Select(index => Summary($"wf-{index:D2}", $"Workflow {index:D2}"))
            .ToArray();
        var handler = new ChannelWorkflowDraftRunSlashCommandHandler(
            new StubScopeWorkflowQueryPort("scope-alpha", workflows),
            AuthorizedScopeResolver("scope-alpha"));

        var reply = await handler.HandleAsync(Context("list"), CancellationToken.None);

        reply!.Text.Should().Contain("/workflow run wf-01");
        reply.Text.Should().Contain("/workflow run wf-20");
        reply.Text.Should().NotContain("/workflow run wf-21");
        reply.Text.Should().Contain("仅显示前 20 个");
        reply.Text.IndexOf("/workflow run wf-01", StringComparison.Ordinal)
            .Should().BeLessThan(reply.Text.IndexOf("/workflow run wf-20", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_WhenScopeHasNoWorkflows_ShouldSaySoInsteadOfClaimingAnOutage()
    {
        var handler = new ChannelWorkflowDraftRunSlashCommandHandler(
            new StubScopeWorkflowQueryPort("scope-alpha", []),
            AuthorizedScopeResolver("scope-alpha"));

        var reply = await handler.HandleAsync(Context("list"), CancellationToken.None);

        reply!.Text.Should().Contain("没有已发布的 workflow");
    }

    [Fact]
    public async Task HandleAsync_WhenListedWithoutAQueryPort_ShouldReportTheServiceIsUnavailable()
    {
        var handler = new ChannelWorkflowDraftRunSlashCommandHandler(
            authorizedScopeResolver: AuthorizedScopeResolver("scope-alpha"));

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
            new StubScopeWorkflowQueryPort("scope-alpha", []),
            AuthorizedScopeResolver("scope-alpha"));

        var reply = await handler.HandleAsync(Context(argumentText), CancellationToken.None);

        reply!.Text.Should().Contain("/workflow list");
        reply.Text.Should().Contain("/workflow run <workflow-id>");
        reply.Text.Should().NotContain("请稍后重试");
    }

    [Fact]
    public async Task HandleAsync_WhenRegistrationScopeIsMissing_ShouldRefuseToGuessAScope()
    {
        var handler = new ChannelWorkflowDraftRunSlashCommandHandler(
            new StubScopeWorkflowQueryPort("scope-alpha", []),
            AuthorizedScopeResolver("scope-alpha"));

        var reply = await handler.HandleAsync(Context("list", registrationScopeId: "  "), CancellationToken.None);

        reply!.Text.Should().Contain("无法确定当前 NyxID scope");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("owner-other")]
    public async Task HandleAsync_WhenSenderDoesNotOwnTheRegistrationScope_ShouldDenyWithoutWorkflowQueries(
        string? ownerScopeId)
    {
        var queryPort = new StubScopeWorkflowQueryPort(
            "scope-alpha",
            [Summary("wf-alpha", "Private workflow")]);
        var handler = new ChannelWorkflowDraftRunSlashCommandHandler(
            queryPort,
            AuthorizedScopeResolver(ownerScopeId));

        var reply = await handler.HandleAsync(Context("list"), CancellationToken.None);

        reply!.Text.Should().Contain("无权查看");
        reply.Text.Should().NotContain("Private workflow");
        queryPort.ListRequests.Should().BeEmpty();
        queryPort.LookupRequests.Should().BeEmpty();
    }

    [Fact]
    public void Usage_ShouldAdvertiseBothListAndRun()
    {
        new ChannelWorkflowDraftRunSlashCommandHandler().Usage.ArgumentSyntax
            .Should().Be("list | run <workflow-id>");
    }

    [Fact]
    public void RequiresBinding_ShouldProtectWorkflowDiscovery()
    {
        new ChannelWorkflowDraftRunSlashCommandHandler().RequiresBinding.Should().BeTrue();
    }

    private static IChannelWorkflowAuthorizedScopeResolver AuthorizedScopeResolver(string? ownerScopeId) =>
        new ChannelWorkflowAuthorizedScopeResolver(new StubOwnerScopeResolver(ownerScopeId));

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
            DateTimeOffset.Parse("2026-08-16T06:00:00Z"))
        {
            PublishedServiceId = $"svc-{workflowId}",
        };

    private sealed class StubScopeWorkflowQueryPort(
        string expectedScopeId,
        IReadOnlyList<ScopeWorkflowSummary> workflows,
        IReadOnlyDictionary<string, ScopeWorkflowLookupStatus>? lookupStatuses = null) : IScopeWorkflowQueryPort
    {
        public List<string> ListRequests { get; } = [];
        public List<(string ScopeId, string WorkflowId)> LookupRequests { get; } = [];

        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(
            string scopeId,
            CancellationToken ct = default)
        {
            scopeId.Should().Be(expectedScopeId);
            ListRequests.Add(scopeId);
            return Task.FromResult(workflows);
        }

        public Task<ScopeWorkflowLookupResult> LookupByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default)
        {
            scopeId.Should().Be(expectedScopeId);
            LookupRequests.Add((scopeId, workflowId));
            var workflow = workflows.SingleOrDefault(candidate =>
                string.Equals(candidate.WorkflowId, workflowId, StringComparison.Ordinal));
            if (workflow is null)
            {
                return Task.FromResult(new ScopeWorkflowLookupResult(
                    ScopeWorkflowLookupStatus.NotFound,
                    null,
                    "test_not_found"));
            }

            var status = lookupStatuses is not null && lookupStatuses.TryGetValue(workflowId, out var configuredStatus)
                ? configuredStatus
                : ScopeWorkflowLookupStatus.Runnable;
            return Task.FromResult(new ScopeWorkflowLookupResult(
                status,
                status == ScopeWorkflowLookupStatus.Runnable ? workflow : null,
                $"test_{status.ToString().ToLowerInvariant()}"));
        }

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

    private sealed class StubOwnerScopeResolver(string? ownerScopeId) : IOwnerScopeResolver
    {
        public Task<OwnerScopeId?> ResolveAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            Task.FromResult(string.IsNullOrWhiteSpace(ownerScopeId)
                ? null
                : new OwnerScopeId { Value = ownerScopeId });
    }
}
