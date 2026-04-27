using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Acceptance tests for issue #466 (unify caller scope across cli/web/lark/telegram).
/// Each fact maps directly to a bullet in the issue's "Acceptance" checklist.
///
/// These tests exercise the contract surface (query port, resolver layer, document
/// matching) rather than full tool flows; the cross-isolation and per-id
/// non-disclosure properties hold at the projection-filter boundary, so they're
/// expressible without standing up an actor runtime.
/// </summary>
public sealed class UnifyCallerScopeAcceptanceTests
{
    // ─── OwnerScope value semantics ───

    [Fact]
    public void OwnerScope_ForNyxIdNative_ProducesCanonicalNyxIdTuple()
    {
        var scope = OwnerScope.ForNyxIdNative("user-1");

        scope.NyxUserId.Should().Be("user-1");
        scope.Platform.Should().Be("nyxid");
        scope.RegistrationScopeId.Should().BeEmpty();
        scope.SenderId.Should().BeEmpty();
        scope.IsNyxIdNative.Should().BeTrue();
    }

    [Fact]
    public void OwnerScope_ForChannel_NormalizesPlatformLowercase()
    {
        var scope = OwnerScope.ForChannel("user-A", "Lark", "scope-bot-1", "sender-1");

        scope.Platform.Should().Be("lark");
        scope.IsNyxIdNative.Should().BeFalse();
    }

    [Theory]
    [InlineData("", "lark", "scope-1", "sender-1", "nyx_user_id")]
    [InlineData("user-1", "", "scope-1", "sender-1", "platform")]
    [InlineData("user-1", "lark", "", "sender-1", "registration_scope_id")]
    [InlineData("user-1", "lark", "scope-1", "", "sender_id")]
    public void OwnerScope_TryValidate_RejectsMissingFields(
        string nyxUserId, string platform, string scope, string sender, string expectedFieldInError)
    {
        var ownerScope = new OwnerScope
        {
            NyxUserId = nyxUserId,
            Platform = platform,
            RegistrationScopeId = scope,
            SenderId = sender,
        };

        ownerScope.TryValidate(out var error).Should().BeFalse();
        error.Should().Contain(expectedFieldInError);
    }

    [Fact]
    public void OwnerScope_TryValidate_NyxIdNativeAcceptsEmptyScopeAndSender()
    {
        OwnerScope.ForNyxIdNative("user-1").TryValidate(out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void OwnerScope_MatchesStrictly_FullTupleEquality()
    {
        var a = OwnerScope.ForChannel("user-1", "lark", "scope-1", "sender-1");
        var b = OwnerScope.ForChannel("user-1", "lark", "scope-1", "sender-1");
        var differentSender = OwnerScope.ForChannel("user-1", "lark", "scope-1", "sender-2");

        a.MatchesStrictly(b).Should().BeTrue();
        a.MatchesStrictly(differentSender).Should().BeFalse();
        a.MatchesStrictly(null).Should().BeFalse();
    }

    [Fact]
    public void OwnerScope_FromLegacyFields_NyxidSurface_Backfills()
    {
        // cli/web (Platform="nyxid" or empty) lazy backfills from legacy fields.
        OwnerScope.FromLegacyFields("user-1", "nyxid")!.MatchesStrictly(OwnerScope.ForNyxIdNative("user-1"))
            .Should().BeTrue();
        OwnerScope.FromLegacyFields("user-1", "")!.MatchesStrictly(OwnerScope.ForNyxIdNative("user-1"))
            .Should().BeTrue();
    }

    [Fact]
    public void OwnerScope_FromLegacyFields_LarkSurface_DoesNotBackfill()
    {
        // Legacy lark documents have no sender_id; per issue #466 migration plan,
        // backfilling would soft-match other senders on the same bot. Force recreate.
        OwnerScope.FromLegacyFields("user-1", "lark").Should().BeNull();
    }

    // ─── Cross-NyxID isolation (same surface, two NyxID users) ───

    [Fact]
    public async Task QueryByCallerAsync_TwoNyxIdUsers_OnlyOwnEntriesReturned()
    {
        var reader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ProjectionDocumentQueryResult<UserAgentCatalogDocument>
            {
                Items = new[]
                {
                    BuildDocument("agent-A", OwnerScope.ForNyxIdNative("user-A")),
                    BuildDocument("agent-B", OwnerScope.ForNyxIdNative("user-B")),
                },
            });

        var port = new UserAgentCatalogQueryPort(reader);

        var asUserA = await port.QueryByCallerAsync(OwnerScope.ForNyxIdNative("user-A"), CancellationToken.None);
        var asUserB = await port.QueryByCallerAsync(OwnerScope.ForNyxIdNative("user-B"), CancellationToken.None);

        asUserA.Select(e => e.AgentId).Should().BeEquivalentTo(new[] { "agent-A" });
        asUserB.Select(e => e.AgentId).Should().BeEquivalentTo(new[] { "agent-B" });
    }

    // ─── Cross-surface isolation (same NyxID user, cli vs lark) ───

    [Fact]
    public async Task QueryByCallerAsync_SameNyxIdUserDifferentSurfaces_NoCrossLeak()
    {
        var reader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ProjectionDocumentQueryResult<UserAgentCatalogDocument>
            {
                Items = new[]
                {
                    BuildDocument("agent-cli", OwnerScope.ForNyxIdNative("user-1")),
                    BuildDocument("agent-lark", OwnerScope.ForChannel("user-1", "lark", "bot-1", "sender-1")),
                },
            });

        var port = new UserAgentCatalogQueryPort(reader);

        var fromCli = await port.QueryByCallerAsync(OwnerScope.ForNyxIdNative("user-1"), CancellationToken.None);
        var fromLark = await port.QueryByCallerAsync(OwnerScope.ForChannel("user-1", "lark", "bot-1", "sender-1"), CancellationToken.None);

        fromCli.Select(e => e.AgentId).Should().BeEquivalentTo(new[] { "agent-cli" });
        fromLark.Select(e => e.AgentId).Should().BeEquivalentTo(new[] { "agent-lark" });
    }

