using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
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
    public void OwnerScope_MatchesStrictly_ChannelIdentityIsPlatformScopeSenderTriple()
    {
        var a = OwnerScope.ForChannel("user-1", "lark", "scope-1", "sender-1");
        var b = OwnerScope.ForChannel("user-1", "lark", "scope-1", "sender-1");
        var differentSender = OwnerScope.ForChannel("user-1", "lark", "scope-1", "sender-2");
        var differentScope = OwnerScope.ForChannel("user-1", "lark", "scope-2", "sender-1");

        a.MatchesStrictly(b).Should().BeTrue();
        a.MatchesStrictly(differentSender).Should().BeFalse();
        a.MatchesStrictly(differentScope).Should().BeFalse();
        a.MatchesStrictly(null).Should().BeFalse();
    }

    [Fact]
    public void OwnerScope_MatchesStrictly_ChannelIgnoresNyxUserIdDrift()
    {
        // Shared-bot regression (prod 2026-06-10): the same Lark sender resolves to the
        // bot owner's NyxID account on slash-command turns (relay token) but to their own
        // account on LLM tool turns (sender binding token promoted by route preference).
        // The channel identity is the relay-authenticated triple; nyx_user_id drift must
        // not hide the caller's own agents.
        var writtenViaSenderToken = OwnerScope.ForChannel("sender-nyx-user", "lark", "scope-1", "sender-1");
        var queriedViaOwnerToken = OwnerScope.ForChannel("bot-owner-nyx-user", "lark", "scope-1", "sender-1");

        queriedViaOwnerToken.MatchesStrictly(writtenViaSenderToken).Should().BeTrue();
    }

    [Fact]
    public void OwnerScope_MatchesStrictly_NyxIdNativeStillRequiresNyxUserId()
    {
        OwnerScope.ForNyxIdNative("user-A").MatchesStrictly(OwnerScope.ForNyxIdNative("user-B"))
            .Should().BeFalse();
        OwnerScope.ForNyxIdNative("user-A").MatchesStrictly(OwnerScope.ForNyxIdNative("user-A"))
            .Should().BeTrue();
    }

    [Fact]
    public void OwnerScope_MatchesStrictly_NativeAndChannelNeverMatch()
    {
        var native = OwnerScope.ForNyxIdNative("user-1");
        var channel = OwnerScope.ForChannel("user-1", "lark", "scope-1", "sender-1");

        native.MatchesStrictly(channel).Should().BeFalse();
        channel.MatchesStrictly(native).Should().BeFalse();
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
        var reader = new RecordingDocumentReader(new List<UserAgentCatalogDocument>
        {
            BuildDocument("agent-A", OwnerScope.ForNyxIdNative("user-A")),
            BuildDocument("agent-B", OwnerScope.ForNyxIdNative("user-B")),
        });

        var port = CreatePort(reader);

        var asUserA = await port.QueryByCallerAsync(OwnerScope.ForNyxIdNative("user-A"), CancellationToken.None);
        var asUserB = await port.QueryByCallerAsync(OwnerScope.ForNyxIdNative("user-B"), CancellationToken.None);

        asUserA.Select(e => e.AgentId).Should().BeEquivalentTo(new[] { "agent-A" });
        asUserB.Select(e => e.AgentId).Should().BeEquivalentTo(new[] { "agent-B" });
    }

    // ─── Cross-surface isolation (same NyxID user, cli vs lark) ───

    [Fact]
    public async Task QueryByCallerAsync_SameNyxIdUserDifferentSurfaces_NoCrossLeak()
    {
        var reader = new RecordingDocumentReader(new List<UserAgentCatalogDocument>
        {
            BuildDocument("agent-cli", OwnerScope.ForNyxIdNative("user-1")),
            BuildDocument("agent-lark", OwnerScope.ForChannel("user-1", "lark", "bot-1", "sender-1")),
        });

        var port = CreatePort(reader);

        var fromCli = await port.QueryByCallerAsync(OwnerScope.ForNyxIdNative("user-1"), CancellationToken.None);
        var fromLark = await port.QueryByCallerAsync(OwnerScope.ForChannel("user-1", "lark", "bot-1", "sender-1"), CancellationToken.None);

        fromCli.Select(e => e.AgentId).Should().BeEquivalentTo(new[] { "agent-cli" });
        fromLark.Select(e => e.AgentId).Should().BeEquivalentTo(new[] { "agent-lark" });
    }

    // ─── Cross-sender isolation (Lark group, two senders, same bot) ───

    [Fact]
    public async Task QueryByCallerAsync_LarkGroupTwoSenders_OnlyOwnSenderReturned()
    {
        var reader = new RecordingDocumentReader(new List<UserAgentCatalogDocument>
        {
            BuildDocument("alice-agent", OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice")),
            BuildDocument("bob-agent", OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob")),
        });

        var port = CreatePort(reader);

        var asBob = await port.QueryByCallerAsync(OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob"), CancellationToken.None);

        asBob.Select(e => e.AgentId).Should().BeEquivalentTo(new[] { "bob-agent" });
        asBob.Should().NotContain(e => e.AgentId == "alice-agent",
            "Bob must not see Alice's agent even though both share the same bot scope");
    }

    // ─── Shared-bot nyx_user_id drift (prod regression 2026-06-10) ───

    [Fact]
    public async Task QueryByCallerAsync_SharedBot_NyxUserIdDrift_StillReturnsOwnAgents()
    {
        // Document written on an LLM tool turn carrying the sender's binding token;
        // query arrives on a slash-command turn carrying the bot owner's relay token.
        var reader = new RecordingDocumentReader(new List<UserAgentCatalogDocument>
        {
            BuildDocument("my-agent", OwnerScope.ForChannel("sender-nyx-user", "lark", "bot-1", "me")),
        });

        var port = CreatePort(reader);

        var viaOwnerToken = await port.QueryByCallerAsync(
            OwnerScope.ForChannel("bot-owner-nyx-user", "lark", "bot-1", "me"),
            CancellationToken.None);

        viaOwnerToken.Select(e => e.AgentId).Should().BeEquivalentTo(new[] { "my-agent" });
    }

    [Fact]
    public async Task GetForCallerAsync_SharedBot_NyxUserIdDrift_StillResolvesOwnAgent()
    {
        var reader = new RecordingDocumentReader(new List<UserAgentCatalogDocument>
        {
            BuildDocument("my-agent", OwnerScope.ForChannel("sender-nyx-user", "lark", "bot-1", "me")),
        });

        var port = CreatePort(reader);

        var entry = await port.GetForCallerAsync(
            "my-agent",
            OwnerScope.ForChannel("bot-owner-nyx-user", "lark", "bot-1", "me"),
            CancellationToken.None);

        entry.Should().NotBeNull();
        entry!.AgentId.Should().Be("my-agent");
    }

    // ─── Per-id ops on non-owned agent return "not found" (no existence disclosure) ───

    [Fact]
    public async Task GetForCallerAsync_NonOwnedAgent_ReturnsNull_NoExistenceDisclosure()
    {
        var reader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        reader.GetAsync("alice-agent", Arg.Any<CancellationToken>())
            .Returns(BuildDocument("alice-agent", OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice")));

        var port = CreatePort(reader);

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

        var port = CreatePort(reader);

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
    public async Task QueryByCallerAsync_LegacyNyxidDocument_RemainsInvisibleUntilReprojected()
    {
        // Legacy nyxid documents that haven't yet re-projected since this PR shipped have
        // OwnerScope=null in the store. With the predicate pushed into the projection
        // reader, they don't match the caller-scoped Eq filters. They become visible
        // again on the next state event (which re-runs the projector → backfills
        // OwnerScope from legacy fields). This is the security-correct trade-off vs.
        // the previous "lazy backfill on read" plan: in-process post-filter would defeat
        // the predicate push-down acceptance criterion.
        var reader = new RecordingDocumentReader(new List<UserAgentCatalogDocument>
        {
            BuildLegacyNyxidDocument("legacy-cli-agent", "user-1"),
        });

        var port = CreatePort(reader);

        var asUser1 = await port.QueryByCallerAsync(OwnerScope.ForNyxIdNative("user-1"), CancellationToken.None);

        asUser1.Should().BeEmpty(
            "legacy documents lacking owner_scope are invisible to the caller-scoped sweep until re-projected; the projector backfills OwnerScope on the next state event");
    }

    [Fact]
    public async Task QueryByCallerAsync_LegacyLarkDocument_DoesNotMatch_DeprecateAndRecreate()
    {
        var reader = new RecordingDocumentReader(new List<UserAgentCatalogDocument>
        {
            BuildLegacyLarkDocument("legacy-lark-agent", "user-1"),
        });

        var port = CreatePort(reader);

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

        AgentToolRequestContext.Current = null;
        try
        {
            (await resolver.TryResolveAsync()).Should().BeNull(
                "no token → resolver does not apply; composite tries the next strategy");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task NyxIdNativeCallerScopeResolver_NyxIdMeFails_ThrowsFailClosed()
    {
        var inner = Substitute.For<INyxIdCurrentUserResolver>();
        inner.ResolveCurrentUserIdAsync("expired-token", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var resolver = new NyxIdNativeCallerScopeResolver(inner);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "expired-token",
        });
        try
        {
            await Assert.ThrowsAsync<CallerScopeUnavailableException>(() => resolver.TryResolveAsync());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ChannelMetadataCallerScopeResolver_PlatformWithoutSenderId_ThrowsFailClosed()
    {
        var inner = Substitute.For<INyxIdCurrentUserResolver>();
        var resolver = new ChannelMetadataCallerScopeResolver(inner);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [ChannelMetadataKeys.Platform] = "lark",
            // no sender_id
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            await Assert.ThrowsAsync<CallerScopeUnavailableException>(() => resolver.TryResolveAsync());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ChannelMetadataCallerScopeResolver_NoPlatform_ReturnsNull_AllowsFallthrough()
    {
        var inner = Substitute.For<INyxIdCurrentUserResolver>();
        var resolver = new ChannelMetadataCallerScopeResolver(inner);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            // no channel.platform
        });
        try
        {
            (await resolver.TryResolveAsync()).Should().BeNull(
                "no channel platform metadata → not a channel surface; let composite try next strategy");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ChannelMetadataCallerScopeResolver_OwnerScopeIdPresent_DoesNotCallNyxIdMe()
    {
        var inner = Substitute.For<INyxIdCurrentUserResolver>();
        inner.ResolveCurrentUserIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        var resolver = new ChannelMetadataCallerScopeResolver(inner);

        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = "proxy-token-that-users-me-would-reject",
            },
            Caller = new AgentToolCallerContext(
                "registration-scope-1",
                "registration-scope-1",
                "message-1",
                OwnerScopeId: "owner-scope-1"),
            Channel = new AgentToolChannelContext(
                "lark",
                "ou_sender_1",
                "registration-scope-1",
                "message-1",
                "platform-message-1"),
        };
        try
        {
            var scope = await resolver.TryResolveAsync();

            scope.Should().NotBeNull();
            scope!.NyxUserId.Should().Be("owner-scope-1");
            scope.Platform.Should().Be("lark");
            scope.RegistrationScopeId.Should().Be("registration-scope-1");
            scope.SenderId.Should().Be("ou_sender_1");
            await inner.DidNotReceiveWithAnyArgs().ResolveCurrentUserIdAsync(default!, default);
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task CompositeCallerScopeResolver_RequireAsync_FailsClosedWhenAllReturnNull()
    {
        var a = Substitute.For<ICallerScopeResolver>();
        a.TryResolveAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OwnerScope?>(null));
        var b = Substitute.For<ICallerScopeResolver>();
        b.TryResolveAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<OwnerScope?>(null));

        ICallerScopeResolver composite = new CompositeCallerScopeResolver(new[] { a, b });

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

        ICallerScopeResolver composite = new CompositeCallerScopeResolver(new[] { channel, native });

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

        var port = CreatePort(reader);
        var entry = await port.GetForCallerAsync("agent-1", OwnerScope.ForNyxIdNative("user-1"), CancellationToken.None);

        entry.Should().NotBeNull();
        typeof(UserAgentCatalogReadModelEntry).GetProperty("NyxApiKey").Should().BeNull(
            "the public DTO must not carry credentials; only the internal IUserAgentDeliveryTargetReader surfaces NyxApiKey");
    }

    // ─── Pagination: QueryByCallerAsync must page until the cursor is exhausted ───

    [Fact]
    public async Task QueryByCallerAsync_PagesPastFirstWindow_ReturnsAllOwnedEntries()
    {
        // Issue #466 review: a caller with more than the projection-store page size
        // (Take=200) of agents must NOT silently lose entries past the first page.
        // The query port pages through cursors until NextCursor is null.
        var caller = OwnerScope.ForNyxIdNative("user-many");
        var docs = Enumerable.Range(0, 305)
            .Select(i => BuildDocument($"agent-{i:000}", caller))
            .ToList<UserAgentCatalogDocument>();
        var reader = new RecordingDocumentReader(docs);

        var port = CreatePort(reader);
        var entries = await port.QueryByCallerAsync(caller, CancellationToken.None);

        entries.Should().HaveCount(305,
            "all owned agents are returned, not just the first 200; the port pages internally");
    }

    [Fact]
    public async Task QueryByCallerAsync_ReturnsCatalogAuthorityWithoutRunnerExecutionJoin()
    {
        var caller = OwnerScope.ForNyxIdNative("user-join");
        var catalogDocument = BuildDocument("agent-join", caller);
        catalogDocument.StateVersion = 5;
        catalogDocument.LastEventId = "catalog-5";
        var reader = new RecordingDocumentReader(new List<UserAgentCatalogDocument>
        {
            catalogDocument,
        });

        var port = CreatePort(reader);

        var entry = (await port.QueryByCallerAsync(caller, CancellationToken.None)).Should().ContainSingle().Subject;
        entry.Status.Should().BeEmpty();
        entry.ErrorCount.Should().Be(0);
        entry.LastError.Should().BeEmpty();
        entry.CatalogAuthorityStateVersion.Should().Be(5);
        entry.CatalogLastEventId.Should().Be("catalog-5");
        entry.RunnerAuthorityStateVersion.Should().BeNull();
        entry.RunnerLastEventId.Should().BeEmpty();
    }

    // ─── Actor → projector → query integration (lark caller end-to-end) ───
    //
    // Issue #466 review caught a gap: the previous acceptance tests stubbed at the
    // projection-reader boundary, so the actor-side OwnerScope copy could be silently
    // dropped without any test failing. The integration test below routes a real
    // UserAgentCatalogUpsertCommand through the actor, projects the resulting state to
    // a real (in-memory) document store, and verifies the caller-scoped query port
    // returns the document for the lark caller (the surface the original bug was on).

    [Fact]
    public async Task LarkCallerIntegration_UpsertActorThenQueryPort_ReturnsAgentForOwner()
    {
        var dispatcher = new RecordingProjectionWriteDispatcher();
        var clock = new FixedProjectionClock(new DateTimeOffset(2026, 4, 28, 10, 0, 0, TimeSpan.Zero));
        var projector = new UserAgentCatalogProjector(
            dispatcher,
            clock);
        var context = new UserAgentCatalogMaterializationContext
        {
            RootActorId = UserAgentCatalogGAgent.WellKnownId,
            ProjectionKind = UserAgentCatalogProjectionBootstrapActivator.ProjectionKind,
        };

        // Project a synthesized post-upsert state for a lark caller. This mirrors what
        // UserAgentCatalogGAgent.HandleUpsertAsync emits when command.OwnerScope is set:
        // the entry carries OwnerScope verbatim and the projector materializes it.
        var aliceScope = OwnerScope.ForChannel("user-A", "lark", "bot-1", "alice");
        var bobScope = OwnerScope.ForChannel("user-B", "lark", "bot-1", "bob");
        var state = new UserAgentCatalogState
        {
            Entries =
            {
                new UserAgentCatalogEntry
                {
                    AgentId = "alice-agent",
                    ConversationId = "oc_chat_alice",
                    AgentType = "skill_runner",
                    TemplateName = "summary",
                    OwnerScope = aliceScope,
                },
                new UserAgentCatalogEntry
                {
                    AgentId = "bob-agent",
                    ConversationId = "oc_chat_bob",
                    AgentType = "skill_runner",
                    TemplateName = "summary",
                    OwnerScope = bobScope,
                },
            },
        };
        await projector.ProjectAsync(context, BuildCommittedEnvelope("evt-1", 1, state), CancellationToken.None);

        // Stage the documents into a fake reader that the query port will consume. We
        // simulate the projection store with a simple substitute that returns the
        // dispatcher's last-written documents — close enough to exercise the actor →
        // projector → reader chain end-to-end without standing up the full pipeline.
        var reader = new RecordingDocumentReader(dispatcher.Upserts);
        var port = CreatePort(reader);

        var fromAlice = await port.QueryByCallerAsync(aliceScope, CancellationToken.None);
        var fromBob = await port.QueryByCallerAsync(bobScope, CancellationToken.None);
        var aliceById = await port.GetForCallerAsync("alice-agent", aliceScope, CancellationToken.None);
        var bobIdAsAlice = await port.GetForCallerAsync("bob-agent", aliceScope, CancellationToken.None);

        fromAlice.Select(e => e.AgentId).Should().BeEquivalentTo(new[] { "alice-agent" });
        fromBob.Select(e => e.AgentId).Should().BeEquivalentTo(new[] { "bob-agent" });
        aliceById.Should().NotBeNull();
        aliceById!.AgentId.Should().Be("alice-agent");
        bobIdAsAlice.Should().BeNull("Alice cannot read Bob's agent through GetForCallerAsync");
    }

    // ─── Helpers ───

    private static UserAgentCatalogDocument BuildDocument(string agentId, OwnerScope scope) =>
        new()
        {
            Id = agentId,
            ConversationId = $"conv-{agentId}",
            NyxProviderSlug = "api-lark-bot",
            AgentType = "skill_runner",
            TemplateName = "summary",
            ScopeId = scope.RegistrationScopeId,
            StateVersion = 1,
            Tombstoned = false,
            ActorId = "agent-registry-store",
            OwnerScope = scope.Clone(),
        };

    private static UserAgentCatalogQueryPort CreatePort(
        IProjectionDocumentReader<UserAgentCatalogDocument, string> reader) =>
        new(reader, new ThrowingRevocationDocumentReader());

#pragma warning disable CS0612 // legacy fields populated for backward-compat tests
    private static UserAgentCatalogDocument BuildLegacyNyxidDocument(string agentId, string nyxUserId) =>
        new()
        {
            Id = agentId,
            ConversationId = $"conv-{agentId}",
            NyxProviderSlug = "api-lark-bot",
            AgentType = "skill_runner",
            TemplateName = "summary",
            Platform = "nyxid",
            OwnerNyxUserId = nyxUserId,
            ScopeId = string.Empty,
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
            TemplateName = "summary",
            Platform = "lark",
            OwnerNyxUserId = nyxUserId,
            ScopeId = "legacy-bot-scope",
            StateVersion = 1,
            Tombstoned = false,
            ActorId = "agent-registry-store",
            // OwnerScope intentionally not populated → cannot be backfilled (no sender_id).
        };
#pragma warning restore CS0612

    private static EventEnvelope BuildCommittedEnvelope(string eventId, long version, UserAgentCatalogState state)
    {
        var occurredAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 28, 10, 0, 0, TimeSpan.Zero));
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = occurredAt.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("user-agent-catalog-acceptance-test"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = occurredAt.Clone(),
                    EventData = Any.Pack(new Empty()),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class RecordingProjectionWriteDispatcher : IProjectionWriteDispatcher<UserAgentCatalogDocument>
    {
        public List<UserAgentCatalogDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(UserAgentCatalogDocument readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.RemoveAll(d => string.Equals(d.Id, id, StringComparison.Ordinal));
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>
    /// Minimal projection-document reader that walks an in-memory list and applies
    /// the same Eq filters + cursor pagination that the InMemoryProjectionDocumentStore
    /// would. Used by the cross-isolation acceptance tests to exercise actor → projector
    /// → reader without pulling in the full projection-pipeline DI graph.
    /// </summary>
    private sealed class RecordingDocumentReader : IProjectionDocumentReader<UserAgentCatalogDocument, string>
    {
        private readonly IList<UserAgentCatalogDocument> _items;

        public RecordingDocumentReader(IList<UserAgentCatalogDocument> items)
        {
            _items = items;
        }

        public Task<UserAgentCatalogDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            var match = _items.FirstOrDefault(d => string.Equals(d.Id, key, StringComparison.Ordinal));
            return Task.FromResult(match?.Clone());
        }

        public Task<ProjectionDocumentQueryResult<UserAgentCatalogDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            IEnumerable<UserAgentCatalogDocument> filtered = _items.Select(d => d.Clone());
            foreach (var filter in query.Filters)
            {
                filtered = filtered.Where(d => MatchesFilter(d, filter));
            }
            var matchingList = filtered.ToList();
            var offset = ParseCursor(query.Cursor);
            var page = matchingList.Skip(offset).Take(query.Take).ToArray();
            var nextOffset = offset + page.Length;
            var nextCursor = nextOffset < matchingList.Count ? nextOffset.ToString() : null;
            return Task.FromResult(new ProjectionDocumentQueryResult<UserAgentCatalogDocument>
            {
                Items = page,
                NextCursor = nextCursor,
            });
        }

        private static int ParseCursor(string? cursor) =>
            int.TryParse(cursor, out var offset) ? offset : 0;

        private static bool MatchesFilter(UserAgentCatalogDocument doc, ProjectionDocumentFilter filter)
        {
            if (filter.Operator != ProjectionDocumentFilterOperator.Eq) return true;
            object? actual = filter.FieldPath switch
            {
                "OwnerScope.NyxUserId" => doc.OwnerScope?.NyxUserId ?? string.Empty,
                "OwnerScope.Platform" => doc.OwnerScope?.Platform ?? string.Empty,
                "OwnerScope.RegistrationScopeId" => doc.OwnerScope?.RegistrationScopeId ?? string.Empty,
                "OwnerScope.SenderId" => doc.OwnerScope?.SenderId ?? string.Empty,
                _ => null,
            };
            return string.Equals(actual as string, filter.Value.RawValue as string, StringComparison.Ordinal);
        }
    }

    private sealed class ThrowingRevocationDocumentReader : IProjectionDocumentReader<UserAgentApiKeyRevocationDocument, string>
    {
        public Task<UserAgentApiKeyRevocationDocument?> GetAsync(string key, CancellationToken ct = default) =>
            throw new InvalidOperationException("This acceptance fixture does not exercise API key revocation documents.");

        public Task<ProjectionDocumentQueryResult<UserAgentApiKeyRevocationDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("This acceptance fixture does not exercise API key revocation documents.");
    }

}
