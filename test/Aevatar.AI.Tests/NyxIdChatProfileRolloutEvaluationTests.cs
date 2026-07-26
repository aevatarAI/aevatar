using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatProfileRolloutEvaluationTests
{
    private static readonly RolloutCase[] RolloutCases =
        (from activationMode in new[]
            {
                AgentProfileActivationMode.Shadow,
                AgentProfileActivationMode.Enforced,
            }
         from selectionMode in Enum.GetValues<SelectionMode>()
         from toolSurface in Enum.GetValues<ToolSurface>()
         from routeState in Enum.GetValues<RouteState>()
         from replayMode in Enum.GetValues<ReplayMode>()
         select new RolloutCase(
             activationMode,
             selectionMode,
             toolSurface,
             routeState,
             replayMode))
        .ToArray();

    public static IEnumerable<object[]> Cases =>
        RolloutCases.Select(static testCase => new object[] { testCase });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ImmutableBindingMatrix_ShouldPreserveModeAndSelectionSemantics(RolloutCase testCase)
    {
        var binding = BuildBinding(testCase.ActivationMode, testCase.SelectionMode == SelectionMode.Alias);
        var classifier = new StaticClassifier(testCase.SelectionMode switch
        {
            SelectionMode.Classifier => AgentProfileTurnClassificationResult.Matched("intent-alpha"),
            SelectionMode.ClassifierFailure => AgentProfileTurnClassificationResult.Failed("classifier_failed"),
            _ => AgentProfileTurnClassificationResult.NoMatch(),
        });
        var tools = NewTools("recovery", "task", "hidden");
        var routeTools = testCase.RouteState == RouteState.TaskCollision
            ? new IAgentTool[]
            {
                tools[0],
                new TestTool("task"),
                new TestTool("task"),
                tools[2],
            }
            : tools;
        var toolContext = testCase.ToolSurface == ToolSurface.Full
            ? AgentToolExecutionContext.Empty
            : AgentToolExecutionContext.Empty with
            {
                ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(["recovery"]),
            };
        var materializer = new AgentProfileTurnCatalogMaterializer(
            new StaticRegistry(routeTools),
            classifier);
        var message = testCase.SelectionMode == SelectionMode.Alias ? "/alpha run" : "classify this";

        var preparation = await materializer.PrepareAsync(
            binding,
            $"session-{testCase}",
            message,
            tools,
            toolContext);
        var committedBinding = binding;
        var committedAuthority = preparation.Authority;
        if (testCase.ReplayMode == ReplayMode.Serialized)
        {
            committedBinding = AgentProfileExecutionBinding.Parser.ParseFrom(binding.ToByteArray());
            committedAuthority = AgentProfileTurnAuthorityState.Parser.ParseFrom(committedAuthority.ToByteArray());
            AgentProfileExecutionBindingCodec.Verify(committedBinding).Should().BeTrue();
        }
        var materialization = await materializer.MaterializeCommittedAsync(
            committedBinding,
            committedAuthority,
            tools,
            toolContext);

        var routeRejected = testCase.RouteState == RouteState.TaskCollision;
        var routeMatched = testCase.SelectionMode is SelectionMode.Alias or SelectionMode.Classifier;
        var expectsSelectedBody = !routeRejected && routeMatched &&
                                  testCase.ActivationMode == AgentProfileActivationMode.Enforced;
        var expectedAuthority = routeRejected
            ? AgentProfileTurnAuthorityKind.RestrictedEmpty
            : expectsSelectedBody
                ? AgentProfileTurnAuthorityKind.Selected
                : AgentProfileTurnAuthorityKind.Recovery;
        var expectedTools = routeRejected
            ? Array.Empty<string>()
            : expectsSelectedBody && testCase.ToolSurface == ToolSurface.Full
                ? ["recovery", "task"]
                : ["recovery"];

        preparation.Authority.AuthorityKind.Should().Be(expectedAuthority);
        materialization.ReconcileProposal.AuthorityKind.Should().Be(expectedAuthority);
        (materialization.Catalog.SelectedSkillPromptLayer is not null)
            .Should().Be(expectsSelectedBody);
        materialization.Catalog.ProfilePromptLayer!.Content.Should().Contain("Published profile instructions.");
        materialization.Catalog.FinalAllowedToolNames.Should().BeEquivalentTo(expectedTools);
        classifier.CallCount.Should().Be(routeRejected || testCase.SelectionMode == SelectionMode.Alias ? 0 : 1);
        if (expectsSelectedBody)
        {
            materialization.Catalog.SelectedSkillPromptLayer!.Content
                .Should().Be("Published sealed instructions.");
        }
        if (routeRejected)
        {
            preparation.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolNameCollision);
        }
    }

    [Fact]
    public void Matrix_ShouldHaveExactly64DistinctAuthorityCases()
    {
        RolloutCases.Should().HaveCount(64);
        RolloutCases.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void SerializedBinding_ShouldRetainAuthorityProvenanceAndDigest()
    {
        var binding = BuildBinding(AgentProfileActivationMode.Enforced, withAlias: true);

        var restored = AgentProfileExecutionBinding.Parser.ParseFrom(binding.ToByteArray());

        AgentProfileExecutionBindingCodec.Verify(restored).Should().BeTrue();
        AgentProfileExecutionBindingCodec.ByteEquivalent(restored, binding).Should().BeTrue();
        restored.Source.ProfileId.Should().Be("profile-alpha");
        restored.Source.StateVersion.Should().Be(17);
        restored.Source.PublishedRevision.Should().Be(5);
        restored.Admission.RolloutRelease.Should().Be("nyxid-chat-r7");
        restored.Members[0].InstructionBody.Should().Be("Published sealed instructions.");
    }

    [Fact]
    public async Task TamperedSerializedBinding_ShouldFailClosedBeforeRegistryRead()
    {
        var binding = BuildBinding(AgentProfileActivationMode.Enforced, withAlias: true);
        binding.Members[0].InstructionBody = "tampered";
        var registry = new StaticRegistry(NewTools("recovery", "task"));

        var preparation = await new AgentProfileTurnCatalogMaterializer(
                registry,
                new StaticClassifier(AgentProfileTurnClassificationResult.NoMatch()))
            .PrepareAsync(
                binding,
                "session-tampered",
                "/alpha run",
                [],
                AgentToolExecutionContext.Empty);

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.RestrictedEmpty);
        registry.ResolveCount.Should().Be(0);
    }

    private static AgentProfileExecutionBinding BuildBinding(
        AgentProfileActivationMode activationMode,
        bool withAlias)
    {
        var binding = AgentProfileExecutionBindingCodecTests.BuildExecutionBinding(
            activationMode,
            instructionBody: "Published sealed instructions.");
        binding.ProfileInstructions = "Published profile instructions.";
        binding.Admission.RouteToolSetRef = "profile.route";
        binding.EffectiveMaximumToolPolicy.ToolNames.Clear();
        binding.EffectiveMaximumToolPolicy.ToolNames.Add(["recovery", "task"]);
        binding.EffectiveRecoveryToolPolicy.ToolNames.Clear();
        binding.EffectiveRecoveryToolPolicy.ToolNames.Add("recovery");
        binding.Members[0].TaskToolPolicy.ToolNames.Clear();
        binding.Members[0].TaskToolPolicy.ToolNames.Add("task");
        binding.Members[0].ExplicitTriggerAliases.Clear();
        if (withAlias)
            binding.Members[0].ExplicitTriggerAliases.Add("/alpha");
        return AgentProfileExecutionBindingCodec.Seal(binding);
    }

    private static IReadOnlyList<IAgentTool> NewTools(params string[] names) =>
        names.Select(static name => (IAgentTool)new TestTool(name)).ToArray();

    public sealed record RolloutCase(
        AgentProfileActivationMode ActivationMode,
        SelectionMode SelectionMode,
        ToolSurface ToolSurface,
        RouteState RouteState,
        ReplayMode ReplayMode);

    public enum SelectionMode
    {
        Alias = 0,
        Classifier = 1,
        NoMatch = 2,
        ClassifierFailure = 3,
    }

    public enum ToolSurface
    {
        Full = 0,
        RecoveryOnly = 1,
    }

    public enum RouteState
    {
        Clean = 0,
        TaskCollision = 1,
    }

    public enum ReplayMode
    {
        Direct = 0,
        Serialized = 1,
    }

    private sealed class StaticClassifier(AgentProfileTurnClassificationResult result)
        : IAgentProfileTurnClassifier
    {
        public int CallCount { get; private set; }

        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class StaticRegistry(IReadOnlyList<IAgentTool> tools) : IToolSetRegistry
    {
        public int ResolveCount { get; private set; }

        public IReadOnlyList<string> GetRegisteredNames() => ["profile.route"];

        public ToolSetResolveResult Resolve(ChatRouteToolSetRef? toolSetRef)
        {
            ResolveCount++;
            return ToolSetResolveResult.Success("profile.route", [new StaticToolSource(tools)]);
        }
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class TestTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }
}