    // ─── Cross-sender isolation (Lark group, two senders, same bot) ───

    [Fact]
    public async Task QueryByCallerAsync_LarkGroupTwoSenders_OnlyOwnSenderReturned()
    {
        var reader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ProjectionDocumentQueryResult<UserAgentCatalogDocument>
            {
                Items = new[]
                {
                    BuildDocument("alice-agent", OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice")),
                    BuildDocument("bob-agent", OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob")),
                },
            });

        var port = new UserAgentCatalogQueryPort(reader);

        var asBob = await port.QueryByCallerAsync(OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob"), CancellationToken.None);

        asBob.Select(e => e.AgentId).Should().BeEquivalentTo(new[] { "bob-agent" });
        asBob.Should().NotContain(e => e.AgentId == "alice-agent",
            "Bob must not see Alice's agent even though both share the same bot scope");
    }

    // ─── Per-id ops on non-owned agent return "not found" (no existence disclosure) ───

    [Fact]
    public async Task GetForCallerAsync_NonOwnedAgent_ReturnsNull_NoExistenceDisclosure()
    {
        var reader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        reader.GetAsync("alice-agent", Arg.Any<CancellationToken>())
            .Returns(BuildDocument("alice-agent", OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice")));

        var port = new UserAgentCatalogQueryPort(reader);

        var asBob = await port.GetForCallerAsync(
            "alice-agent",
            OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob"),
            CancellationToken.None);
        var asAliceMissing = await port.GetForCallerAsync(
            "doesnt-exist",
            OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice"),
            CancellationToken.None);

        asBob.Should().BeNull("non-owned agent_id collapses to null");
        asAliceMissing.Should().BeNull("missing agent_id also returns null");
    }

    // ─── Version non-disclosure: GetStateVersion does not differ for owned vs non-owned ───

    [Fact]
    public async Task GetStateVersionForCallerAsync_NonOwnedAgent_ReturnsNull()
    {
        var reader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var doc = BuildDocument("alice-agent", OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice"));
        doc.StateVersion = 42;
        reader.GetAsync("alice-agent", Arg.Any<CancellationToken>()).Returns(doc);

        var port = new UserAgentCatalogQueryPort(reader);

        var asAlice = await port.GetStateVersionForCallerAsync(
            "alice-agent",
            OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice"),
            CancellationToken.None);
        var asBob = await port.GetStateVersionForCallerAsync(
            "alice-agent",
            OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob"),
            CancellationToken.None);
        var missing = await port.GetStateVersionForCallerAsync(
            "no-such-agent",
            OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice"),
            CancellationToken.None);

        asAlice.Should().Be(42);
        asBob.Should().BeNull("non-owner gets the same null as a missing id; no version oracle");
        missing.Should().BeNull();
    }

    // ─── Legacy migration: nyxid lazy-backfill, lark deprecate-recreate ───

    [Fact]
    public async Task QueryByCallerAsync_LegacyNyxidDocument_BackfillsAndMatches()
    {
        var reader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ProjectionDocumentQueryResult<UserAgentCatalogDocument>
            {
                Items = new[] { BuildLegacyNyxidDocument("legacy-cli-agent", "user-1") },
            });

        var port = new UserAgentCatalogQueryPort(reader);

        var asUser1 = await port.QueryByCallerAsync(OwnerScope.ForNyxIdNative("user-1"), CancellationToken.None);

        asUser1.Select(e => e.AgentId).Should().BeEquivalentTo(new[] { "legacy-cli-agent" });
    }

    [Fact]
    public async Task QueryByCallerAsync_LegacyLarkDocument_DoesNotMatch_DeprecateAndRecreate()
    {
        var reader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ProjectionDocumentQueryResult<UserAgentCatalogDocument>
            {
                Items = new[] { BuildLegacyLarkDocument("legacy-lark-agent", "user-1") },
            });

        var port = new UserAgentCatalogQueryPort(reader);

        var asUser1Lark = await port.QueryByCallerAsync(
            OwnerScope.ForChannel("user-1", "lark", "bot-1", "sender-1"),
            CancellationToken.None);

        asUser1Lark.Should().BeEmpty(
            "legacy lark documents lacked sender_id; the migration plan deprecates-and-recreates them rather than soft-matching");
    }

    // ─── Resolver fail-closed semantics ───

    [Fact]
    public async Task NyxIdNativeCallerScopeResolver_NoToken_ReturnsNull_NoFallthrough()
    {
        var inner = Substitute.For<INyxIdCurrentUserResolver>();
        var resolver = new NyxIdNativeCallerScopeResolver(inner);

        AgentToolRequestContext.CurrentMetadata = null;
        try
        {
            (await resolver.TryResolveAsync()).Should().BeNull(
                "no token → resolver does not apply; composite tries the next strategy");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task NyxIdNativeCallerScopeResolver_NyxIdMeFails_ThrowsFailClosed()
    {
        var inner = Substitute.For<INyxIdCurrentUserResolver>();
        inner.ResolveCurrentUserIdAsync("expired-token", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var resolver = new NyxIdNativeCallerScopeResolver(inner);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "expired-token",
        };
        try
        {
            await Assert.ThrowsAsync<CallerScopeUnavailableException>(() => resolver.TryResolveAsync());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ChannelMetadataCallerScopeResolver_PlatformWithoutSenderId_ThrowsFailClosed()
    {
        var inner = Substitute.For<INyxIdCurrentUserResolver>();
        var resolver = new ChannelMetadataCallerScopeResolver(inner);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [ChannelMetadataKeys.Platform] = "lark",
            // no sender_id
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            await Assert.ThrowsAsync<CallerScopeUnavailableException>(() => resolver.TryResolveAsync());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ChannelMetadataCallerScopeResolver_NoPlatform_ReturnsNull_AllowsFallthrough()
    {
        var inner = Substitute.For<INyxIdCurrentUserResolver>();
        var resolver = new ChannelMetadataCallerScopeResolver(inner);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            // no channel.platform
        };
        try
        {
            (await resolver.TryResolveAsync()).Should().BeNull(
                "no channel platform metadata → not a channel surface; let composite try next strategy");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task CompositeCallerScopeResolver_RequireAsync_FailsClosedWhenAllReturnNull()
    {
        var a = Substitute.For<ICallerScopeResolver>();
        a.TryResolveAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OwnerScope?>(null));
        var b = Substitute.For<ICallerScopeResolver>();
        b.TryResolveAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OwnerScope?>(null));

        var composite = new CompositeCallerScopeResolver(new[] { a, b });

        await Assert.ThrowsAsync<CallerScopeUnavailableException>(() => composite.RequireAsync());
    }

    [Fact]
    public async Task CompositeCallerScopeResolver_PrefersFirstNonNullResolver()
    {
        // Order matters: channel resolver must run before native so a Lark-bound request
        // still produces the per-sender tuple, not the looser nyxid scope from the
        // session token (issue #466 §B).
        var channel = Substitute.For<ICallerScopeResolver>();
        channel.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForChannel("user-1", "lark", "bot-1", "sender-1")));
        var native = Substitute.For<ICallerScopeResolver>();
        native.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));

        var composite = new CompositeCallerScopeResolver(new[] { channel, native });

        var resolved = await composite.RequireAsync();
        resolved.Platform.Should().Be("lark");
        resolved.SenderId.Should().Be("sender-1");
        // Native resolver should not have been consulted.
        await native.DidNotReceive().TryResolveAsync(Arg.Any<CancellationToken>());
    }

    // ─── Secret boundary: public DTO never carries NyxApiKey ───

    [Fact]
    public async Task UserAgentCatalogQueryPort_PublicEntry_DoesNotSurfaceNyxApiKey()
    {
        // Even if the document accidentally still carried a credential field, the
        // caller-scoped query port projects via a DTO that excludes it. Verify the
        // public entry never has the secret populated.
        var reader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        reader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(BuildDocument("agent-1", OwnerScope.ForNyxIdNative("user-1")));

        var port = new UserAgentCatalogQueryPort(reader);
        var entry = await port.GetForCallerAsync("agent-1", OwnerScope.ForNyxIdNative("user-1"), CancellationToken.None);

        entry.Should().NotBeNull();
#pragma warning disable CS0612 // verifying the deprecated field is not populated through the public surface
        entry!.NyxApiKey.Should().BeEmpty(
            "the public DTO must not carry credentials; only the internal IUserAgentDeliveryTargetReader surfaces NyxApiKey");
#pragma warning restore CS0612
    }

    // ─── Helpers ───

    private static UserAgentCatalogDocument BuildDocument(string agentId, OwnerScope scope) =>
        new()
        {
            Id = agentId,
            ConversationId = $"conv-{agentId}",
            NyxProviderSlug = "api-lark-bot",
            AgentType = "skill_runner",
            TemplateName = "daily_report",
            ScopeId = scope.RegistrationScopeId,
            Status = "running",
            StateVersion = 1,
            Tombstoned = false,
            ActorId = "agent-registry-store",
            OwnerScope = scope.Clone(),
        };

#pragma warning disable CS0612 // legacy fields populated for backward-compat tests
    private static UserAgentCatalogDocument BuildLegacyNyxidDocument(string agentId, string nyxUserId) =>
        new()
        {
            Id = agentId,
            ConversationId = $"conv-{agentId}",
            NyxProviderSlug = "api-lark-bot",
            AgentType = "skill_runner",
            TemplateName = "daily_report",
            Platform = "nyxid",
            OwnerNyxUserId = nyxUserId,
            ScopeId = string.Empty,
            Status = "running",
            StateVersion = 1,
            Tombstoned = false,
            ActorId = "agent-registry-store",
            // OwnerScope intentionally not populated → backfilled lazily on read.
        };

    private static UserAgentCatalogDocument BuildLegacyLarkDocument(string agentId, string nyxUserId) =>
        new()
        {
            Id = agentId,
            ConversationId = $"conv-{agentId}",
            NyxProviderSlug = "api-lark-bot",
            AgentType = "skill_runner",
            TemplateName = "daily_report",
            Platform = "lark",
            OwnerNyxUserId = nyxUserId,
            ScopeId = "legacy-bot-scope",
            Status = "running",
            StateVersion = 1,
            Tombstoned = false,
            ActorId = "agent-registry-store",
            // OwnerScope intentionally not populated → cannot be backfilled (no sender_id).
        };
#pragma warning restore CS0612
}
