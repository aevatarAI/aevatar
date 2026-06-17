using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogQueryPortTests
{
    [Fact]
    public async Task QueryVisibleByCallerAsync_ReturnsOwnedAndSharedRows_WithDedupe()
    {
        var alice = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var bob = OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob");
        var reader = new RecordingDocumentReader(
        [
            BuildDocument("alice-agent", alice),
            BuildSharedDocument("shared-agent", alice, allowTrigger: true),
            BuildDocument("bob-agent", bob),
        ]);
        var port = new UserAgentCatalogQueryPort(reader);

        var visible = await port.QueryVisibleByCallerAsync(alice, CancellationToken.None);

        visible.Select(static entry => entry.AgentId).Should().BeEquivalentTo(["alice-agent", "shared-agent"]);
        reader.Queries.Should().HaveCount(2);
        reader.Queries[0].Filters.Select(static filter => filter.FieldPath)
            .Should().Contain($"{nameof(UserAgentCatalogDocument.OwnerScope)}.{nameof(OwnerScope.SenderId)}");
        reader.Queries[1].Filters.Should().ContainSingle(filter =>
            filter.FieldPath == nameof(UserAgentCatalogDocument.VisibleSharingAudienceKey) &&
            string.Equals(filter.Value.RawValue as string, "lark:bot-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetVisibleForCallerAsync_AllowsSameRegistrationScopeSharedRow()
    {
        var owner = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var teammate = OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob");
        var port = new UserAgentCatalogQueryPort(new RecordingDocumentReader(
        [
            BuildSharedDocument("shared-agent", owner, allowTrigger: false),
        ]));

        var entry = await port.GetVisibleForCallerAsync("shared-agent", teammate, CancellationToken.None);

        entry.Should().NotBeNull();
        entry!.AgentId.Should().Be("shared-agent");
    }

    [Fact]
    public async Task GetTriggerableForCallerAsync_RequiresTriggerGrant()
    {
        var owner = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var teammate = OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob");
        var port = new UserAgentCatalogQueryPort(new RecordingDocumentReader(
        [
            BuildSharedDocument("view-only-agent", owner, allowTrigger: false),
            BuildSharedDocument("trigger-agent", owner, allowTrigger: true),
        ]));

        var viewOnly = await port.GetTriggerableForCallerAsync("view-only-agent", teammate, CancellationToken.None);
        var triggerable = await port.GetTriggerableForCallerAsync("trigger-agent", teammate, CancellationToken.None);

        viewOnly.Should().BeNull();
        triggerable.Should().NotBeNull();
        triggerable!.AgentId.Should().Be("trigger-agent");
    }

    [Fact]
    public async Task SharedAccess_DifferentRegistrationScope_ReturnsNull()
    {
        var owner = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var otherScope = OwnerScope.ForChannel("user-B", "lark", "bot-2", "bob");
        var port = new UserAgentCatalogQueryPort(new RecordingDocumentReader(
        [
            BuildSharedDocument("shared-agent", owner, allowTrigger: true),
        ]));

        var visible = await port.GetVisibleForCallerAsync("shared-agent", otherScope, CancellationToken.None);
        var triggerable = await port.GetTriggerableForCallerAsync("shared-agent", otherScope, CancellationToken.None);

        visible.Should().BeNull();
        triggerable.Should().BeNull();
    }

    private static UserAgentCatalogDocument BuildDocument(string agentId, OwnerScope ownerScope) =>
        new()
        {
            Id = agentId,
            AgentType = SkillRunnerDefaults.AgentType,
            TemplateName = "summary",
            OwnerScope = ownerScope.Clone(),
            StateVersion = 1,
            ActorId = UserAgentCatalogGAgent.WellKnownId,
        };

    private static UserAgentCatalogDocument BuildSharedDocument(
        string agentId,
        OwnerScope ownerScope,
        bool allowTrigger)
    {
        var document = BuildDocument(agentId, ownerScope);
        document.SharingGrant = new ScheduledAgentSharingGrant
        {
            SharedWithRegistrationScope = ownerScope.RegistrationScopeId,
            AllowTrigger = allowTrigger,
            GrantedBy = ownerScope.SenderId,
        };
        document.VisibleSharingAudienceKey = $"{ownerScope.Platform}:{ownerScope.RegistrationScopeId}";
        document.TriggerSharingAudienceKey = allowTrigger ? document.VisibleSharingAudienceKey : string.Empty;
        return document;
    }

    private sealed class RecordingDocumentReader : IProjectionDocumentReader<UserAgentCatalogDocument, string>
    {
        private readonly IList<UserAgentCatalogDocument> _items;

        public List<ProjectionDocumentQuery> Queries { get; } = [];

        public RecordingDocumentReader(IList<UserAgentCatalogDocument> items)
        {
            _items = items;
        }

        public Task<UserAgentCatalogDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var match = _items.FirstOrDefault(item => string.Equals(item.Id, key, StringComparison.Ordinal));
            return Task.FromResult(match?.Clone());
        }

        public Task<ProjectionDocumentQueryResult<UserAgentCatalogDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Queries.Add(query);
            IEnumerable<UserAgentCatalogDocument> filtered = _items.Select(static item => item.Clone());
            foreach (var filter in query.Filters)
            {
                filtered = filtered.Where(item => MatchesFilter(item, filter));
            }

            var page = filtered.Take(query.Take).ToArray();
            return Task.FromResult(new ProjectionDocumentQueryResult<UserAgentCatalogDocument>
            {
                Items = page,
            });
        }

        private static bool MatchesFilter(UserAgentCatalogDocument document, ProjectionDocumentFilter filter)
        {
            if (filter.Operator != ProjectionDocumentFilterOperator.Eq)
                return true;

            var actual = filter.FieldPath switch
            {
                "OwnerScope.NyxUserId" => document.OwnerScope?.NyxUserId ?? string.Empty,
                "OwnerScope.Platform" => document.OwnerScope?.Platform ?? string.Empty,
                "OwnerScope.RegistrationScopeId" => document.OwnerScope?.RegistrationScopeId ?? string.Empty,
                "OwnerScope.SenderId" => document.OwnerScope?.SenderId ?? string.Empty,
                nameof(UserAgentCatalogDocument.VisibleSharingAudienceKey) => document.VisibleSharingAudienceKey,
                nameof(UserAgentCatalogDocument.TriggerSharingAudienceKey) => document.TriggerSharingAudienceKey,
                _ => string.Empty,
            };
            return string.Equals(actual, filter.Value.RawValue as string, StringComparison.Ordinal);
        }
    }
}
