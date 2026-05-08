using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Scheduled;
using StudioUserConfig = Aevatar.Studio.Application.Studio.Abstractions.UserConfig;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentBuilderToolTests
{
    [Fact]
    public async Task ExecuteAsync_DeleteAgent_DisablesActor_RevokesApiKey_AndTombstonesRegistry()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily",
                    ApiKeyId = "key-1",
                    OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
                }),
                Task.FromResult<UserAgentCatalogEntry?>(null));
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogEntry>>(Array.Empty<UserAgentCatalogEntry>()));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        catalogCommandPort.TombstoneAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.Observed)));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-1", """{"ok":true}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(nyxClient);
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "delete_agent",
                  "agent_id": "skill-runner-1",
                  "confirm": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("deleted");
            doc.RootElement.GetProperty("revoked_api_key_id").GetString().Should().Be("key-1");
            doc.RootElement.GetProperty("agents").GetArrayLength().Should().Be(0);
            doc.RootElement.GetProperty("delete_notice").GetString().Should().Contain("Deleted agent");

            await skillRunnerPort.Received(1).DisableAsync(
                "skill-runner-1",
                "delete_agent",
                Arg.Any<CancellationToken>());

            await catalogCommandPort.Received(1).TombstoneAsync(
                "skill-runner-1",
                Arg.Any<CancellationToken>());

            handler.Requests.Should().ContainSingle(x =>
                x.Method == HttpMethod.Delete &&
                x.Path == "/api/v1/api-keys/key-1");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAgent_ReturnsAcceptedWithPropagatingHint_WhenTombstoneDoesNotReflectWithinBudget()
    {
        // Production bug class: with the old 5 s polling budget, /delete-agent
        // routinely returned "accepted" + "tombstone is not yet reflected" while
        // the document was still visible to /agents minutes later. This guard
        // proves that when the read model legitimately stays behind, the user-
        // facing payload now nudges the user to retry rather than implying the
        // delete might not have landed at all.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-stuck", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-stuck",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily",
                ApiKeyId = "key-stuck",
                OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
            }));
        // Read-model lags forever in this test: GetStateVersionAsync keeps
        // returning the same version (the projector never advances past it),
        // and GetAsync keeps surfacing the entry.
        queryPort.GetStateVersionForCallerAsync("skill-runner-stuck", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(7L));
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogEntry>>(
                [new UserAgentCatalogEntry { AgentId = "skill-runner-stuck", OwnerScope = OwnerScope.ForNyxIdNative("user-1") }]));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Tombstone is dispatched but the projection has not yet caught up; the
        // port surfaces an Accepted outcome and the tool reports the propagating
        // notice so the user knows to re-check /agents.
        catalogCommandPort.TombstoneAsync("skill-runner-stuck", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.Accepted)));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-stuck", """{"ok":true}""");
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(nyxClient);
        // Inject a shrunk wait budget per-instance (3 attempts × 1 ms) so the
        // not-reflected branch fires in <100 ms instead of the production
        // 15 s. Per-instance state replaces the earlier mutable-static
        // approach (codex review r3141706856) so concurrent test classes
        // that exercise other AgentBuilderTool paths cannot be poisoned by
        // shrunk values leaking through process-global state.
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(
            services.BuildServiceProvider(),
            projectionWaitAttempts: 3,
            projectionWaitDelayMilliseconds: 1);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "delete_agent",
                  "agent_id": "skill-runner-stuck",
                  "confirm": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("revoked_api_key_id").GetString().Should().Be("key-stuck");
            doc.RootElement.GetProperty("delete_notice").GetString()
                .Should().Contain("Delete submitted for");
            // The new copy must point users at /agents to verify rather than
            // implying the tombstone did not land.
            doc.RootElement.GetProperty("note").GetString()
                .Should().Contain("propagating")
                .And.Contain("/agents");

            await catalogCommandPort.Received(1).TombstoneAsync(
                "skill-runner-stuck",
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunAgent_DispatchesManualTrigger()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily",
            }));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "run_agent",
                  "agent_id": "skill-runner-1"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("agent_id").GetString().Should().Be("skill-runner-1");

            await skillRunnerPort.Received(1).TriggerAsync(
                "skill-runner-1",
                "run_agent",
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunAgent_RejectsDisabledAgent()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily",
                Status = SkillRunnerDefaults.StatusDisabled,
            }));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "run_agent",
                  "agent_id": "skill-runner-1"
                }
                """);

            result.Should().Contain("is disabled");
            await skillRunnerPort.DidNotReceive().TriggerAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DisableAgent_ReturnsStatusFast_WhenProjectionAdvancesOnFirstPoll()
    {
        // Pins the new version+status dual-gate fast-exit contract: when the
        // caller-captured baseline is X and the read model advances to X+1
        // with status==expected on the very first post-dispatch poll, the
        // wait helper must exit immediately (<1 s) instead of running the
        // full 15 s budget. This guards against two regressions:
        //
        //  1. Re-introducing a status-only check (codex P3 in this PR's
        //     thread): would accept a stale replica that already happens to
        //     hold the expected historical status, returning before the
        //     dispatch is actually materialized.
        //
        //  2. Re-introducing the *helper-side* baseline capture (codex P2 in
        //     PR #413's first review pass): would capture versionBefore
        //     after dispatch, so a fast projection that already advanced
        //     the version would make versionAfter == versionBefore on every
        //     poll and burn the full budget.
        //
        // Both regressions make this test fail (case 1 by accepting before
        // the dispatch, case 2 by deadlocking past the 1 s ceiling).
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-fast", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(
                // RequireManagedAgentAsync's existence check sees the pre-disable status.
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-fast",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily",
                    Status = SkillRunnerDefaults.StatusRunning,
                }),
                // Wait helper's first poll sees the materialized disable.
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-fast",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily",
                    Status = SkillRunnerDefaults.StatusDisabled,
                }));
        // Caller's pre-dispatch baseline read returns 42; helper's post-
        // dispatch poll sees 43 (the projection materialized the disable on
        // the very next state event). Both checks pass on the first iteration.
        queryPort.GetStateVersionForCallerAsync("skill-runner-fast", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<long?>(42L),
                Task.FromResult<long?>(43L));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await tool.ExecuteAsync("""
                {
                  "action": "disable_agent",
                  "agent_id": "skill-runner-fast"
                }
                """);
            stopwatch.Stop();

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be(SkillRunnerDefaults.StatusDisabled);
            // 1 s ceiling: any regression that prevents a dual-gate first-poll
            // exit would burn the full ProjectionWaitAttempts ×
            // ProjectionWaitDelayMilliseconds budget (15 s by default).
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DisableAgent_KeepsWaitingWhenStatusMatchesButVersionStale()
    {
        // Stale-replica defense: a read replica can surface a historically
        // expected status (e.g., a previous disable→enable→disable cycle
        // left the entry's last-projected status as Disabled in some replica)
        // while the current actor has not yet processed *this* dispatch.
        // Status-only polling would accept this replica and return prematurely
        // before the dispatch materializes. The dual gate keeps waiting
        // until version advances.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-stale", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(
                // RequireManagedAgentAsync sees the canonical Running state
                // because that is what the caller observed when issuing the
                // disable. (A different replica surfaces stale Disabled below.)
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-stale",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily",
                    Status = SkillRunnerDefaults.StatusRunning,
                }),
                // Helper's terminal fallback (after budget exhausts) returns
                // a stale-but-expected-looking Disabled. With status-only
                // polling the wait would have returned this entry on the
                // first iteration. With the dual gate the version stays at
                // baseline, so the version check short-circuits before the
                // status check is even reached.
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-stale",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily",
                    Status = SkillRunnerDefaults.StatusDisabled,
                }));
        // Caller baseline = 7; replica's view never advances past 7. Helper
        // must keep iterating; we shrink the budget so the test finishes fast.
        queryPort.GetStateVersionForCallerAsync("skill-runner-stale", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(7L));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        // Shrunk budget so the version-stale path finishes in <100 ms.
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(
            services.BuildServiceProvider(),
            projectionWaitAttempts: 3,
            projectionWaitDelayMilliseconds: 1);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "disable_agent",
                  "agent_id": "skill-runner-stale"
                }
                """);

            using var doc = JsonDocument.Parse(result);

            // Path-level assertion: the helper exhausted the injected
            // 3-attempt budget instead of returning on the first status
            // match: 1 caller baseline + 3 helper iterations = 4 calls.
            // With status-only polling the helper would have returned on
            // iteration 0 without ever calling GetStateVersionAsync, so
            // total would be 1. Tightly coupled to the injected budget by
            // design — that is what pins the contract.
            await queryPort.Received(4).GetStateVersionForCallerAsync("skill-runner-stale", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>());

            // Outcome-level assertion: when the dual gate never passes, the
            // user-facing payload must NOT claim success. The wait helper
            // returns Confirmed=false (no un-gated GetAsync fallback), and
            // DisableAgentAsync surfaces the pre-dispatch entry plus an
            // honest "submitted / propagating" note. A regression that
            // re-introduces the un-gated final read OR drops the
            // confirmed/unconfirmed branching makes this test fail by
            // surfacing "Scheduling paused" + status=Disabled despite the
            // dual gate having been violated.
            doc.RootElement.GetProperty("status").GetString().Should().Be(SkillRunnerDefaults.StatusRunning);
            var note = doc.RootElement.GetProperty("note").GetString();
            note.Should().Contain("Disable submitted")
                .And.Contain("/agent-status")
                .And.NotContain("Scheduling paused");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DisableAgent_DispatchesDisableAndReturnsStatus()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily",
                    Status = SkillRunnerDefaults.StatusRunning,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }),
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily",
                    Status = SkillRunnerDefaults.StatusDisabled,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }));
        // Caller's pre-dispatch baseline read returns 5; helper's post-dispatch
        // poll sees 6, satisfying the new version+status dual gate.
        queryPort.GetStateVersionForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<long?>(5L),
                Task.FromResult<long?>(6L));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "disable_agent",
                  "agent_id": "skill-runner-1"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be(SkillRunnerDefaults.StatusDisabled);
            doc.RootElement.GetProperty("note").GetString().Should().Contain("Scheduling paused");

            await skillRunnerPort.Received(1).DisableAsync(
                "skill-runner-1",
                "disable_agent",
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_EnableAgent_DispatchesEnableAndReturnsStatus()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily",
                    Status = SkillRunnerDefaults.StatusDisabled,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }),
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily",
                    Status = SkillRunnerDefaults.StatusRunning,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }));
        // Caller's pre-dispatch baseline read returns 5; helper's post-dispatch
        // poll sees 6, satisfying the new version+status dual gate.
        queryPort.GetStateVersionForCallerAsync("skill-runner-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<long?>(5L),
                Task.FromResult<long?>(6L));

        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        services.AddSingleton(callerScopeResolver);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "enable_agent",
                  "agent_id": "skill-runner-1"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be(SkillRunnerDefaults.StatusRunning);
            doc.RootElement.GetProperty("note").GetString().Should().Contain("Scheduling resumed");

            await skillRunnerPort.Received(1).EnableAsync(
                "skill-runner-1",
                "enable_agent",
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ToolSource_Always_ReturnsTool()
    {
        var source = new AgentBuilderToolSource(new ServiceCollection().BuildServiceProvider());
        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be("agent_builder");
    }

    private sealed class RoutingJsonHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.OrdinalIgnoreCase);

        public List<RecordedRequest> Requests { get; } = [];

        public void Add(HttpMethod method, string path, string json)
        {
            _responses[BuildKey(method, path)] = json;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, path, body));

            if (_responses.TryGetValue(BuildKey(request.Method, path), out var json))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":true,"message":"not found"}""", Encoding.UTF8, "application/json"),
            };
        }

        private static string BuildKey(HttpMethod method, string path) => $"{method.Method}:{path}";
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Body);

    private sealed class StubUserConfigQueryPort : IUserConfigQueryPort
    {
        private readonly StudioUserConfig _config;

        public StubUserConfigQueryPort(StudioUserConfig config)
        {
            _config = config;
        }

        public Task<StudioUserConfig> GetAsync(CancellationToken ct = default) => Task.FromResult(_config);

        public Task<StudioUserConfig> GetAsync(string scopeId, CancellationToken ct = default) => Task.FromResult(_config);
    }

    private sealed class RecordingUserConfigCommandService : IUserConfigCommandService
    {
        public string? SavedScopeId { get; private set; }
        public StudioUserConfig? SavedConfig { get; private set; }
        public string? SavedGithubUsername { get; private set; }

        public Task SaveAsync(StudioUserConfig config, CancellationToken ct = default)
        {
            SavedConfig = config;
            return Task.CompletedTask;
        }

        public Task SaveAsync(string scopeId, StudioUserConfig config, CancellationToken ct = default)
        {
            SavedScopeId = scopeId;
            return SaveAsync(config, ct);
        }

        public Task SaveGithubUsernameAsync(string scopeId, string githubUsername, CancellationToken ct = default)
        {
            SavedScopeId = scopeId;
            SavedGithubUsername = githubUsername;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal in-memory <see cref="ILogger{T}"/> that records each log call so tests can assert
    /// on level + formatted message. Avoids a full Microsoft.Extensions.Logging.Testing dependency
    /// for a single observability assertion.
    /// </summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }

        public sealed record LogEntry(LogLevel Level, string Message);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
