using System.Text;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.AgentProfiles;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class AgentProfileSkillSealerTests
{
    private const string SkillGuid = "2d05bf2e-88ee-4f76-9998-728ba2f9db10";

    [Fact]
    public async Task ResolveAndSealAsync_ShouldResolveInBindingOrderAndPreserveExactActivationFacts()
    {
        var sourcePackages = new Dictionary<string, ResolvedOrnnSkillPackage>(StringComparer.Ordinal);
        var resolver = new RecordingResolver(reference =>
        {
            var package = PackageFor(reference);
            package.Description = $"Descriptor for {reference.ExpectedName}";
            package.WhenToUse = $"Route when {reference.ExpectedName} applies";
            package.Assets.Add(
            [
                new AgentProfileNamedTextAsset { Path = "zeta.txt", Content = "Zeta\r\nline" },
                new AgentProfileNamedTextAsset { Path = "alpha.txt", Content = "Alpha line" },
            ]);
            package.DeclaredToolNames.Add(["tool-zeta", "tool-alpha"]);
            sourcePackages.Add(reference.ExpectedName, package);
            return ExactOrnnSkillResolutionResult.Success(package);
        });
        var content = Content();
        content.SkillBindings.Add(
        [
            Binding("bind-zeta", AgentProfileSkillActivationMode.Routed, ExactReference(3, "skill-zeta")),
            Binding("bind-alpha", AgentProfileSkillActivationMode.Always, ExactReference(1, "skill-alpha")),
            Binding("bind-middle", AgentProfileSkillActivationMode.DefaultForUnmatchedTurn, ExactReference(2, "skill-middle")),
        ]);
        var sealer = new AgentProfileSkillSealer(resolver, new StaticToolSetRegistry([]));

        var result = await sealer.ResolveAndSealAsync(Identity(), content, "access-token");

        result.IsSuccess.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
        resolver.BindingNames.Should().Equal("skill-alpha", "skill-middle", "skill-zeta");
        result.Snapshot.Should().NotBeNull();
        var snapshot = result.Snapshot!;
        snapshot.PublishedRevision.Should().Be(0);
        snapshot.SourceDraftSha256.Should().Equal(AgentProfileDeterminism.ComputeSourceDraftSha256(content));
        snapshot.SnapshotSha256.Should().Equal(AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot));
        snapshot.SkillBindings.Select(static binding => binding.BindingId).Should().Equal(
            "bind-alpha",
            "bind-middle",
            "bind-zeta");
        snapshot.SkillBindings.Select(static binding => binding.ActivationMode).Should().Equal(
            AgentProfileSkillActivationMode.Always,
            AgentProfileSkillActivationMode.DefaultForUnmatchedTurn,
            AgentProfileSkillActivationMode.Routed);

        foreach (var sealedBinding in snapshot.SkillBindings)
        {
            sealedBinding.Skill.ExactReference.Should().BeEquivalentTo(
                content.SkillBindings.Single(binding => binding.BindingId == sealedBinding.BindingId).Skill);
            sealedBinding.Skill.ContentSha256.Should().Equal(
                AgentProfileDeterminism.ComputeSealedSkillSha256(sealedBinding.Skill));
        }

        var routed = snapshot.SkillBindings.Single(binding =>
            binding.ActivationMode == AgentProfileSkillActivationMode.Routed);
        routed.Skill.Package.Description.Should().Be("Descriptor for skill-zeta");
        routed.Skill.Package.WhenToUse.Should().Be("Route when skill-zeta applies");
        routed.Skill.Package.ModelInvocable.Should().BeTrue();
        routed.Skill.Package.UserInvocable.Should().BeTrue();

        sourcePackages.Values.Should().OnlyContain(package =>
            package.Assets.Select(static asset => asset.Path).SequenceEqual(new[] { "zeta.txt", "alpha.txt" }) &&
            package.DeclaredToolNames.SequenceEqual(new[] { "tool-zeta", "tool-alpha" }) &&
            package.Assets[0].Content.Contains("\r\n", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AgentProfileSkillActivationMode.Always)]
    [InlineData(AgentProfileSkillActivationMode.Routed)]
    [InlineData(AgentProfileSkillActivationMode.DefaultForUnmatchedTurn)]
    public async Task ResolveAndSealAsync_ShouldAcceptEachSpecifiedActivationMode(
        AgentProfileSkillActivationMode activationMode)
    {
        var resolver = SuccessResolver();
        var content = Content();
        content.SkillBindings.Add(Binding("bind-alpha", activationMode, ExactReference()));

        var result = await CreateSealer(resolver).ResolveAndSealAsync(
            Identity(),
            content,
            "token");

        result.IsSuccess.Should().BeTrue();
        result.Snapshot!.SkillBindings.Should().ContainSingle()
            .Which.ActivationMode.Should().Be(activationMode);
        resolver.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldRejectUnspecifiedActivationBeforeResolution()
    {
        var resolver = SuccessResolver();
        var content = Content();
        content.SkillBindings.Add(Binding(
            "bind-alpha",
            AgentProfileSkillActivationMode.Unspecified,
            ExactReference()));

        var result = await CreateSealer(resolver).ResolveAndSealAsync(Identity(), content, "token");

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "INVALID_SKILL_ACTIVATION_MODE");
        resolver.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldRejectUnknownActivationModeBeforeResolution()
    {
        var resolver = SuccessResolver();
        var content = Content();
        content.SkillBindings.Add(Binding(
            "bind-alpha",
            (AgentProfileSkillActivationMode)999,
            ExactReference()));

        var result = await CreateSealer(resolver).ResolveAndSealAsync(Identity(), content, "token");

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "INVALID_SKILL_ACTIVATION_MODE" &&
            diagnostic.Path == "skill_bindings[0].activation_mode");
        resolver.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldRejectUnknownToolPolicyModeBeforeResolution()
    {
        var resolver = SuccessResolver();
        var content = Content((AgentProfileToolPolicyMode)999);
        content.SkillBindings.Add(Binding(
            "bind-alpha",
            AgentProfileSkillActivationMode.Routed,
            ExactReference()));

        var result = await CreateSealer(resolver).ResolveAndSealAsync(Identity(), content, "token");

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "INVALID_TOOL_POLICY_MODE" &&
            diagnostic.Path == "tool_policy.mode");
        resolver.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldRejectMultipleDefaultBindingsBeforeResolution()
    {
        var resolver = SuccessResolver();
        var content = Content();
        content.SkillBindings.Add(
        [
            Binding("bind-alpha", AgentProfileSkillActivationMode.DefaultForUnmatchedTurn, ExactReference(1, "skill-alpha")),
            Binding("bind-beta", AgentProfileSkillActivationMode.DefaultForUnmatchedTurn, ExactReference(2, "skill-beta")),
        ]);

        var result = await CreateSealer(resolver).ResolveAndSealAsync(Identity(), content, "token");

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == "MULTIPLE_DEFAULT_SKILLS");
        resolver.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAndSealAsync_ExplicitAllowlist_ShouldRequireEveryDeclaredDependency()
    {
        var resolver = new RecordingResolver(reference =>
        {
            var package = PackageFor(reference);
            package.DeclaredToolNames.Add(["tool-alpha", "tool-beta"]);
            return ExactOrnnSkillResolutionResult.Success(package);
        });
        var content = Content(AgentProfileToolPolicyMode.ExplicitAllowlist);
        content.ToolPolicy.ToolNames.Add("tool-alpha");
        content.ToolPolicy.ToolSetRefs.Add("workspace.default");
        content.SkillBindings.Add(Binding("bind-alpha", AgentProfileSkillActivationMode.Routed, ExactReference()));

        var result = await new AgentProfileSkillSealer(
                resolver,
                new StaticToolSetRegistry(["workspace.default"]))
            .ResolveAndSealAsync(Identity(), content, "token");

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "SKILL_TOOL_DEPENDENCY_NOT_ALLOWED" &&
            diagnostic.Path.Contains("tool-beta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveAndSealAsync_InheritRouteMaximum_ShouldRecordDependenciesWithoutClaimingAvailability()
    {
        var resolver = new RecordingResolver(reference =>
        {
            var package = PackageFor(reference);
            package.DeclaredToolNames.Add("route-only-tool");
            return ExactOrnnSkillResolutionResult.Success(package);
        });
        var content = Content(AgentProfileToolPolicyMode.InheritRouteMaximum);
        content.SkillBindings.Add(Binding("bind-alpha", AgentProfileSkillActivationMode.Routed, ExactReference()));

        var result = await CreateSealer(resolver).ResolveAndSealAsync(Identity(), content, "token");

        result.IsSuccess.Should().BeTrue();
        result.Snapshot!.ToolPolicy.Mode.Should().Be(AgentProfileToolPolicyMode.InheritRouteMaximum);
        result.Snapshot.SkillBindings.Single().Skill.Package.DeclaredToolNames
            .Should().Equal("route-only-tool");
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public async Task ResolveAndSealAsync_InheritRouteMaximum_ShouldEnforceDeclaredToolNameUtf8Limit(
        int characterCount,
        bool expectedSuccess)
    {
        var declaredToolName = new string('\u00e9', characterCount);
        var resolver = new RecordingResolver(reference =>
        {
            var package = PackageFor(reference);
            package.DeclaredToolNames.Add(declaredToolName);
            return ExactOrnnSkillResolutionResult.Success(package);
        });
        var content = Content(AgentProfileToolPolicyMode.InheritRouteMaximum);
        content.SkillBindings.Add(Binding("bind-alpha", AgentProfileSkillActivationMode.Routed, ExactReference()));

        var result = await CreateSealer(resolver).ResolveAndSealAsync(Identity(), content, "token");

        result.IsSuccess.Should().Be(expectedSuccess);
        resolver.Calls.Should().Be(1);
        if (expectedSuccess)
        {
            result.Snapshot!.SkillBindings.Single().Skill.Package.DeclaredToolNames
                .Should().Equal(declaredToolName);
        }
        else
        {
            result.Diagnostics.Should().ContainSingle(diagnostic =>
                diagnostic.Code == "INVALID_DECLARED_TOOL_NAME" &&
                diagnostic.Path.Contains("declared_tool_names", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldRejectUnknownToolSetReference()
    {
        var resolver = SuccessResolver();
        var content = Content();
        content.ToolPolicy.ToolSetRefs.Add("unknown-set");

        var result = await new AgentProfileSkillSealer(
                resolver,
                new StaticToolSetRegistry(["workspace.default"]))
            .ResolveAndSealAsync(Identity(), content, null);

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "UNKNOWN_TOOL_SET_REF" && diagnostic.Path == "tool_policy.tool_set_refs[0]");
        resolver.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldAllowNullTokenOnlyWhenThereAreNoBindings()
    {
        var resolver = SuccessResolver();
        var sealer = CreateSealer(resolver);

        var emptyResult = await sealer.ResolveAndSealAsync(Identity(), Content(), null);
        var boundContent = Content();
        boundContent.SkillBindings.Add(Binding("bind-alpha", AgentProfileSkillActivationMode.Always, ExactReference()));
        var boundResult = await sealer.ResolveAndSealAsync(Identity(), boundContent, null);

        emptyResult.IsSuccess.Should().BeTrue();
        boundResult.IsSuccess.Should().BeFalse();
        boundResult.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "ORNN_ACCESS_TOKEN_REQUIRED");
        resolver.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldProduceStableHashesAcrossInputOrdering()
    {
        var reversePackageOrder = false;
        var resolver = new RecordingResolver(reference =>
        {
            var package = PackageFor(reference);
            var assets = new[]
            {
                new AgentProfileNamedTextAsset { Path = "alpha.txt", Content = "Alpha\r\nline" },
                new AgentProfileNamedTextAsset { Path = "zeta.txt", Content = "Zeta line" },
            };
            package.Assets.Add(reversePackageOrder ? assets.Reverse() : assets);
            package.DeclaredToolNames.Add(reversePackageOrder ? ["zeta", "alpha"] : ["alpha", "zeta"]);
            return ExactOrnnSkillResolutionResult.Success(package);
        });
        var first = Content();
        first.ToolPolicy.ToolNames.Add(["zeta", "alpha"]);
        first.SkillBindings.Add(
        [
            Binding("bind-zeta", AgentProfileSkillActivationMode.Routed, ExactReference(2, "skill-zeta")),
            Binding("bind-alpha", AgentProfileSkillActivationMode.Always, ExactReference(1, "skill-alpha")),
        ]);
        var second = Content();
        second.ToolPolicy.ToolNames.Add(["alpha", "zeta"]);
        second.SkillBindings.Add(first.SkillBindings.Reverse().Select(static binding => binding.Clone()));
        var sealer = CreateSealer(resolver);

        var firstResult = await sealer.ResolveAndSealAsync(Identity(), first, "token");
        reversePackageOrder = true;
        var secondResult = await sealer.ResolveAndSealAsync(Identity(), second, "token");

        firstResult.IsSuccess.Should().BeTrue();
        secondResult.IsSuccess.Should().BeTrue();
        firstResult.Snapshot!.SnapshotSha256.Should().Equal(secondResult.Snapshot!.SnapshotSha256);
        firstResult.Snapshot.SkillBindings.Select(static binding => binding.Skill.ContentSha256)
            .Should().Equal(secondResult.Snapshot.SkillBindings.Select(static binding => binding.Skill.ContentSha256));
    }

    [Fact]
    public void DefaultLimits_ShouldMatchThePublishedGlobalConstraints()
    {
        AgentProfileValidationLimits.DisplayNameMaxUtf8Bytes.Should().Be(256);
        AgentProfileValidationLimits.PurposeMaxUtf8Bytes.Should().Be(4_096);
        AgentProfileValidationLimits.IdentifierMaxUtf8Bytes.Should().Be(128);
        AgentProfileValidationLimits.ExpectedOrnnNameMaxUtf8Bytes.Should().Be(64);
        AgentProfileValidationLimits.PublisherIdMaxUtf8Bytes.Should().Be(256);
        AgentProfileValidationLimits.SkillBindingMaxCount.Should().Be(32);
        AgentProfileValidationLimits.ExplicitToolNameMaxCount.Should().Be(128);
        AgentProfileValidationLimits.ToolSetRefMaxCount.Should().Be(32);
        AgentProfileValidationLimits.ProfileInstructionsMaxUtf8Bytes.Should().Be(32_768);
        AgentProfileValidationLimits.AggregatePromptMaxUtf8Bytes.Should().Be(65_536);
        AgentProfileValidationLimits.AggregatePromptMaxTokens.Should().Be(65_536);
        AgentProfileValidationLimits.TextAssetMaxUtf8Bytes.Should().Be(262_144);
        AgentProfileValidationLimits.SealedSkillMaxSerializedBytes.Should().Be(1_048_576);
        AgentProfileValidationLimits.PublishedSnapshotMaxSerializedBytes.Should().Be(4_194_304);
        AgentProfileValidationLimits.DiagnosticMaxCount.Should().Be(64);
        AgentProfileValidationLimits.DiagnosticMessageMaxUtf8Bytes.Should().Be(512);
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldEnforceEveryAuthoredContractLimitBeforeResolution()
    {
        var cases = new (string Code, Func<AgentProfileContent> Content)[]
        {
            ("INVALID_DISPLAY_NAME", () => With(content => content.DisplayName = new string('a', 257))),
            ("INVALID_PURPOSE", () => With(content => content.Purpose = new string('a', 4_097))),
            ("INVALID_INSTRUCTIONS", () => With(content => content.Instructions = new string('a', 32_769))),
            ("INVALID_BINDING_ID", () => With(content => content.SkillBindings.Add(
                Binding(new string('a', 129), AgentProfileSkillActivationMode.Always, ExactReference())))),
            ("INVALID_EXPECTED_SKILL_NAME", () => With(content => content.SkillBindings.Add(
                Binding("bind-alpha", AgentProfileSkillActivationMode.Always,
                    ExactReference(name: new string('a', 65)))))),
            ("INVALID_EXPECTED_PUBLISHER_ID", () => With(content => content.SkillBindings.Add(
                Binding("bind-alpha", AgentProfileSkillActivationMode.Always,
                    ExactReference(publisher: new string('a', 257)))))),
            ("TOO_MANY_SKILL_BINDINGS", () => With(content => content.SkillBindings.Add(
                Enumerable.Range(1, 33).Select(index => Binding(
                    $"bind-{index}",
                    AgentProfileSkillActivationMode.Routed,
                    ExactReference(index, $"skill-{index}")))))),
            ("TOO_MANY_TOOL_NAMES", () => With(content => content.ToolPolicy.ToolNames.Add(
                Enumerable.Range(1, 129).Select(index => $"tool-{index}")))),
            ("INVALID_TOOL_NAME", () => With(content => content.ToolPolicy.ToolNames.Add(new string('a', 129)))),
            ("TOO_MANY_TOOL_SET_REFS", () => With(content => content.ToolPolicy.ToolSetRefs.Add(
                Enumerable.Range(1, 33).Select(index => $"set-{index}")))),
            ("INVALID_TOOL_SET_REF", () => With(content => content.ToolPolicy.ToolSetRefs.Add(new string('a', 129)))),
        };

        foreach (var testCase in cases)
        {
            var resolver = SuccessResolver();
            var result = await CreateSealer(resolver)
                .ResolveAndSealAsync(Identity(), testCase.Content(), "token");

            result.IsSuccess.Should().BeFalse(testCase.Code);
            result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == testCase.Code);
            resolver.Calls.Should().Be(0);
        }
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldEnforceAggregatePromptAndTokenLimits()
    {
        var resolver = new RecordingResolver(reference =>
        {
            var package = PackageFor(reference);
            package.Instructions = new string('a', 65_537);
            return ExactOrnnSkillResolutionResult.Success(package);
        });
        var content = Content();
        content.SkillBindings.Add(Binding("bind-alpha", AgentProfileSkillActivationMode.Always, ExactReference()));

        var result = await CreateSealer(resolver).ResolveAndSealAsync(Identity(), content, "token");

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "AGGREGATE_PROMPT_BYTES_EXCEEDED");
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "AGGREGATE_PROMPT_TOKENS_EXCEEDED");
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldEnforcePerTextAssetLimit()
    {
        var resolver = new RecordingResolver(reference =>
        {
            var package = PackageFor(reference);
            package.Assets.Add(new AgentProfileNamedTextAsset
            {
                Path = "large.txt",
                Content = new string('a', 262_145),
            });
            return ExactOrnnSkillResolutionResult.Success(package);
        });
        var content = Content();
        content.SkillBindings.Add(Binding("bind-alpha", AgentProfileSkillActivationMode.Routed, ExactReference()));

        var result = await CreateSealer(resolver).ResolveAndSealAsync(Identity(), content, "token");

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == "TEXT_ASSET_TOO_LARGE");
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldEnforceSerializedSealedSkillLimit()
    {
        var resolver = new RecordingResolver(reference =>
        {
            var package = PackageFor(reference);
            for (var index = 0; index < 4; index++)
            {
                package.Assets.Add(new AgentProfileNamedTextAsset
                {
                    Path = $"asset-{index}.txt",
                    Content = new string((char)('a' + index), 262_144),
                });
            }
            return ExactOrnnSkillResolutionResult.Success(package);
        });
        var content = Content();
        content.SkillBindings.Add(Binding("bind-alpha", AgentProfileSkillActivationMode.Routed, ExactReference()));

        var result = await CreateSealer(resolver).ResolveAndSealAsync(Identity(), content, "token");

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == "SEALED_SKILL_TOO_LARGE");
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldEnforceSerializedSnapshotLimit()
    {
        var resolver = new RecordingResolver(reference =>
        {
            var package = PackageFor(reference);
            package.Assets.Add(new AgentProfileNamedTextAsset
            {
                Path = "asset.txt",
                Content = new string('a', 250_000),
            });
            return ExactOrnnSkillResolutionResult.Success(package);
        });
        var content = Content();
        content.SkillBindings.Add(Enumerable.Range(1, 17).Select(index => Binding(
            $"bind-{index:D2}",
            AgentProfileSkillActivationMode.Routed,
            ExactReference(index, $"skill-{index}"))));

        var result = await CreateSealer(resolver).ResolveAndSealAsync(Identity(), content, "token");

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == "PUBLISHED_SNAPSHOT_TOO_LARGE");
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldBoundDiagnosticCountAndUtf8MessageSize()
    {
        var longFailureResolver = new RecordingResolver(_ =>
            ExactOrnnSkillResolutionResult.Failed("ORNN_DEPENDENCY_UNAVAILABLE", new string('\u00e9', 600)));
        var oneBinding = Content();
        oneBinding.SkillBindings.Add(Binding("bind-alpha", AgentProfileSkillActivationMode.Routed, ExactReference()));
        var longMessageResult = await CreateSealer(longFailureResolver)
            .ResolveAndSealAsync(Identity(), oneBinding, "token");

        Encoding.UTF8.GetByteCount(longMessageResult.Diagnostics.Single().Message)
            .Should().BeLessThanOrEqualTo(512);

        var dependencyResolver = new RecordingResolver(reference =>
        {
            var package = PackageFor(reference);
            package.DeclaredToolNames.Add(["tool-alpha", "tool-beta", "tool-gamma"]);
            return ExactOrnnSkillResolutionResult.Success(package);
        });
        var manyBindings = Content(AgentProfileToolPolicyMode.ExplicitAllowlist);
        manyBindings.SkillBindings.Add(Enumerable.Range(1, 32).Select(index => Binding(
            $"bind-{index:D2}",
            AgentProfileSkillActivationMode.Routed,
            ExactReference(index, $"skill-{index}"))));

        var manyDiagnosticsResult = await CreateSealer(dependencyResolver)
            .ResolveAndSealAsync(Identity(), manyBindings, "token");

        manyDiagnosticsResult.Diagnostics.Should().HaveCount(64);
        manyDiagnosticsResult.Diagnostics.Should().OnlyContain(diagnostic =>
            Encoding.UTF8.GetByteCount(diagnostic.Message) <= 512);
    }

    private static AgentProfileSkillSealer CreateSealer(IExactOrnnSkillResolver resolver) =>
        new(resolver, new StaticToolSetRegistry([]));

    private static RecordingResolver SuccessResolver() =>
        new(reference => ExactOrnnSkillResolutionResult.Success(PackageFor(reference)));

    private static AgentProfileContent Content(
        AgentProfileToolPolicyMode policyMode = AgentProfileToolPolicyMode.InheritRouteMaximum) =>
        new()
        {
            DisplayName = "Profile Alpha",
            Purpose = "Controls exact test behavior",
            Instructions = "Follow the Profile procedure.",
            ToolPolicy = new AgentProfileToolPolicy { Mode = policyMode },
        };

    private static AgentProfileContent With(Action<AgentProfileContent> mutate)
    {
        var content = Content();
        mutate(content);
        return content;
    }

    private static AgentProfileIdentity Identity() => new()
    {
        ProfileId = "prof-alpha",
        Owner = new AgentProfileOwnerIdentity
        {
            User = new AgentProfileUserOwnerIdentity
            {
                IdentityProvider = AgentProfilePolicies.NyxIdIdentityProvider,
                SubjectId = "subject-alpha",
            },
        },
        OwningScopeId = "scope-gamma",
        Reference = new AgentProfileReference
        {
            OwnerHandle = "owner-alpha",
            ProfileSlug = "profile-alpha",
        },
    };

    private static AgentProfileSkillBinding Binding(
        string bindingId,
        AgentProfileSkillActivationMode activationMode,
        ExactOrnnSkillReference reference) =>
        new()
        {
            BindingId = bindingId,
            ActivationMode = activationMode,
            Skill = reference,
        };

    private static ExactOrnnSkillReference ExactReference(
        int identity = 1,
        string name = "skill-alpha",
        string publisher = "publisher-alpha") =>
        new()
        {
            SkillGuid = $"00000000-0000-0000-0000-{identity:D12}",
            LiteralVersion = "1.4",
            ExpectedName = name,
            ExpectedPublisherId = publisher,
        };

    private static ResolvedOrnnSkillPackage PackageFor(ExactOrnnSkillReference reference) =>
        new()
        {
            SkillGuid = reference.SkillGuid,
            LiteralVersion = reference.LiteralVersion,
            CanonicalName = reference.ExpectedName,
            PublisherId = reference.ExpectedPublisherId,
            UpstreamSkillHash = "hash-alpha",
            Description = "Skill descriptor",
            Instructions = "Run the sealed procedure.",
            Arguments = "request",
            WhenToUse = "Use when the request matches",
            ModelInvocable = true,
            UserInvocable = true,
        };

    private sealed class RecordingResolver(
        Func<ExactOrnnSkillReference, ExactOrnnSkillResolutionResult> resolve)
        : IExactOrnnSkillResolver
    {
        public int Calls { get; private set; }
        public List<string> BindingNames { get; } = [];

        public Task<ExactOrnnSkillResolutionResult> ResolveAsync(
            string nyxIdAccessToken,
            ExactOrnnSkillReference reference,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            BindingNames.Add(reference.ExpectedName);
            return Task.FromResult(resolve(reference));
        }
    }

    private sealed class StaticToolSetRegistry(IReadOnlyList<string> registeredNames) : IToolSetRegistry
    {
        private readonly IReadOnlyList<string> _registeredNames =
            registeredNames.Order(StringComparer.Ordinal).ToArray();

        public IReadOnlyList<string> GetRegisteredNames() => _registeredNames;

        public ToolSetResolveResult Resolve(ChatRouteToolSetRef? toolSetRef)
        {
            var name = toolSetRef?.Name ?? string.Empty;
            return _registeredNames.Contains(name, StringComparer.Ordinal)
                ? ToolSetResolveResult.Success(name, Array.Empty<IAgentToolSource>())
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    name,
                    "Unknown tool set.",
                    _registeredNames));
        }
    }
}
