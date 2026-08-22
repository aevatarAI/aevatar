using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class AgentProfileSkillSealerTests
{
    private static readonly DateTimeOffset PublishedAt =
        DateTimeOffset.Parse("2026-07-30T00:01:00Z");
    private static readonly ByteString SkillSha256 =
        ByteString.CopyFrom(Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());

    [Fact]
    public async Task ResolveAndSealAsync_ShouldUseAuthorityRevisionsAndSealExactHashWithoutMutatingDraft()
    {
        var resolver = new RecordingResolver();
        var sealer = NewSealer(resolver);
        var draft = Draft();
        var original = draft.Clone();

        var result = await sealer.ResolveAndSealAsync(
            Identity(),
            draft,
            Context(draftRevision: 7, nextPublishedRevision: 3));

        result.IsSuccess.Should().BeTrue();
        result.Snapshot!.DraftRevision.Should().Be(7);
        result.Snapshot.PublishedRevision.Should().Be(3);
        result.Snapshot.PublishedAt.ToDateTimeOffset().Should().Be(PublishedAt);
        result.Snapshot.RuntimeProfile.PublishedRevision.Should().Be(3);
        result.Snapshot.RuntimeProfile.Members[0].SkillRef.Should()
            .BeEquivalentTo(draft.RuntimeProfile.Members[0].SkillRef);
        result.Snapshot.RuntimeProfile.Members[0].SealedSkillSha256.Should().Equal(SkillSha256);
        result.Snapshot.RuntimeProfile.DeterministicPolicySha256.Length.Should().Be(32);
        draft.Should().BeEquivalentTo(original);
        resolver.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ResolveAndSealAsync_CandidateShouldBeAcceptedByAuthoritativeActor()
    {
        var identity = Identity();
        var draft = Draft();
        var actor = GAgentServiceTestKit.CreateStatefulAgent<AgentProfileGAgent, AgentProfileState>(
            new InMemoryEventStore(),
            AgentProfileActorIds.Profile(identity.ProfileId),
            static () => new AgentProfileGAgent());
        await InitializeAsync(actor, new InitializeAgentProfileCommand
        {
            Identity = identity.Clone(),
            InitialDraft = draft.Clone(),
            Operation = Operation("op-init", "init"),
        });
        var result = await NewSealer().ResolveAndSealAsync(
            identity,
            draft,
            Context(actor.State.DraftRevision, actor.State.PublishedRevision + 1));

        await actor.HandlePublishAsync(new PublishAgentProfileCommand
        {
            Identity = identity.Clone(),
            Snapshot = result.Snapshot,
            SourceDraftSha256 = actor.State.DraftSha256,
            ExpectedAuthorityStateVersion = 1,
            Operation = Operation("op-publish", "publish"),
        });

        result.IsSuccess.Should().BeTrue();
        actor.State.LastMutation.Code.Should().Be("PROFILE_PUBLISHED");
        actor.State.PublishedRevision.Should().Be(1);
        actor.State.Published.RuntimeProfile.Members[0].SealedSkillSha256.Should().Equal(SkillSha256);
    }

    [Theory]
    [InlineData("guid")]
    [InlineData("version")]
    [InlineData("name")]
    [InlineData("publisher")]
    [InlineData("hash")]
    [InlineData("tools")]
    public async Task ResolveAndSealAsync_ShouldRevalidateAllResolverEvidence(string mismatch)
    {
        var draft = Draft();
        if (mismatch == "hash")
            draft.RuntimeProfile.Members[0].SealedSkillSha256 = ByteString.CopyFrom(new byte[32]);
        var resolver = new RecordingResolver(Package(mismatch));
        var expectedDiagnosticCode = mismatch switch
        {
            "publisher" => "ORNN_SKILL_PUBLISHER_MISMATCH",
            "hash" => "ORNN_SKILL_HASH_MISMATCH",
            "tools" => "ORNN_SKILL_DECLARED_TOOL_NOT_ALLOWED",
            _ => "ORNN_SKILL_IDENTITY_MISMATCH",
        };

        var result = await NewSealer(resolver).ResolveAndSealAsync(Identity(), draft, Context());

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == expectedDiagnosticCode &&
            !string.IsNullOrWhiteSpace(diagnostic.Field) &&
            !string.IsNullOrWhiteSpace(diagnostic.Message));
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldRejectResolverHashThatIsNotSha256()
    {
        var resolver = new RecordingResolver(Package() with
        {
            SkillSha256 = ByteString.CopyFrom(new byte[31]),
        });

        var result = await NewSealer(resolver).ResolveAndSealAsync(Identity(), Draft(), Context());

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "ORNN_SKILL_INTEGRITY_EVIDENCE_MISSING" &&
            diagnostic.Field.EndsWith(".sealedSkillSha256", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AgentProfileValidationLimits.RequiredMaxSelectedSkillBytes, true)]
    [InlineData(AgentProfileValidationLimits.RequiredMaxSelectedSkillBytes + 1, false)]
    public async Task ResolveAndSealAsync_ShouldEnforceSelectedSkillBodyBudget(
        int skillMarkdownUtf8Bytes,
        bool expectedSuccess)
    {
        var resolver = new RecordingResolver(Package() with
        {
            SkillMarkdownUtf8Bytes = skillMarkdownUtf8Bytes,
        });

        var result = await NewSealer(resolver).ResolveAndSealAsync(Identity(), Draft(), Context());

        result.IsSuccess.Should().Be(expectedSuccess);
        if (expectedSuccess)
        {
            result.Diagnostics.Should().BeEmpty();
        }
        else
        {
            result.Diagnostics.Should().ContainSingle(diagnostic =>
                diagnostic.Code == "ORNN_SKILL_BODY_TOO_LARGE" &&
                diagnostic.Field.EndsWith(".skillRef", StringComparison.Ordinal) &&
                diagnostic.Message.Contains(
                    $"{AgentProfileValidationLimits.RequiredMaxSelectedSkillBytes + 1}",
                    StringComparison.Ordinal) &&
                diagnostic.Message.Contains(
                    $"{AgentProfileValidationLimits.RequiredMaxSelectedSkillBytes}",
                    StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("task-name")]
    [InlineData("task-ref")]
    [InlineData("task-selector-risk")]
    [InlineData("recovery-name")]
    [InlineData("recovery-ref")]
    public async Task ResolveAndSealAsync_ShouldRequireTaskAndRecoveryPoliciesWithinMaximum(string violation)
    {
        var draft = Draft();
        switch (violation)
        {
            case "task-name":
                draft.RuntimeProfile.Members[0].TaskToolPolicy.ToolNames.Add("admin");
                break;
            case "task-ref":
                draft.RuntimeProfile.Members[0].TaskToolPolicy.ToolSetRefs.Add("task.extra");
                break;
            case "task-selector-risk":
                draft.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors.Add(
                    Selector("api-github", AgentToolOperationRiskPayload.ReadOnly));
                draft.RuntimeProfile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(
                    Selector("api-github", AgentToolOperationRiskPayload.Write));
                break;
            case "recovery-name":
                draft.RuntimeProfile.RecoveryToolPolicy.ToolNames.Add("admin");
                break;
            case "recovery-ref":
                draft.RuntimeProfile.RecoveryToolPolicy.ToolSetRefs.Add("recovery.extra");
                break;
        }
        var resolver = new RecordingResolver();

        var result = await NewSealer(resolver).ResolveAndSealAsync(Identity(), draft, Context());

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "PROFILE_TOOL_POLICY_EXCEEDS_MAXIMUM");
        resolver.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("invalid-slug", "PROFILE_CONNECTED_SERVICE_SLUG_INVALID")]
    [InlineData("empty-risks", "PROFILE_CONNECTED_SERVICE_RISKS_REQUIRED")]
    [InlineData("destructive", "PROFILE_CONNECTED_SERVICE_RISK_INVALID")]
    [InlineData("duplicate", "PROFILE_CONNECTED_SERVICE_SELECTOR_DUPLICATE")]
    [InlineData("invalid-endpoint", "PROFILE_CONNECTED_SERVICE_ENDPOINT_INVALID")]
    public async Task ResolveAndSealAsync_ShouldRejectInvalidConnectedServiceSelectors(
        string violation,
        string expectedCode)
    {
        var draft = Draft();
        var selector = violation switch
        {
            "invalid-slug" => Selector("API-GitHub", AgentToolOperationRiskPayload.ReadOnly),
            "empty-risks" => Selector("api-github"),
            "destructive" => Selector("api-github", AgentToolOperationRiskPayload.Destructive),
            _ => Selector("api-github", AgentToolOperationRiskPayload.ReadOnly),
        };
        if (violation == "invalid-endpoint")
            selector.EndpointId = " endpoint-read";
        draft.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors.Add(selector);
        if (violation == "duplicate")
        {
            draft.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors.Add(
                Selector("api-github", AgentToolOperationRiskPayload.Write));
        }
        var resolver = new RecordingResolver();

        var result = await NewSealer(resolver).ResolveAndSealAsync(Identity(), draft, Context());

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == expectedCode);
        resolver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAndSealAsync_BroadMaximumSelector_ShouldAllowExactTaskEndpoint()
    {
        var draft = Draft();
        var maximum = Selector("api-github", AgentToolOperationRiskPayload.ReadOnly);
        var task = maximum.Clone();
        task.EndpointId = "endpoint-read";
        draft.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors.Add(maximum);
        draft.RuntimeProfile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(task);

        var result = await NewSealer(new RecordingResolver())
            .ResolveAndSealAsync(Identity(), draft, Context());

        result.IsSuccess.Should().BeTrue();
        result.Snapshot!.RuntimeProfile.Members[0].TaskToolPolicy.ConnectedServiceSelectors[0].EndpointId
            .Should().Be("endpoint-read");
    }

    [Theory]
    [InlineData("")]
    [InlineData("endpoint-write")]
    public async Task ResolveAndSealAsync_ExactMaximumSelector_ShouldRejectBroaderOrDifferentTaskEndpoint(
        string taskEndpointId)
    {
        var draft = Draft();
        var maximum = Selector("api-github", AgentToolOperationRiskPayload.ReadOnly);
        maximum.EndpointId = "endpoint-read";
        var task = maximum.Clone();
        task.EndpointId = taskEndpointId;
        draft.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors.Add(maximum);
        draft.RuntimeProfile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(task);

        var result = await NewSealer(new RecordingResolver())
            .ResolveAndSealAsync(Identity(), draft, Context());

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "PROFILE_TOOL_POLICY_EXCEEDS_MAXIMUM");
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldRejectNonCanonicalReadinessScopes()
    {
        var draft = Draft();
        var selector = Selector("api-github", AgentToolOperationRiskPayload.ReadOnly);
        selector.Readiness = new AgentProfileConnectedServiceReadiness
        {
            RequestedScopes = { "repo", " repo" },
        };
        draft.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors.Add(selector);

        var result = await NewSealer(new RecordingResolver())
            .ResolveAndSealAsync(Identity(), draft, Context());

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == "PROFILE_CONNECTED_SERVICE_READINESS_SCOPES_INVALID");
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldRejectEmptyReadinessScopes()
    {
        var draft = Draft();
        var selector = Selector("api-github", AgentToolOperationRiskPayload.ReadOnly);
        selector.Readiness = new AgentProfileConnectedServiceReadiness();
        draft.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors.Add(selector);

        var result = await NewSealer(new RecordingResolver())
            .ResolveAndSealAsync(Identity(), draft, Context());

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "PROFILE_CONNECTED_SERVICE_READINESS_SCOPES_INVALID" &&
            diagnostic.Field.EndsWith(".readiness.requestedScopes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldKeepTaskReadinessScopesWithinMaximum()
    {
        var draft = Draft();
        var maximum = Selector("api-github", AgentToolOperationRiskPayload.ReadOnly);
        maximum.Readiness = new AgentProfileConnectedServiceReadiness
        {
            RequestedScopes = { "repo" },
        };
        var task = maximum.Clone();
        task.Readiness.RequestedScopes.Add("read:user");
        draft.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors.Add(maximum);
        draft.RuntimeProfile.Members[0].TaskToolPolicy.ConnectedServiceSelectors.Add(task);

        var result = await NewSealer(new RecordingResolver())
            .ResolveAndSealAsync(Identity(), draft, Context());

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "PROFILE_TOOL_POLICY_EXCEEDS_MAXIMUM");
    }

    [Fact]
    public void NormalizeDraft_ShouldCanonicalizeConnectedServiceSelectorsAndRisks()
    {
        var left = Draft();
        left.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors.Add([
            Selector(
                "api-slack",
                AgentToolOperationRiskPayload.Write,
                AgentToolOperationRiskPayload.ReadOnly),
            Selector("api-github", AgentToolOperationRiskPayload.ReadOnly),
        ]);
        left.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors[1].Readiness =
            new AgentProfileConnectedServiceReadiness
            {
                RequestedScopes = { "repo", "read:user" },
            };
        var right = Draft();
        right.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors.Add([
            Selector("api-github", AgentToolOperationRiskPayload.ReadOnly),
            Selector(
                "api-slack",
                AgentToolOperationRiskPayload.ReadOnly,
                AgentToolOperationRiskPayload.Write),
        ]);
        right.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors[0].Readiness =
            new AgentProfileConnectedServiceReadiness
            {
                RequestedScopes = { "read:user", "repo" },
            };

        var normalized = AgentProfileDeterminism.NormalizeDraft(left);

        AgentProfileDeterminism.ComputeDraftDigest(left)
            .Should().Equal(AgentProfileDeterminism.ComputeDraftDigest(right));
        normalized.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors
            .Select(static selector => selector.CatalogServiceSlug)
            .Should().Equal("api-github", "api-slack");
        normalized.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors[1].AllowedRisks
            .Should().Equal(
                AgentToolOperationRiskPayload.ReadOnly,
                AgentToolOperationRiskPayload.Write);
        normalized.RuntimeProfile.MaximumToolPolicy.ConnectedServiceSelectors[0]
            .Readiness.RequestedScopes.Should().Equal("read:user", "repo");
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldRejectUnsupportedRouteToolSetBeforeResolution()
    {
        var draft = Draft();
        draft.RuntimeProfile.RouteToolSetRef = "dynamic.route";
        var resolver = new RecordingResolver();

        var result = await NewSealer(resolver).ResolveAndSealAsync(Identity(), draft, Context());

        result.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == "UNSUPPORTED_ROUTE_TOOL_SET");
        resolver.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("classifier", "PROFILE_CLASSIFIER_TIMEOUT_INVALID")]
    [InlineData("exact-skill", "PROFILE_EXACT_SKILL_TIMEOUT_INVALID")]
    public async Task ResolveAndSealAsync_ShouldRejectLegacySubsecondTurnBudgets(
        string budget,
        string expectedCode)
    {
        var draft = Draft();
        if (budget == "classifier")
            draft.RuntimeProfile.ClassifierTimeoutMs = 600;
        else
            draft.RuntimeProfile.ExactSkillFetchTimeoutMs = 1_500;
        var resolver = new RecordingResolver();

        var result = await NewSealer(resolver).ResolveAndSealAsync(Identity(), draft, Context());

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == expectedCode);
        resolver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAndSealAsync_ShouldRejectMissingTokenAndTypedResolverFailure()
    {
        var missingTokenResolver = new RecordingResolver();
        var missingToken = await NewSealer(missingTokenResolver).ResolveAndSealAsync(
            Identity(), Draft(), Context(token: null));
        var upstreamFailure = await NewSealer(new RecordingResolver(
                ExactOrnnSkillResolutionResult.Failure("ORNN_SKILL_NOT_FOUND")))
            .ResolveAndSealAsync(Identity(), Draft(), Context());

        missingToken.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "ORNN_DEPENDENCY_UNAVAILABLE");
        missingTokenResolver.Requests.Should().BeEmpty();
        upstreamFailure.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "ORNN_SKILL_NOT_FOUND");
    }

    [Fact]
    public void AddAgentProfileApplication_ShouldRegisterInjectableSealerGraph()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IExactOrnnSkillResolver>(new RecordingResolver());

        services.AddAgentProfileApplication();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        provider.GetRequiredService<IAgentProfileSkillSealer>().Should().BeOfType<AgentProfileSkillSealer>();
        provider.GetRequiredService<AgentProfileSkillSealer>().Should()
            .BeSameAs(provider.GetRequiredService<IAgentProfileSkillSealer>());
    }

    private static AgentProfileSkillSealer NewSealer(IExactOrnnSkillResolver? resolver = null) =>
        new(resolver ?? new RecordingResolver(), new AgentProfileValidationLimits());

    private static AgentProfileSealingContext Context(
        long draftRevision = 1,
        long nextPublishedRevision = 1,
        string? token = "token") =>
        new(draftRevision, nextPublishedRevision, PublishedAt, token);

    private static AgentProfileIdentity Identity() => new()
    {
        ProfileId = "prof-alpha",
        Owner = AgentProfileOwners.ForScope("scope-alpha"),
        ProfileSlug = "research-assistant",
    };

    private static Task InitializeAsync(AgentProfileGAgent actor, InitializeAgentProfileCommand command) =>
        actor.HandleEventAsync(new EventEnvelope
        {
            Id = $"test-{Guid.NewGuid():N}",
            Timestamp = Timestamp.FromDateTimeOffset(PublishedAt),
            Route = EnvelopeRouteSemantics.CreateDirect(
                AgentProfileActorIds.Namespace(command.Identity.Owner),
                actor.Id),
            Payload = Any.Pack(command),
        });

    private static AgentProfileDraft Draft() => new()
    {
        DisplayName = "Research assistant",
        Purpose = "Research",
        Instructions = "Use verified sources.",
        RuntimeProfile = new AgentProfileSnapshot
        {
            AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
            PolicyRevision = "1",
            ActivationMode = AgentProfileActivationMode.Enforced,
            RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
            MaxPlanSteps = 4,
            HandoffTtlSeconds = 900,
            ClassifierTimeoutMs = 15_000,
            ExactSkillFetchTimeoutMs = 15_000,
            MaxSelectedSkillBytes = AgentProfileValidationLimits.RequiredMaxSelectedSkillBytes,
            MaxOwnedToolCount = 8,
            MaxSchemaBytes = 48 * 1024,
            MaximumToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "lookup", "search" },
                ToolSetRefs = { "safe.shared" },
            },
            RecoveryToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "lookup" },
                ToolSetRefs = { "safe.shared" },
            },
            Members =
            {
                new AgentProfileSkillMember
                {
                    IntentId = "research",
                    RoutingDescription = "Find sources",
                    SkillRef = new ExactRemoteSkillRef
                    {
                        Guid = "2d05bf2e-88ee-4f76-9998-728ba2f9db10",
                        LiteralVersion = "1.4",
                    },
                    ExpectedSkillName = "skill-alpha",
                    ReviewedPublisherId = "publisher-alpha",
                    TaskToolPolicy = new AgentProfileToolPolicy
                    {
                        ToolNames = { "lookup", "search" },
                        ToolSetRefs = { "safe.shared" },
                    },
                },
            },
        },
    };

    private static AgentProfileConnectedServiceSelector Selector(
        string catalogServiceSlug,
        params AgentToolOperationRiskPayload[] allowedRisks) => new()
    {
        CatalogServiceSlug = catalogServiceSlug,
        AllowedRisks = { allowedRisks },
    };

    private static ResolvedOrnnSkillPackage Package(string? mismatch = null) => new()
    {
        SkillGuid = mismatch == "guid"
            ? "3d05bf2e-88ee-4f76-9998-728ba2f9db10"
            : "2d05bf2e-88ee-4f76-9998-728ba2f9db10",
        LiteralVersion = mismatch == "version" ? "1.5" : "1.4",
        CanonicalName = mismatch == "name" ? "skill-beta" : "skill-alpha",
        PublisherId = mismatch == "publisher" ? "publisher-beta" : "publisher-alpha",
        SkillSha256 = mismatch == "hash"
            ? ByteString.CopyFrom(Enumerable.Repeat((byte)0xff, 32).ToArray())
            : SkillSha256,
        SkillMarkdownUtf8Bytes = 1_024,
        DeclaredToolNames = mismatch == "tools" ? ["admin"] : ["lookup", "search"],
    };

    private static AgentProfileOperationFact Operation(string operationId, string input) => new()
    {
        OperationId = operationId,
        CommandId = $"cmd-{operationId}",
        CorrelationId = $"corr-{operationId}",
        InputSha256 = ByteString.CopyFrom(AgentProfileDeterminism.Sha256Utf8(input)),
        RequestedAt = Timestamp.FromDateTimeOffset(PublishedAt),
    };

    private sealed class RecordingResolver : IExactOrnnSkillResolver
    {
        private readonly ExactOrnnSkillResolutionResult _result;

        public RecordingResolver(ResolvedOrnnSkillPackage? package = null)
            : this(ExactOrnnSkillResolutionResult.Success(package ?? Package()))
        {
        }

        public RecordingResolver(ExactOrnnSkillResolutionResult result) => _result = result;

        public List<ExactRemoteSkillRef> Requests { get; } = [];

        public Task<ExactOrnnSkillResolutionResult> ResolveAsync(
            string nyxIdAccessToken,
            ExactRemoteSkillRef reference,
            CancellationToken ct = default)
        {
            Requests.Add(reference.Clone());
            return Task.FromResult(_result);
        }
    }
}
