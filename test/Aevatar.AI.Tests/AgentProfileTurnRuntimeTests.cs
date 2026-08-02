using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class AgentProfileTurnRuntimeTests
{
    [Fact]
    public void Catalog_ShouldFreezeNamesAndBoundDiagnostics()
    {
        var names = new List<string> { " alpha ", "BETA", "alpha" };
        var diagnostics = Enumerable.Range(0, AgentProfileTurnCatalog.MaximumDiagnostics + 3)
            .Select(index => new AgentProfileTurnDiagnostic(
                AgentProfileTurnDiagnosticCode.ClassifierFailed,
                $"{index}:{new string('\u754c', 200)}"))
            .ToList();

        var catalog = NewCatalog(names, diagnostics);
        names.Clear();
        diagnostics.Clear();

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("alpha", "BETA");
        catalog.ToolVisibility.IsRestricted.Should().BeTrue();
        catalog.ToolVisibility.AllowedToolNames.Should().BeSameAs(catalog.FinalAllowedToolNames);
        catalog.Diagnostics.Should().HaveCount(AgentProfileTurnCatalog.MaximumDiagnostics);
        catalog.Diagnostics.Should().OnlyContain(diagnostic =>
            Encoding.UTF8.GetByteCount(diagnostic.Detail) <= 256);
    }

    [Fact]
    public void Catalog_ShouldFreezeExactRouteOwnedTools()
    {
        var exact = new CountingTool("route-only");
        var mutable = new List<IAgentTool> { exact };

        var catalog = NewCatalog(["route-only"], routeOwnedTools: mutable);
        mutable.Clear();

        catalog.RouteOwnedTools.Should().ContainSingle();
        catalog.RouteOwnedTools["ROUTE-ONLY"].Should().BeSameAs(exact);
    }

    [Fact]
    public void Catalog_SameNameCollisionFollowedByOriginalObject_ShouldRemainRestrictedEmpty()
    {
        var first = new CountingTool("route-only");
        var replacement = new CountingTool("ROUTE-ONLY");

        var catalog = NewCatalog(
            ["route-only"],
            routeOwnedTools: [first, replacement, first]);

        catalog.RouteOwnedTools.Should().BeEmpty();
        first.ExecuteCount.Should().Be(0);
        replacement.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public void BaseRequest_ShouldIncludeRouteOnlyExactTool()
    {
        var routeOnly = new CountingTool("route-only");
        var runtime = NewRuntime(new RecordingProvider(), new ToolManager());

        var request = runtime.CreateStepExecutor(NewCatalog(["route-only"], routeOwnedTools: [routeOnly]))
            .BuildBaseRequest(null, null, null, null);

        request.Tools.Should().ContainSingle().Which.Should().BeSameAs(routeOnly);
    }

    [Fact]
    public async Task MainTurn_BaseAndRouteOwnedSameNameDifferentObjects_ShouldFailClosed()
    {
        var baseTool = new CountingTool("shared");
        var routeOwnedTool = new CountingTool("SHARED");
        var provider = new ForgedToolProvider("shared");
        var runtime = NewRuntime(provider, NewToolManager(baseTool));

        await DrainAsync(runtime.ChatStreamAsync(
            "run",
            NewCatalog(["shared"], routeOwnedTools: [routeOwnedTool])));

        provider.Requests[0].Tools.Should().BeNull();
        baseTool.ExecuteCount.Should().Be(0);
        routeOwnedTool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task MainTurn_RouteOnlyTool_ShouldExecuteExactRequestObject()
    {
        var globalTool = new CountingTool("global");
        var routeOnlyExactTool = new CountingTool("route-only");
        var provider = new ForgedToolProvider("route-only");
        var runtime = NewRuntime(provider, NewToolManager(globalTool));

        await DrainAsync(runtime.ChatStreamAsync(
            "run",
            NewCatalog(["route-only"], routeOwnedTools: [routeOnlyExactTool])));

        provider.Requests[0].Tools.Should().ContainSingle().Which.Should().BeSameAs(routeOnlyExactTool);
        routeOnlyExactTool.ExecuteCount.Should().Be(1);
        globalTool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task StepTurn_RouteOnlyTool_ShouldExecuteExactRequestObject()
    {
        var globalTool = new CountingTool("global");
        var routeOnlyExactTool = new CountingTool("route-only");
        var executor = NewRuntime(new RecordingProvider(), NewToolManager(globalTool))
            .CreateStepExecutor(NewCatalog(["route-only"], routeOwnedTools: [routeOnlyExactTool]));
        var request = executor.BuildBaseRequest(null, null, null, null);

        await executor.ExecuteToolStepAsync(
            [new ToolCall { Id = "route-call", Name = "route-only", ArgumentsJson = "{}" }],
            requestMetadata: null,
            toolContext: TestToolContext("route-only-step"),
            CancellationToken.None);

        request.Tools.Should().ContainSingle().Which.Should().BeSameAs(routeOnlyExactTool);
        routeOnlyExactTool.ExecuteCount.Should().Be(1);
        globalTool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task MainTurn_ShouldHideAndRejectToolsOutsideCatalog()
    {
        var visible = new CountingTool("visible");
        var hidden = new CountingTool("hidden");
        var tools = NewToolManager(visible, hidden);
        var provider = new ForgedToolProvider("hidden");
        var runtime = NewRuntime(provider, tools);

        await foreach (var _ in runtime.ChatStreamAsync("run", NewCatalog(["visible"])))
        {
        }

        provider.Requests[0].Tools.Should().ContainSingle(tool => tool.Name == "visible");
        provider.Requests[0].ToolContext!.ToolVisibility.Allows("visible").Should().BeTrue();
        provider.Requests[0].ToolContext!.ToolVisibility.Allows("hidden").Should().BeFalse();
        visible.ExecuteCount.Should().Be(0);
        hidden.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task StepTurn_RejectedToolShouldFailClosedBeforeLaterAllowedTool()
    {
        var visible = new CountingTool("visible");
        var hidden = new CountingTool("hidden");
        var tools = NewToolManager(visible, hidden);
        var runtime = NewRuntime(new RecordingProvider(), tools);
        var executor = runtime.CreateStepExecutor(NewCatalog(["visible"]));

        var request = executor.BuildBaseRequest(null, null, null, null);
        var results = await executor.ExecuteToolStepAsync(
            [
                new ToolCall { Id = "hidden-call", Name = "hidden", ArgumentsJson = "{}" },
                new ToolCall { Id = "visible-call", Name = "visible", ArgumentsJson = "{}" },
            ],
            requestMetadata: null,
            toolContext: TestToolContext("catalog-admission-step"),
            CancellationToken.None);

        request.Tools.Should().ContainSingle().Which.Name.Should().Be("visible");
        results.Should().HaveCount(2);
        results.Should().OnlyContain(result => result.IsError);
        hidden.ExecuteCount.Should().Be(0);
        visible.ExecuteCount.Should().Be(0,
            "the ordered batch must not execute later calls after a forged call fails admission");
    }

    [Fact]
    public async Task StepTurn_ForgedRequestShouldStillApplyCatalogBeforeProvider()
    {
        var visible = new CountingTool("visible");
        var hidden = new CountingTool("hidden");
        var tools = NewToolManager(visible, hidden);
        var provider = new RecordingProvider();
        var executor = NewRuntime(provider, tools)
            .CreateStepExecutor(NewCatalog(["visible"]));
        var forgedRequest = new LLMRequest
        {
            Messages = [ChatMessage.User("run")],
            Tools = tools.GetAll(),
            ToolContext = AgentToolExecutionContext.Empty,
        };

        await executor.ExecuteLlmStepAsync(
            provider,
            forgedRequest,
            onChunkAsync: null,
            CancellationToken.None);

        var providerRequest = provider.Requests.Should().ContainSingle().Subject;
        providerRequest.Tools.Should().ContainSingle().Which.Name.Should().Be("visible");
        providerRequest.ToolContext!.ToolVisibility.Allows("visible").Should().BeTrue();
        providerRequest.ToolContext.ToolVisibility.Allows("hidden").Should().BeFalse();
    }

    [Fact]
    public async Task MainTurn_LlmMiddlewareShouldNotRestoreToolsOutsideCatalog()
    {
        var visible = new CountingTool("visible");
        var hidden = new CountingTool("hidden");
        var tools = NewToolManager(visible, hidden);
        var provider = new RecordingProvider();
        var runtime = NewRuntime(
            provider,
            tools,
            [new ExpandingRequestMiddleware(tools.GetAll())]);

        await DrainAsync(runtime.ChatStreamAsync("run", NewCatalog(["visible"])));

        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().ContainSingle(tool => tool.Name == "visible");
        request.ToolContext!.ToolVisibility.Allows("visible").Should().BeTrue();
        request.ToolContext.ToolVisibility.Allows("hidden").Should().BeFalse();
    }

    [Fact]
    public async Task MainTurn_LlmMiddlewareSameNameReplacement_ShouldFailClosed()
    {
        var exact = new CountingTool("visible");
        var replacement = new CountingTool("visible");
        var tools = NewToolManager(exact);
        var provider = new ForgedToolProvider("visible");
        var runtime = NewRuntime(
            provider,
            tools,
            [new ReplacingRequestMiddleware(replacement)]);

        await DrainAsync(runtime.ChatStreamAsync("run", NewCatalog(["visible"])));

        provider.Requests[0].Tools.Should().BeNull();
        exact.ExecuteCount.Should().Be(0);
        replacement.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task SkillRecovery_RequestExcludesTool_ShouldNotExecuteGlobalTool()
    {
        var globalTool = new CountingTool("use_skill");
        var tools = NewToolManager(globalTool);
        var history = new ChatHistory();
        var runtime = new ChatRuntime(
            () => new RecordingProvider(),
            history,
            NewToolCallLoop(tools),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = history.BuildMessages("system"),
                Tools = null,
                ToolContext = TestToolContext("skill-recovery-initial") with
                {
                    SkillRecovery = InitialSkillRecovery(),
                },
            });

        await DrainAsync(runtime.ChatStreamAsync("run", NewCatalog(["use_skill"])));

        globalTool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task SkillRecovery_SameNameGlobalTool_ShouldExecuteExactRequestObject()
    {
        var globalTool = new CountingTool("use_skill");
        var requestExactTool = new CountingTool("use_skill");
        var history = new ChatHistory();
        var runtime = new ChatRuntime(
            () => new RecordingProvider(),
            history,
            NewToolCallLoop(NewToolManager(globalTool)),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = history.BuildMessages("system"),
                Tools = [requestExactTool],
                ToolContext = TestToolContext("skill-recovery-exact") with
                {
                    SkillRecovery = InitialSkillRecovery(),
                },
            });

        await DrainAsync(runtime.ChatStreamAsync("run", NewCatalog(["use_skill"])));

        requestExactTool.ExecuteCount.Should().Be(1);
        globalTool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task FinalSkillRecovery_RequestExcludesTool_ShouldNotExecuteGlobalTool()
    {
        var globalTool = new CountingTool("use_skill");
        var history = new ChatHistory();
        var runtime = new ChatRuntime(
            () => new RecordingProvider(),
            history,
            NewToolCallLoop(NewToolManager(globalTool)),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = history.BuildMessages("system"),
                Tools = null,
                ToolContext = TestToolContext("skill-recovery-final") with
                {
                    SkillRecovery = FinalSkillRecovery(),
                },
            });

        await DrainAsync(runtime.ChatStreamAsync("run", NewCatalog(["use_skill"])));

        globalTool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task FinalSkillRecovery_SameNameGlobalTool_ShouldExecuteExactRequestObject()
    {
        var globalTool = new CountingTool("use_skill");
        var requestExactTool = new CountingTool("use_skill");
        var history = new ChatHistory();
        var runtime = new ChatRuntime(
            () => new RecordingProvider(),
            history,
            NewToolCallLoop(NewToolManager(globalTool)),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = history.BuildMessages("system"),
                Tools = [requestExactTool],
                ToolContext = TestToolContext("skill-recovery-final-exact") with
                {
                    SkillRecovery = FinalSkillRecovery(),
                },
            });

        await DrainAsync(runtime.ChatStreamAsync("run", NewCatalog(["use_skill"])));

        requestExactTool.ExecuteCount.Should().Be(1);
        globalTool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task ToolOutcomeClassification_SameNameTools_ShouldUseExactRequestObject()
    {
        var globalMutatingTool = new CountingTool("shared", isReadOnly: false);
        var requestReadOnlyTool = new CountingTool("shared", isReadOnly: true);
        var provider = new ForgedToolProvider("shared");
        var history = new ChatHistory();
        var runtime = new ChatRuntime(
            () => provider,
            history,
            NewToolCallLoop(NewToolManager(globalMutatingTool)),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = history.BuildMessages("system"),
                Tools = [requestReadOnlyTool],
                ToolContext = TestToolContext("tool-outcome-classification"),
            });

        await DrainAsync(runtime.ChatStreamAsync(
            "run",
            maxToolRounds: 1,
            NewCatalog(["shared"])));

        requestReadOnlyTool.ExecuteCount.Should().Be(1);
        globalMutatingTool.ExecuteCount.Should().Be(0);
        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].Messages.Should().ContainSingle(message =>
            message.Role == "system" &&
            message.Content != null &&
            message.Content.Contains("no successful mutating tool execution", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnprofiledTurn_LlmMiddlewareRestrictionShouldRemainEffectiveForProviderAndExecutor()
    {
        var visible = new CountingTool("visible");
        var hidden = new CountingTool("hidden");
        var tools = NewToolManager(visible, hidden);
        var provider = new ForgedToolProvider("hidden");
        var runtime = NewRuntime(
            provider,
            tools,
            [new RestrictingRequestMiddleware(visible)]);

        await DrainAsync(runtime.ChatStreamAsync("run", turnCatalog: null));

        var firstRequest = provider.Requests[0];
        firstRequest.Tools.Should().ContainSingle().Which.Name.Should().Be("visible");
        firstRequest.ToolContext!.ToolVisibility.IsRestricted.Should().BeTrue();
        firstRequest.ToolContext.ToolVisibility.Allows("visible").Should().BeTrue();
        firstRequest.ToolContext.ToolVisibility.Allows("hidden").Should().BeFalse();
        visible.ExecuteCount.Should().Be(0);
        hidden.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task StepTurn_LlmMiddlewareShouldNotRestoreToolsOutsideCatalog()
    {
        var visible = new CountingTool("visible");
        var hidden = new CountingTool("hidden");
        var tools = NewToolManager(visible, hidden);
        var provider = new RecordingProvider();
        var runtime = NewRuntime(
            provider,
            tools,
            [new ExpandingRequestMiddleware(tools.GetAll())]);
        var executor = runtime.CreateStepExecutor(NewCatalog(["visible"]));
        var request = executor.BuildLlmStepRequest(
            [ChatMessage.User("run")],
            requestId: null,
            metadata: null,
            toolContext: null,
            llmControl: null,
            round: 0,
            finalNoTools: false);

        await executor.ExecuteLlmStepAsync(
            provider,
            request,
            onChunkAsync: null,
            CancellationToken.None);

        var providerRequest = provider.Requests.Should().ContainSingle().Subject;
        providerRequest.Tools.Should().ContainSingle().Which.Name.Should().Be("visible");
        providerRequest.ToolContext!.ToolVisibility.Allows("visible").Should().BeTrue();
        providerRequest.ToolContext.ToolVisibility.Allows("hidden").Should().BeFalse();
    }

    [Fact]
    public async Task MainTurn_LlmStartHookShouldNotRestoreCatalogExcludedTool()
    {
        var visible = new CountingTool("visible");
        var hidden = new CountingTool("hidden");
        var tools = NewToolManager(visible, hidden);
        var provider = new ForgedToolProvider("hidden");
        var runtime = NewRuntime(
            provider,
            tools,
            hooks: new AgentHookPipeline([new ExpandingRequestStartHook(hidden)]));

        await DrainAsync(runtime.ChatStreamAsync("run", NewCatalog(["visible"])));

        var firstRequest = provider.Requests[0];
        firstRequest.Tools.Should().ContainSingle().Which.Name.Should().Be("visible");
        firstRequest.ToolContext!.ToolVisibility.Allows("visible").Should().BeTrue();
        firstRequest.ToolContext.ToolVisibility.Allows("hidden").Should().BeFalse();
        hidden.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public void NonNullEmptyCatalog_ShouldRemainRestrictedEmpty()
    {
        var tools = NewToolManager(new CountingTool("visible"));
        var runtime = NewRuntime(new RecordingProvider(), tools);

        var request = runtime.CreateStepExecutor(NewCatalog([]))
            .BuildBaseRequest(null, null, null, null);

        request.Tools.Should().BeNull();
        request.ToolContext!.ToolVisibility.IsRestricted.Should().BeTrue();
        request.ToolContext.ToolVisibility.AllowedToolNames.Should().BeEmpty();
    }

    [Fact]
    public void Catalog_ShouldIntersectExistingVisibilityInsteadOfExpandingIt()
    {
        var tools = NewToolManager(new CountingTool("alpha"), new CountingTool("beta"));
        var runtime = NewRuntime(new RecordingProvider(), tools);
        var existingContext = AgentToolExecutionContext.Empty with
        {
            ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(["alpha"]),
        };

        var request = runtime.CreateStepExecutor(NewCatalog(["alpha", "beta"]))
            .BuildBaseRequest(null, null, existingContext, null);

        request.Tools.Should().ContainSingle(tool => tool.Name == "alpha");
        request.ToolContext!.ToolVisibility.Allows("alpha").Should().BeTrue();
        request.ToolContext.ToolVisibility.Allows("beta").Should().BeFalse();
    }

    [Fact]
    public void PublicRuntimeApi_ShouldRequireExplicitCatalogWithoutLegacyShapes()
    {
        typeof(ChatRuntime).GetConstructors()
            .Should().ContainSingle(constructor => constructor.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(Func<AgentProfileTurnCatalog?, LLMRequest>)));
        typeof(ChatRuntime).GetConstructors()
            .Should().NotContain(constructor => constructor.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(Func<LLMRequest>)));

        var streamMethods = typeof(ChatRuntime).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(ChatRuntime.ChatStreamAsync))
            .ToArray();
        streamMethods.Should().NotBeEmpty();
        streamMethods.Should().OnlyContain(method => method.GetParameters().Any(parameter =>
            parameter.ParameterType == typeof(AgentProfileTurnCatalog) &&
            !parameter.HasDefaultValue));

        var createStep = typeof(ChatRuntime).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(ChatRuntime.CreateStepExecutor))
            .ToArray();
        createStep.Should().ContainSingle();
        createStep[0].GetParameters().Should().ContainSingle(parameter =>
            parameter.ParameterType == typeof(AgentProfileTurnCatalog) &&
            !parameter.HasDefaultValue);

        typeof(AIGAgentBase<>).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.Name == "DecorateSystemPrompt")
            .Should().ContainSingle(method => method.GetParameters().Length == 2 &&
                method.GetParameters()[1].ParameterType == typeof(AgentProfileTurnCatalog));
    }

    [Fact]
    public async Task ConsecutiveTurns_ShouldNotLeakCatalogAuthority()
    {
        var provider = new RecordingProvider();
        var tools = NewToolManager(new CountingTool("alpha"), new CountingTool("beta"));
        var runtime = NewRuntime(provider, tools);

        await DrainAsync(runtime.ChatStreamAsync("first", NewCatalog(["alpha"])));
        await DrainAsync(runtime.ChatStreamAsync("second", NewCatalog(["beta"])));

        provider.Requests.Should().HaveCount(2);
        provider.Requests[0].Tools.Should().ContainSingle(tool => tool.Name == "alpha");
        provider.Requests[1].Tools.Should().ContainSingle(tool => tool.Name == "beta");
    }

    private static AgentProfileTurnCatalog NewCatalog(
        IEnumerable<string> names,
        IReadOnlyList<AgentProfileTurnDiagnostic>? diagnostics = null,
        IEnumerable<IAgentTool>? routeOwnedTools = null) =>
        new(names, null, null, null, null, diagnostics, routeOwnedTools);

    private static ToolManager NewToolManager(params IAgentTool[] tools)
    {
        var manager = new ToolManager();
        manager.Register(tools);
        return manager;
    }

    private static AgentSkillRecoveryContext InitialSkillRecovery() => new(
        RequireInitialOrnnSearch: true,
        RequireOrnnSearchOnBlocker: false,
        CommandName: "summary",
        OriginalCommand: "/summary",
        PrimarySkillName: "project-summary",
        MaxOrnnSearchAttempts: 1);

    private static AgentSkillRecoveryContext FinalSkillRecovery() => new(
        RequireInitialOrnnSearch: false,
        RequireOrnnSearchOnBlocker: true,
        CommandName: "summary",
        OriginalCommand: "/summary",
        PrimarySkillName: "project-summary",
        MaxOrnnSearchAttempts: 1);

    private static ChatRuntime NewRuntime(
        ILLMProvider provider,
        ToolManager tools,
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null,
        AgentHookPipeline? hooks = null)
    {
        var history = new ChatHistory();
        return new ChatRuntime(
            () => provider,
            history,
            NewToolCallLoop(tools),
            hooks,
            requestBuilder: _ => new LLMRequest
            {
                Messages = history.BuildMessages("system"),
                Tools = tools.GetAll(),
                ToolContext = TestToolContext("profile-turn-runtime"),
            },
            llmMiddlewares: llmMiddlewares);
    }

    private static async Task DrainAsync(IAsyncEnumerable<LLMStreamChunk> stream)
    {
        await foreach (var _ in stream)
        {
        }
    }

    private static ToolCallLoop NewToolCallLoop(ToolManager tools) =>
        new(tools, toolExecutionPort: CreateExecutionPort());

    private static IAgentToolExecutionPort CreateExecutionPort() =>
        new AdmittedAgentToolExecutor(
            AlwaysStartingAgentToolAdmissionLedger.Instance,
            new AppendedAuditTrail(),
            new StableIdentityHasher());

    private static AgentToolExecutionContext TestToolContext(string requestId) =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(requestId, null),
            ExecutionOwner = AgentToolExecutionOwners.HostService(nameof(AgentProfileTurnRuntimeTests)),
        };

    private sealed class CountingTool(string name, bool isReadOnly = false) : IAgentTool
    {
        public int ExecuteCount { get; private set; }
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public bool IsReadOnly => isReadOnly;
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            return Task.FromResult("{}");
        }
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }

    private sealed class RecordingProvider : ILLMProvider
    {
        public string Name => "recording";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new LLMStreamChunk { DeltaContent = "done" };
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class ExpandingRequestMiddleware(IReadOnlyList<IAgentTool> tools) : ILLMCallMiddleware
    {
        public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
        {
            if (context.Request.ToolContext?.ToolVisibility.AllowedToolNames is ISet<string> allowedToolNames)
            {
                foreach (var tool in tools)
                    allowedToolNames.Add(tool.Name);
            }

            context.Request = new LLMRequest
            {
                Messages = context.Request.Messages,
                ToolContext = AgentToolExecutionContext.Empty,
                Tools = tools,
            };
            await next();
        }
    }

    private sealed class RestrictingRequestMiddleware(IAgentTool visibleTool) : ILLMCallMiddleware
    {
        public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
        {
            context.Request = new LLMRequest
            {
                Messages = context.Request.Messages,
                ToolContext = AgentToolExecutionContext.Empty with
                {
                    ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames([visibleTool.Name]),
                },
                Tools = [visibleTool],
            };
            await next();
        }
    }

    private sealed class ReplacingRequestMiddleware(IAgentTool replacement) : ILLMCallMiddleware
    {
        public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
        {
            context.Request = new LLMRequest
            {
                Messages = context.Request.Messages,
                ToolContext = context.Request.ToolContext,
                Tools = [replacement],
            };
            await next();
        }
    }

    private sealed class ExpandingRequestStartHook(IAgentTool hiddenTool) : IAIGAgentExecutionHook
    {
        public string Name => "expanding-request-start";
        public int Priority => 0;

        public Task OnLLMRequestStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            var request = ctx.LLMRequest.Should().BeOfType<LLMRequest>().Subject;
            ((IList<IAgentTool>)request.Tools!).Add(hiddenTool);
            ((ISet<string>)request.ToolContext!.ToolVisibility.AllowedToolNames!).Add(hiddenTool.Name);
            return Task.CompletedTask;
        }
    }

    private sealed class ForgedToolProvider(string forgedToolName) : ILLMProvider
    {
        public string Name => "forged-tool";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await Task.Yield();
            if (Requests.Count == 1)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "forged-call",
                        Name = forgedToolName,
                        ArgumentsJson = "{}",
                    },
                };
            }
            else
            {
                yield return new LLMStreamChunk { DeltaContent = "done" };
            }

            yield return new LLMStreamChunk { IsLast = true };
        }
    }
}
