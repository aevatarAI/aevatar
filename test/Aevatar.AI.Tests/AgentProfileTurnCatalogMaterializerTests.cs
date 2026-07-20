using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class AgentProfileTurnCatalogMaterializerTests
{
    private const string SkillGuid = "11111111-1111-1111-1111-111111111111";
    private const string SkillVersion = "1.2";
    private const string SkillName = "skill-alpha";
    private const string PublisherId = "publisher-alpha";
    private const string SkillMarkdown = "---\nname: skill-alpha\n---\nSelected instructions.";

    [Fact]
    public async Task MaterializeAsync_EnforcedAlias_ShouldSelectBodyAndAttenuatedPolicy()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = RegistryWithRoute(tools);
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(SuccessfulFetch());
        var profile = SealProfile(BuildProfile(withAlias: true));

        var catalog = await NewMaterializer(registry, classifier, fetcher)
            .MaterializeAsync(profile, "/alpha now", "token", tools, ToolContext(), CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery", "task");
        catalog.SelectedIntentId.Should().Be("intent-alpha");
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedSkillPromptLayer!.Content.Should().Be("Selected instructions.");
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.AliasMatched);
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_ClassifierMatch_ShouldSelectExactMember()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha"));
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(RegistryWithRoute(tools), classifier, fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "classify me",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.SelectedIntentId.Should().Be("intent-alpha");
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierMatched);
        classifier.CallCount.Should().Be(1);
        classifier.LastRequest!.Candidates.Should().ContainSingle(candidate =>
            candidate.IntentId == "intent-alpha" && candidate.RoutingDescription == "Route alpha requests.");
    }

    [Fact]
    public async Task MaterializeAsync_ClassifierNoMatch_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "no route",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierNoMatch);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_ClassifierException_ShouldFailClosedToRecovery()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new RecordingClassifier(new InvalidOperationException("classifier failed"));

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                classifier,
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "classify",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierFailed &&
            diagnostic.Detail == "classifier_exception");
    }

    [Fact]
    public async Task MaterializeAsync_Shadow_ShouldKeepCandidateDiagnosticWithoutFetchingOrResolvingTaskPolicy()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = RegistryWithRoute(tools);
        var profile = BuildProfile(withAlias: true);
        profile.ActivationMode = AgentProfileActivationMode.Shadow;
        profile.Members[0].TaskToolPolicy.ToolSetRefs.Add("candidate-only");
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ShadowCandidate);
        fetcher.CallCount.Should().Be(0);
        registry.ResolveCalls.Should().NotContain("candidate-only");
    }

    [Fact]
    public async Task MaterializeAsync_ExactFetchIdentityOrBodyFailure_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");
        var failures = new[]
        {
            ExactRemoteSkillFetchResult.Failed(ExactRemoteSkillFetchFailureCode.NotFound),
            ExactRemoteSkillFetchResult.Success(
                SkillGuid, SkillVersion, "wrong-name", PublisherId, "hash", SkillMarkdown),
            ExactRemoteSkillFetchResult.Success(
                SkillGuid, SkillVersion, SkillName, PublisherId, "hash", new string('x', 300)),
        };

        foreach (var failure in failures)
        {
            var catalog = await NewMaterializer(
                    RegistryWithRoute(tools),
                    new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                    new RecordingFetcher(failure))
                .MaterializeAsync(
                    SealProfile(BuildProfile()),
                    "select",
                    "token",
                    tools,
                    ToolContext(),
                    CancellationToken.None);

            catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
            catalog.SelectedSkillPromptLayer.Should().BeNull();
            catalog.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed ||
                diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillIdentityMismatch ||
                diagnostic.Code == AgentProfileTurnDiagnosticCode.SelectedSkillBodyInvalid);
        }
    }

    [Fact]
    public async Task MaterializeAsync_ExactFetcherUnavailable_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher: null)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed &&
            diagnostic.Detail == "exact_fetch_unavailable");
    }

    [Fact]
    public async Task MaterializeAsync_ExactFetchTimeout_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = BuildProfile(withAlias: true);
        profile.ExactSkillFetchTimeoutMs = 20;

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new CancellationBlockingFetcher())
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed &&
            diagnostic.Detail == "timeout");
    }

    [Fact]
    public async Task MaterializeAsync_ExactFetcherException_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new ThrowingFetcher(new InvalidOperationException("fetch failed")))
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed &&
            diagnostic.Detail == "fetch_exception");
    }

    [Fact]
    public async Task MaterializeAsync_CallerCancellationDuringExactFetch_ShouldPropagate()
    {
        var tools = NewTools("recovery", "task", "extra");
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        var act = async () => await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new CancellationBlockingFetcher())
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MaterializeAsync_InvalidSnapshot_ShouldReturnRestrictedEmpty()
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = SealProfile(BuildProfile());
        profile.PolicyRevision = "tampered";

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(profile, "select", "token", tools, ToolContext(), CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.ToolVisibility.IsRestricted.Should().BeTrue();
        catalog.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ProfileInvalid);
    }

    [Fact]
    public async Task MaterializeAsync_UnknownRouteToolSet_ShouldReturnRestrictedEmpty()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = new RecordingToolSetRegistry();

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "no route",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable);
    }

    [Fact]
    public async Task MaterializeAsync_RouteDiscoveryFailure_ShouldReturnRestrictedEmpty()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = new RecordingToolSetRegistry();
        registry.Add("profile.route", new ThrowingToolSource());
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "no route",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolDiscoveryFailed);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_CollisionAndCapabilityRejection_ShouldNotGrantThoseNames()
    {
        var duplicateA = new TestTool("recovery");
        var duplicateB = new TestTool("recovery");
        var blocked = new CapabilityTool(
            "task",
            [AgentToolCapabilities.ExcludeFromDirectChannelChat]);
        var routeTools = new IAgentTool[] { duplicateA, duplicateB, blocked };
        var registry = RegistryWithRoute(routeTools);
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "no route",
                "token",
                routeTools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolNameCollision &&
            diagnostic.Detail == "recovery");
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolCapabilityRejected &&
            diagnostic.Detail == "task");
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_TaskToolSetFailure_ShouldDiscardSelectionAndUseRecovery()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = RegistryWithRoute(tools);
        var profile = BuildProfile(withAlias: true);
        profile.Members[0].TaskToolPolicy.ToolSetRefs.Add("missing.task");

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolSetUnavailable);
    }

    private static AgentProfileTurnCatalogMaterializer NewMaterializer(
        IToolSetRegistry registry,
        IAgentProfileTurnClassifier classifier,
        IExactRemoteSkillFetcher? fetcher) =>
        new(registry, classifier, fetcher);

    private static AgentProfileSnapshot BuildProfile(bool withAlias = false)
    {
        var member = new AgentProfileSkillMember
        {
            IntentId = "intent-alpha",
            RoutingDescription = "Route alpha requests.",
            SkillRef = new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = SkillVersion },
            TaskToolPolicy = new AgentProfileToolPolicy(),
            SideEffectClass = AgentProfileSideEffectClass.ReadOnly,
            ExpectedSkillName = SkillName,
            ReviewedPublisherId = PublisherId,
        };
        member.TaskToolPolicy.ToolNames.Add("task");
        if (withAlias)
            member.ExplicitTriggerAliases.Add("/alpha");

        var profile = new AgentProfileSnapshot
        {
            ProfileId = "profile-alpha",
            ProfileVersion = "profile-v1",
            AgentKind = "nyxid.chat",
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy(),
            RecoveryToolPolicy = new AgentProfileToolPolicy(),
            ClassifierTimeoutMs = 600,
            ExactSkillFetchTimeoutMs = 1_500,
            MaxSelectedSkillBytes = 256,
            ActivationMode = AgentProfileActivationMode.Enforced,
        };
        profile.MaximumToolPolicy.ToolNames.Add(["recovery", "task", "extra"]);
        profile.RecoveryToolPolicy.ToolNames.Add("recovery");
        profile.Members.Add(member);
        return profile;
    }

    private static AgentProfileSnapshot SealProfile(AgentProfileSnapshot profile) =>
        AgentProfileSnapshotCodec.Seal(profile);

    private static ExactRemoteSkillFetchResult SuccessfulFetch() =>
        ExactRemoteSkillFetchResult.Success(
            SkillGuid,
            SkillVersion,
            SkillName,
            PublisherId,
            "hash-alpha",
            SkillMarkdown);

    private static AgentToolExecutionContext ToolContext() =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials("token", null, null),
        };

    private static IReadOnlyList<IAgentTool> NewTools(params string[] names) =>
        names.Select(static name => (IAgentTool)new TestTool(name)).ToArray();

    private static RecordingToolSetRegistry RegistryWithRoute(IReadOnlyList<IAgentTool> tools)
    {
        var registry = new RecordingToolSetRegistry();
        registry.Add("profile.route", new StaticToolSource(tools));
        return registry;
    }

    private sealed class RecordingClassifier : IAgentProfileTurnClassifier
    {
        private readonly AgentProfileTurnClassificationResult? _result;
        private readonly Exception? _exception;

        public RecordingClassifier(AgentProfileTurnClassificationResult result) => _result = result;
        public RecordingClassifier(Exception exception) => _exception = exception;

        public int CallCount { get; private set; }
        public AgentProfileTurnClassificationRequest? LastRequest { get; private set; }

        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default)
        {
            CallCount++;
            LastRequest = request;
            return _exception is not null
                ? Task.FromException<AgentProfileTurnClassificationResult>(_exception)
                : Task.FromResult(_result!);
        }
    }

    private sealed class RecordingFetcher(ExactRemoteSkillFetchResult result) : IExactRemoteSkillFetcher
    {
        public int CallCount { get; private set; }

        public Task<ExactRemoteSkillFetchResult> FetchAsync(
            string accessToken,
            ExactRemoteSkillRef skillRef,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class CancellationBlockingFetcher : IExactRemoteSkillFetcher
    {
        public async Task<ExactRemoteSkillFetchResult> FetchAsync(
            string accessToken,
            ExactRemoteSkillRef skillRef,
            CancellationToken ct = default)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                canceled);
            await canceled.Task;
            return SuccessfulFetch();
        }
    }

    private sealed class ThrowingFetcher(Exception exception) : IExactRemoteSkillFetcher
    {
        public Task<ExactRemoteSkillFetchResult> FetchAsync(
            string accessToken,
            ExactRemoteSkillRef skillRef,
            CancellationToken ct = default) =>
            Task.FromException<ExactRemoteSkillFetchResult>(exception);
    }

    private sealed class RecordingToolSetRegistry : IToolSetRegistry
    {
        private readonly Dictionary<string, IReadOnlyList<IAgentToolSource>> _sources =
            new(StringComparer.Ordinal);

        public List<string> ResolveCalls { get; } = [];

        public void Add(string name, params IAgentToolSource[] sources) =>
            _sources.Add(name, sources);

        public IReadOnlyList<string> GetRegisteredNames() => _sources.Keys.ToArray();

        public ToolSetResolveResult Resolve(ChatRouteToolSetRef? toolSetRef)
        {
            var name = toolSetRef?.Name ?? string.Empty;
            ResolveCalls.Add(name);
            return _sources.TryGetValue(name, out var sources)
                ? ToolSetResolveResult.Success(name, sources)
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    name,
                    "missing",
                    GetRegisteredNames()));
        }
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class ThrowingToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<IAgentTool>>(new InvalidOperationException("discovery failed"));
    }

    private class TestTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private sealed class CapabilityTool(string name, IReadOnlyCollection<string> capabilities)
        : TestTool(name), IAgentToolCapabilityDescriptor
    {
        public IReadOnlyCollection<string> Capabilities { get; } = capabilities;
    }
}
