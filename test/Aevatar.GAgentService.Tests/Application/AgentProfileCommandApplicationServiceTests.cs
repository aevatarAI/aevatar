using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class AgentProfileCommandApplicationServiceTests
{
    private const string ProfileId = "prof-alpha";
    private const string ProfileSlug = "profile-alpha";
    private const string ScopeId = "scope-gamma";
    private const string OwnerHandle = "owner-alpha";

    [Fact]
    public async Task CreateAsync_ShouldDeriveAuthorityAndStableResourceIdentityWithFreshAttemptIds()
    {
        var actorPort = new RecordingActorPort();
        var service = CreateService(actorPort: actorPort);
        var caller = Caller();
        var request = CreateRequest();

        var first = await service.CreateAsync(caller, request, "create-key-alpha");
        var second = await service.CreateAsync(caller, request, "create-key-alpha");

        actorPort.CreateCommands.Should().HaveCount(2);
        var firstCommand = actorPort.CreateCommands[0];
        var secondCommand = actorPort.CreateCommands[1];
        firstCommand.Identity.Owner.OwnerCase.Should().Be(AgentProfileOwnerIdentity.OwnerOneofCase.User);
        firstCommand.Identity.Owner.User.Should().Be(caller.Owner);
        firstCommand.Identity.OwningScopeId.Should().Be(ScopeId);
        firstCommand.Identity.Reference.Should().BeEquivalentTo(new AgentProfileReference
        {
            OwnerHandle = OwnerHandle,
            ProfileSlug = ProfileSlug,
        });
        firstCommand.InitialContent.SkillBindings.Should().BeEmpty();
        firstCommand.InitialContent.RecoveryToolPolicy.Should().BeEquivalentTo(request.RecoveryToolPolicy);
        actorPort.ResolvedProfileIds.Should().Equal(firstCommand.Identity.ProfileId, secondCommand.Identity.ProfileId);
        firstCommand.ProfileActorId.Should().Be($"profile-actor:{firstCommand.Identity.ProfileId}");
        firstCommand.Operation.OperationId.Should().Be(secondCommand.Operation.OperationId);
        firstCommand.Identity.ProfileId.Should().Be(secondCommand.Identity.ProfileId);
        firstCommand.Operation.CommandId.Should().NotBe(secondCommand.Operation.CommandId);
        firstCommand.Operation.CorrelationId.Should().NotBe(secondCommand.Operation.CorrelationId);
        firstCommand.Operation.CommandId.Should().NotBe(firstCommand.Operation.CorrelationId);
        first.OperationId.Should().Be(firstCommand.Operation.OperationId);
        first.CommandId.Should().Be(firstCommand.Operation.CommandId);
        first.CorrelationId.Should().Be(firstCommand.Operation.CorrelationId);
        first.ProfileId.Should().Be(firstCommand.Identity.ProfileId);
        first.ActorId.Should().Be("namespace-actor");
        first.Accepted.Should().BeTrue();
        first.AckStage.Should().Be("accepted");
        first.ResourceUrl.Should().Be($"/api/scopes/{ScopeId}/agent-profiles/{ProfileSlug}");
    }

    [Fact]
    public async Task CreateAsync_WhenIngressProofIsUnavailable_ShouldNotTouchActorLifecycleOrDispatch()
    {
        var harness = new MissingProofAgentProfileActorPortHarness();
        var service = CreateService(actorPort: harness.Port);

        var act = () => service.CreateAsync(Caller(), CreateRequest(), "missing-proof-key");

        await act.Should().ThrowAsync<AgentProfileIngressProofUnavailableException>();
        harness.Runtime.GetCalls.Should().BeEmpty();
        harness.Runtime.CreateCalls.Should().BeEmpty();
        harness.Runtime.MaterializedCalls.Should().BeEmpty();
        harness.Dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_DecomposedCallerIdentity_ShouldUseCanonicalOwnerAndScope()
    {
        const string idempotencyKey = "unicode-caller-create";
        const string canonicalScopeId = "scope-\u00e9";
        var canonicalOwner = new AgentProfileUserOwnerIdentity
        {
            IdentityProvider = AgentProfilePolicies.NyxIdIdentityProvider,
            SubjectId = "subject-\u00e9",
        };
        var caller = new AgentProfileCallerContext(
            new AgentProfileUserOwnerIdentity
            {
                IdentityProvider = AgentProfilePolicies.NyxIdIdentityProvider,
                SubjectId = "subject-e\u0301",
            },
            "scope-e\u0301",
            OwnerHandle,
            "token-alpha");
        var actorPort = new RecordingActorPort();
        var service = CreateService(actorPort: actorPort);

        var receipt = await service.CreateAsync(caller, CreateRequest(), idempotencyKey);

        var expectedProfileId = AgentProfileDeterminism.CreateProfileId(
            canonicalOwner,
            canonicalScopeId,
            idempotencyKey);
        var command = actorPort.CreateCommands.Should().ContainSingle().Which;
        command.Identity.ProfileId.Should().Be(expectedProfileId);
        command.Identity.Owner.User.Should().Be(canonicalOwner);
        command.Identity.OwningScopeId.Should().Be(canonicalScopeId);
        receipt.ProfileId.Should().Be(expectedProfileId);
        receipt.ResourceUrl.Should().Be(
            $"/api/scopes/{canonicalScopeId}/agent-profiles/{ProfileSlug}");
    }

    [Fact]
    public async Task ManagementAsync_DecomposedCallerIdentity_ShouldUseCanonicalLookupAndOwnership()
    {
        const string canonicalScopeId = "scope-\u00e9";
        var canonicalOwner = new AgentProfileOwnerIdentity
        {
            User = new AgentProfileUserOwnerIdentity
            {
                IdentityProvider = AgentProfilePolicies.NyxIdIdentityProvider,
                SubjectId = "subject-\u00e9",
            },
        };
        var caller = new AgentProfileCallerContext(
            new AgentProfileUserOwnerIdentity
            {
                IdentityProvider = AgentProfilePolicies.NyxIdIdentityProvider,
                SubjectId = "subject-e\u0301",
            },
            "scope-e\u0301",
            OwnerHandle,
            "token-alpha");
        var identity = Identity();
        identity.Owner = canonicalOwner;
        identity.OwningScopeId = canonicalScopeId;
        var namespaceEntry = NamespaceEntry() with
        {
            Owner = canonicalOwner,
            OwningScopeId = canonicalScopeId,
        };
        var namespacePort = new RecordingNamespaceQueryPort { OwnedResult = namespaceEntry };
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            namespacePort,
            new RecordingManagementQueryPort
            {
                Result = ManagementSnapshot() with { Identity = identity },
            },
            actorPort);

        var receipt = await service.UpdateDraftAsync(
            caller,
            ProfileSlug,
            14,
            UpdateRequest("Canonical caller update"),
            "unicode-caller-update");

        var lookup = namespacePort.OwnedCalls.Should().ContainSingle().Which;
        lookup.Owner.Should().Be(canonicalOwner);
        lookup.OwningScopeId.Should().Be(canonicalScopeId);
        actorPort.UpdateCommands.Should().ContainSingle()
            .Which.Identity.Should().BeEquivalentTo(identity);
        receipt.ResourceUrl.Should().Be(
            $"/api/scopes/{canonicalScopeId}/agent-profiles/{ProfileSlug}");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" create-key ")]
    public async Task CreateAsync_ShouldRequireNonEmptyIdempotencyKey(string idempotencyKey)
    {
        var actorPort = new RecordingActorPort();
        var service = CreateService(actorPort: actorPort);

        var act = () => service.CreateAsync(Caller(), CreateRequest(), idempotencyKey);

        var exception = await act.Should().ThrowAsync<AgentProfileRequestException>();
        exception.Which.Code.Should().Be(
            string.IsNullOrWhiteSpace(idempotencyKey)
                ? "IDEMPOTENCY_KEY_REQUIRED"
                : "INVALID_IDEMPOTENCY_KEY");
        actorPort.ResolvedProfileIds.Should().BeEmpty();
        actorPort.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectReservedSystemHandleFromOrdinaryCaller()
    {
        var actorPort = new RecordingActorPort();
        var service = CreateService(actorPort: actorPort);
        var request = CreateRequest() with { OwnerHandle = AgentProfilePolicies.SystemOwnerHandle };

        var act = () => service.CreateAsync(Caller(), request, "create-system-attempt");

        var exception = await act.Should().ThrowAsync<AgentProfileRequestException>();
        exception.Which.Diagnostics.Should().ContainSingle(x => x.Code == "RESERVED_OWNER_HANDLE");
        actorPort.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectReservedPlatformScopeBeforeTargetAllocationOrDispatch()
    {
        var actorPort = new RecordingActorPort();
        var service = CreateService(actorPort: actorPort);
        var caller = Caller() with { ScopeId = PlatformScopeSemantics.ReservedPlatformScopeId };

        var act = () => service.CreateAsync(caller, CreateRequest(), "reserved-scope-create");

        var exception = await act.Should().ThrowAsync<AgentProfileRequestException>();
        exception.Which.Code.Should().Be("RESERVED_OWNING_SCOPE_ID");
        actorPort.ResolvedProfileIds.Should().BeEmpty();
        actorPort.DispatchCount.Should().Be(0);
    }

    [Fact]
    public void RequestContracts_ShouldNotExposeAuthorityVersionDigestOrOutcomeInputs()
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Owner",
            "ScopeId",
            "ProfileId",
            "System",
            "AuthorityStateVersion",
            "DraftRevision",
            "DraftSha256",
            "PublishedRevision",
            "PublishedSnapshotSha256",
            "LastMutation",
            "MutationOutcome",
            "SealedSkill",
        };
        var requestTypes = new[]
        {
            typeof(CreateAgentProfileRequest),
            typeof(UpdateAgentProfileDraftRequest),
            typeof(UpsertAgentProfileSkillBindingRequest),
        };

        requestTypes.SelectMany(static type => type.GetProperties())
            .Select(static property => property.Name)
            .Should().NotContain(name => forbidden.Contains(name));
    }

    [Theory]
    [InlineData(ManagementCommandKind.Validate, DraftDigestCorruption.MalformedLength)]
    [InlineData(ManagementCommandKind.Validate, DraftDigestCorruption.WrongContent)]
    [InlineData(ManagementCommandKind.Publish, DraftDigestCorruption.MalformedLength)]
    [InlineData(ManagementCommandKind.Publish, DraftDigestCorruption.WrongContent)]
    [InlineData(ManagementCommandKind.UpdateDraft, DraftDigestCorruption.MalformedLength)]
    [InlineData(ManagementCommandKind.UpdateDraft, DraftDigestCorruption.WrongContent)]
    public async Task ManagementAsync_InvalidDraftDigest_ShouldFailClosedBeforeResolutionOrDispatch(
        ManagementCommandKind commandKind,
        DraftDigestCorruption corruption)
    {
        var management = ManagementSnapshot() with
        {
            DraftSha256 = corruption == DraftDigestCorruption.MalformedLength
                ? ByteString.CopyFrom(new byte[31])
                : Digest(0x7f),
        };
        var resolver = SuccessResolver();
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() },
            new RecordingManagementQueryPort { Result = management },
            actorPort,
            resolver);
        Func<Task> act = commandKind switch
        {
            ManagementCommandKind.Validate => async () =>
                await service.ValidateAsync(Caller(), ProfileSlug),
            ManagementCommandKind.Publish => async () =>
                await service.PublishAsync(Caller(), ProfileSlug, 14, "invalid-draft-publish"),
            ManagementCommandKind.UpdateDraft => async () =>
                await service.UpdateDraftAsync(
                    Caller(),
                    ProfileSlug,
                    14,
                    UpdateRequest("Invalid draft update"),
                    "invalid-draft-update"),
            _ => throw new ArgumentOutOfRangeException(nameof(commandKind)),
        };

        await act.Should().ThrowAsync<AgentProfileNotFoundException>();
        resolver.Calls.Should().Be(0);
        actorPort.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task DraftMutations_ShouldResolveOnlyTheCallersCommittedNamespaceAndUseServerIdentity()
    {
        var namespacePort = new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() };
        var managementPort = new RecordingManagementQueryPort { Result = ManagementSnapshot() };
        var actorPort = new RecordingActorPort();
        var service = CreateService(namespacePort, managementPort, actorPort);

        await service.UpdateDraftAsync(
            Caller(),
            ProfileSlug,
            14,
            UpdateRequest("Updated profile"),
            "update-alpha");
        await service.UpsertSkillBindingAsync(
            Caller(),
            ProfileSlug,
            "bind-beta",
            14,
            new UpsertAgentProfileSkillBindingRequest(
                AgentProfileSkillActivationMode.Routed,
                ExactReference(2, "skill-beta"),
                RoutingPolicy("bind-beta")),
            "upsert-beta");
        await service.RemoveSkillBindingAsync(
            Caller(),
            ProfileSlug,
            "bind-alpha",
            14,
            "remove-alpha");

        namespacePort.OwnedCalls.Should().HaveCount(3);
        namespacePort.OwnedCalls.Should().OnlyContain(call =>
            call.Owner.OwnerCase == AgentProfileOwnerIdentity.OwnerOneofCase.User &&
            call.Owner.User.Equals(Caller().Owner) &&
            call.OwningScopeId == ScopeId &&
            call.ProfileSlug == ProfileSlug);
        managementPort.ProfileIds.Should().Equal(ProfileId, ProfileId, ProfileId);
        actorPort.UpdateCommands.Should().ContainSingle();
        actorPort.UpsertCommands.Should().ContainSingle();
        actorPort.RemoveCommands.Should().ContainSingle();
        actorPort.UpdateCommands[0].Identity.Should().BeEquivalentTo(Identity());
        actorPort.UpdateCommands[0].ExpectedAuthorityStateVersion.Should().Be(14);
        actorPort.UpdateCommands[0].Content.DisplayName.Should().Be("Updated profile");
        actorPort.UpdateCommands[0].Content.RecoveryToolPolicy.Should()
            .BeEquivalentTo(UpdateRequest("ignored").RecoveryToolPolicy);
        actorPort.UpdateCommands[0].Content.SkillBindings.Should().ContainSingle()
            .Which.BindingId.Should().Be("bind-alpha");
        actorPort.UpsertCommands[0].Identity.Should().BeEquivalentTo(Identity());
        actorPort.UpsertCommands[0].Binding.BindingId.Should().Be("bind-beta");
        actorPort.UpsertCommands[0].Binding.Skill.ExpectedName.Should().Be("skill-beta");
        actorPort.UpsertCommands[0].Binding.RoutingPolicy.Should()
            .BeEquivalentTo(RoutingPolicy("bind-beta"));
        actorPort.RemoveCommands[0].Identity.Should().BeEquivalentTo(Identity());
        actorPort.RemoveCommands[0].BindingId.Should().Be("bind-alpha");
    }

    [Fact]
    public async Task MutationAsync_KnownStaleVersion_ShouldFailBeforeDispatch()
    {
        var namespacePort = new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() };
        var managementPort = new RecordingManagementQueryPort { Result = ManagementSnapshot() };
        var actorPort = new RecordingActorPort();
        var service = CreateService(namespacePort, managementPort, actorPort);

        var act = () => service.UpdateDraftAsync(
            Caller(),
            ProfileSlug,
            13,
            UpdateRequest("Stale update"),
            "stale-update");

        var exception = await act.Should().ThrowAsync<AgentProfilePreconditionException>();
        exception.Which.Code.Should().Be("AGENT_PROFILE_STALE_VERSION");
        exception.Which.ExpectedAuthorityStateVersion.Should().Be(13);
        exception.Which.ObservedAuthorityStateVersion.Should().Be(14);
        actorPort.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task MutationAsync_StaleIdempotentRetry_ShouldReachActorWithExpectedVersionAndFreshAttemptIds()
    {
        const string idempotencyKey = "retry-update";
        var operationId = AgentProfileDeterminism.CreateOperationId(
            "update-agent-profile-draft",
            ProfileId,
            idempotencyKey);
        var snapshot = ManagementSnapshot(lastOperationId: operationId);
        var namespacePort = new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() };
        var managementPort = new RecordingManagementQueryPort { Result = snapshot };
        var actorPort = new RecordingActorPort();
        var service = CreateService(namespacePort, managementPort, actorPort);

        await service.UpdateDraftAsync(
            Caller(), ProfileSlug, 13, UpdateRequest("Retry update"), idempotencyKey);
        await service.UpdateDraftAsync(
            Caller(), ProfileSlug, 13, UpdateRequest("Retry update"), idempotencyKey);

        actorPort.UpdateCommands.Should().HaveCount(2);
        actorPort.UpdateCommands.Should().OnlyContain(command =>
            command.ExpectedAuthorityStateVersion == 13 &&
            command.Operation.OperationId == operationId);
        actorPort.UpdateCommands[0].Operation.CommandId.Should()
            .NotBe(actorPort.UpdateCommands[1].Operation.CommandId);
        actorPort.UpdateCommands[0].Operation.CorrelationId.Should()
            .NotBe(actorPort.UpdateCommands[1].Operation.CorrelationId);
    }

    [Theory]
    [InlineData(14, false, true)]
    [InlineData(13, false, false)]
    [InlineData(15, false, false)]
    [InlineData(15, true, false)]
    [InlineData(13, true, true)]
    public async Task MutationAsync_ExpectedVersionMatrix_ShouldRequireExactOrMatchingLowerRetry(
        long expectedAuthorityStateVersion,
        bool lastMutationMatches,
        bool shouldDispatch)
    {
        const string idempotencyKey = "version-matrix-update";
        var operationId = AgentProfileDeterminism.CreateOperationId(
            "update-agent-profile-draft",
            ProfileId,
            idempotencyKey);
        var management = ManagementSnapshot(
            lastOperationId: lastMutationMatches ? operationId : "other-operation");
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() },
            new RecordingManagementQueryPort { Result = management },
            actorPort);

        var act = () => service.UpdateDraftAsync(
            Caller(),
            ProfileSlug,
            expectedAuthorityStateVersion,
            UpdateRequest("Version matrix update"),
            idempotencyKey);

        if (shouldDispatch)
        {
            var receipt = await act();
            receipt.Accepted.Should().BeTrue();
            actorPort.UpdateCommands.Should().ContainSingle()
                .Which.ExpectedAuthorityStateVersion.Should().Be(expectedAuthorityStateVersion);
        }
        else
        {
            var exception = await act.Should().ThrowAsync<AgentProfilePreconditionException>();
            exception.Which.ExpectedAuthorityStateVersion.Should().Be(expectedAuthorityStateVersion);
            exception.Which.ObservedAuthorityStateVersion.Should().Be(14);
            actorPort.DispatchCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task UpsertAsync_ShouldDispatchStructurallyValidSecondDefaultForActorDecision()
    {
        var draft = Draft();
        draft.SkillBindings[0].ActivationMode = AgentProfileSkillActivationMode.DefaultForUnmatchedTurn;
        var namespacePort = new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() };
        var managementPort = new RecordingManagementQueryPort
        {
            Result = ManagementSnapshot(draft: draft),
        };
        var actorPort = new RecordingActorPort();
        var service = CreateService(namespacePort, managementPort, actorPort);

        var receipt = await service.UpsertSkillBindingAsync(
            Caller(),
            ProfileSlug,
            "bind-beta",
            14,
            new UpsertAgentProfileSkillBindingRequest(
                AgentProfileSkillActivationMode.DefaultForUnmatchedTurn,
                ExactReference(2, "skill-beta"),
                RoutingPolicy("bind-beta")),
            "second-default");

        receipt.Accepted.Should().BeTrue();
        actorPort.UpsertCommands.Should().ContainSingle();
    }

    [Fact]
    public async Task ValidateAsync_ShouldResolveEveryBindingAndReturnOnlySafeEphemeralSummaries()
    {
        var draft = DraftWithTwoRoutedBindings();
        var management = ManagementSnapshot(draft: draft);
        var namespacePort = new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() };
        var managementPort = new RecordingManagementQueryPort { Result = management };
        var actorPort = new RecordingActorPort();
        var resolver = new RecordingResolver(reference =>
        {
            var package = PackageFor(reference);
            package.Instructions = "sealed-instructions-secret";
            package.Assets.Add(new AgentProfileNamedTextAsset
            {
                Path = "secret.txt",
                Content = "sealed-asset-secret",
            });
            return ExactOrnnSkillResolutionResult.Success(package);
        });
        var service = CreateService(namespacePort, managementPort, actorPort, resolver);

        var report = await service.ValidateAsync(Caller(), ProfileSlug);

        report.Valid.Should().BeTrue();
        report.DraftRevision.Should().Be(management.DraftRevision);
        report.DraftSha256.Should().Equal(management.DraftSha256);
        report.Diagnostics.Should().BeEmpty();
        report.ResolvedSkills.Should().HaveCount(2);
        report.ResolvedSkills.Select(static skill => skill.BindingId)
            .Should().Equal("bind-alpha", "bind-beta");
        report.ResolvedSkills.Should().OnlyContain(skill => skill.ContentSha256.Length == 32);
        resolver.Calls.Should().Be(2);
        actorPort.DispatchCount.Should().Be(0);
        report.ToString().Should().NotContain("sealed-instructions-secret");
        report.ToString().Should().NotContain("sealed-asset-secret");
        report.ToString().Should().NotContain("token-alpha");
    }

    [Fact]
    public async Task ValidateAsync_MixedResolutionResults_ShouldPreserveSuccessfulSummaries()
    {
        var resolver = new RecordingResolver(reference =>
            reference.ExpectedName == "skill-alpha"
                ? ExactOrnnSkillResolutionResult.Success(PackageFor(reference))
                : ExactOrnnSkillResolutionResult.Failed(
                    "ORNN_SKILL_NOT_FOUND",
                    "The exact Ornn skill was not found."));
        var service = CreateService(
            new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() },
            new RecordingManagementQueryPort
            {
                Result = ManagementSnapshot(draft: DraftWithTwoRoutedBindings()),
            },
            new RecordingActorPort(),
            resolver);

        var report = await service.ValidateAsync(Caller(), ProfileSlug);

        report.Valid.Should().BeFalse();
        report.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == "ORNN_SKILL_NOT_FOUND" &&
            diagnostic.Path == "skill_bindings.bind-beta");
        report.ResolvedSkills.Should().ContainSingle()
            .Which.BindingId.Should().Be("bind-alpha");
        report.ResolvedSkills[0].ExactReference.Should().BeEquivalentTo(ExactReference());
        report.ResolvedSkills[0].ContentSha256.Should().HaveCount(32);
        resolver.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ValidateAsync_AggregatePromptLimit_ShouldPreserveAllSuccessfulSummaries()
    {
        var resolver = new RecordingResolver(reference =>
        {
            var package = PackageFor(reference);
            package.Instructions = new string(
                'a',
                AgentProfileValidationLimits.RawAuthoritativeAggregateContentMaxUtf8Bytes / 2 + 1);
            return ExactOrnnSkillResolutionResult.Success(package);
        });
        var service = CreateService(
            new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() },
            new RecordingManagementQueryPort
            {
                Result = ManagementSnapshot(draft: DraftWithTwoRoutedBindings()),
            },
            new RecordingActorPort(),
            resolver);

        var report = await service.ValidateAsync(Caller(), ProfileSlug);

        report.Valid.Should().BeFalse();
        report.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == "AGGREGATE_PROMPT_BYTES_EXCEEDED");
        report.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == "AGGREGATE_PROMPT_TOKENS_EXCEEDED");
        report.ResolvedSkills.Select(static skill => skill.BindingId)
            .Should().Equal("bind-alpha", "bind-beta");
        report.ResolvedSkills.Should().OnlyContain(skill => skill.ContentSha256.Length == 32);
        resolver.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ValidateAndPublishAsync_MultipleDefaultBindings_ShouldReportPublishOnlyInvariant()
    {
        var draft = DraftWithTwoRoutedBindings();
        foreach (var binding in draft.SkillBindings)
            binding.ActivationMode = AgentProfileSkillActivationMode.DefaultForUnmatchedTurn;
        var namespacePort = new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() };
        var managementPort = new RecordingManagementQueryPort
        {
            Result = ManagementSnapshot(draft: draft),
        };
        var actorPort = new RecordingActorPort();
        var resolver = SuccessResolver();
        var service = CreateService(namespacePort, managementPort, actorPort, resolver);

        var report = await service.ValidateAsync(Caller(), ProfileSlug);
        var publish = () => service.PublishAsync(Caller(), ProfileSlug, 14, "publish-defaults");

        report.Valid.Should().BeFalse();
        report.Diagnostics.Should().ContainSingle(x => x.Code == "MULTIPLE_DEFAULT_SKILLS");
        var exception = await publish.Should().ThrowAsync<AgentProfilePublishValidationException>();
        exception.Which.Diagnostics.Should().ContainSingle(x => x.Code == "MULTIPLE_DEFAULT_SKILLS");
        resolver.Calls.Should().Be(0);
        actorPort.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task PublishAsync_ShouldResolveFreshAndDispatchServerOwnedCandidateWithoutInferringNoChange()
    {
        var draft = Draft();
        var draftSha256 = AgentProfileDeterminism.ComputeDraftSha256(draft);
        var management = ManagementSnapshot(
            draft: draft,
            publishedSourceDraftSha256: draftSha256);
        var namespacePort = new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() };
        var managementPort = new RecordingManagementQueryPort { Result = management };
        var actorPort = new RecordingActorPort();
        var resolver = SuccessResolver();
        var service = CreateService(namespacePort, managementPort, actorPort, resolver);

        await service.ValidateAsync(Caller(), ProfileSlug);
        var receipt = await service.PublishAsync(Caller(), ProfileSlug, 14, "publish-alpha");

        resolver.Calls.Should().Be(2);
        actorPort.PublishCommands.Should().ContainSingle();
        var command = actorPort.PublishCommands[0];
        command.Identity.Should().BeEquivalentTo(Identity());
        command.ExpectedAuthorityStateVersion.Should().Be(14);
        command.ExpectedDraftRevision.Should().Be(management.DraftRevision);
        command.ExpectedDraftSha256.Should().Equal(management.DraftSha256);
        command.Snapshot.Identity.Should().BeEquivalentTo(Identity());
        command.Snapshot.PublishedRevision.Should().Be(0);
        command.Snapshot.SourceDraftSha256.Should().Equal(management.DraftSha256);
        command.Operation.InputSha256.Should().Equal(
            AgentProfileDeterminism.ComputePublishAgentProfileInputSha256(
                command.Identity,
                command.Snapshot));
        receipt.Accepted.Should().BeTrue();
        receipt.AckStage.Should().Be("accepted");
        receipt.OperationId.Should().Be(command.Operation.OperationId);
        receipt.ProfileId.Should().Be(ProfileId);
    }

    [Fact]
    public async Task PublishAsync_KnownStaleNonRetry_ShouldFailBeforeExactResolution()
    {
        var resolver = SuccessResolver();
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() },
            new RecordingManagementQueryPort
            {
                Result = ManagementSnapshot(lastOperationId: "other-operation"),
            },
            actorPort,
            resolver);

        var act = () => service.PublishAsync(
            Caller(), ProfileSlug, 13, "known-stale-publish");

        var exception = await act.Should().ThrowAsync<AgentProfilePreconditionException>();
        exception.Which.ExpectedAuthorityStateVersion.Should().Be(13);
        exception.Which.ObservedAuthorityStateVersion.Should().Be(14);
        resolver.Calls.Should().Be(0);
        actorPort.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task PublishAsync_MatchingStaleRetry_ShouldResolveFreshAndDispatchEachAttempt()
    {
        const string idempotencyKey = "stale-publish-retry";
        var operationId = AgentProfileDeterminism.CreateOperationId(
            "publish-agent-profile",
            ProfileId,
            idempotencyKey);
        var resolver = SuccessResolver();
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() },
            new RecordingManagementQueryPort
            {
                Result = ManagementSnapshot(lastOperationId: operationId),
            },
            actorPort,
            resolver);

        await service.PublishAsync(Caller(), ProfileSlug, 13, idempotencyKey);
        await service.PublishAsync(Caller(), ProfileSlug, 13, idempotencyKey);

        resolver.Calls.Should().Be(2);
        actorPort.PublishCommands.Should().HaveCount(2);
        actorPort.PublishCommands.Should().OnlyContain(command =>
            command.ExpectedAuthorityStateVersion == 13 &&
            command.Operation.OperationId == operationId &&
            command.Operation.InputSha256.Equals(
                AgentProfileDeterminism.ComputePublishAgentProfileInputSha256(
                    command.Identity,
                    command.Snapshot)));
        actorPort.PublishCommands[0].Operation.CommandId.Should()
            .NotBe(actorPort.PublishCommands[1].Operation.CommandId);
        actorPort.PublishCommands[0].Operation.CorrelationId.Should()
            .NotBe(actorPort.PublishCommands[1].Operation.CorrelationId);
    }

    [Fact]
    public async Task PublishAsync_ShouldMapContentFailureWithoutDispatch()
    {
        var resolver = new RecordingResolver(_ =>
            ExactOrnnSkillResolutionResult.Failed(
                "ORNN_VERSION_MISMATCH",
                "safe version mismatch"));
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() },
            new RecordingManagementQueryPort { Result = ManagementSnapshot() },
            actorPort,
            resolver);

        var act = () => service.PublishAsync(Caller(), ProfileSlug, 14, "publish-invalid");

        var exception = await act.Should().ThrowAsync<AgentProfilePublishValidationException>();
        exception.Which.Code.Should().Be("ORNN_VERSION_MISMATCH");
        actorPort.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task PublishAsync_ShouldMapTransientDependencyFailureWithoutDispatch()
    {
        var resolver = new RecordingResolver(_ =>
            ExactOrnnSkillResolutionResult.Failed(
                "ORNN_DEPENDENCY_UNAVAILABLE",
                "safe dependency failure"));
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() },
            new RecordingManagementQueryPort { Result = ManagementSnapshot() },
            actorPort,
            resolver);

        var act = () => service.PublishAsync(Caller(), ProfileSlug, 14, "publish-unavailable");

        var exception = await act.Should().ThrowAsync<AgentProfileDependencyUnavailableException>();
        exception.Which.Code.Should().Be("ORNN_DEPENDENCY_UNAVAILABLE");
        actorPort.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task PublishAsync_BoundDraftWithoutBearer_ShouldReturnTypedAuthenticationFailure()
    {
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() },
            new RecordingManagementQueryPort { Result = ManagementSnapshot() },
            actorPort);
        var caller = Caller() with { NyxIdAccessToken = null };

        var act = () => service.PublishAsync(caller, ProfileSlug, 14, "publish-without-bearer");

        var exception = await act.Should().ThrowAsync<AgentProfileAuthenticationRequiredException>();
        exception.Which.Code.Should().Be("ORNN_ACCESS_TOKEN_REQUIRED");
        actorPort.DispatchCount.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManagementMutation_ShouldRejectSystemOrDifferentOwnerEntry(bool systemOwner)
    {
        var entry = NamespaceEntry();
        entry = entry with
        {
            Owner = systemOwner
                ? new AgentProfileOwnerIdentity
                {
                    System = new AgentProfileSystemOwnerIdentity
                    {
                        PlatformId = AgentProfilePolicies.AevatarPlatformId,
                    },
                }
                : new AgentProfileOwnerIdentity
                {
                    User = new AgentProfileUserOwnerIdentity
                    {
                        IdentityProvider = AgentProfilePolicies.NyxIdIdentityProvider,
                        SubjectId = "subject-other",
                    },
                },
            OwningScopeId = systemOwner ? string.Empty : ScopeId,
        };
        var namespacePort = new RecordingNamespaceQueryPort { OwnedResult = entry };
        var managementPort = new RecordingManagementQueryPort { Result = ManagementSnapshot() };
        var actorPort = new RecordingActorPort();
        var service = CreateService(namespacePort, managementPort, actorPort);

        var act = () => service.UpdateDraftAsync(
            Caller(), ProfileSlug, 14, UpdateRequest("Forbidden"), "forbidden");

        await act.Should().ThrowAsync<AgentProfileNotFoundException>();
        managementPort.ProfileIds.Should().BeEmpty();
        actorPort.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task ManagementMutation_ShouldRejectReservedSystemReferenceEvenWithForgedCallerOwnerRow()
    {
        var systemReference = new AgentProfileReference
        {
            OwnerHandle = AgentProfilePolicies.SystemOwnerHandle,
            ProfileSlug = "studio",
        };
        var forgedIdentity = Identity();
        forgedIdentity.Reference = systemReference;
        var entry = NamespaceEntry() with { Reference = systemReference };
        var management = ManagementSnapshot() with { Identity = forgedIdentity };
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            new RecordingNamespaceQueryPort { OwnedResult = entry },
            new RecordingManagementQueryPort { Result = management },
            actorPort);

        var act = () => service.UpdateDraftAsync(
            Caller(), "studio", 14, UpdateRequest("Forbidden"), "forged-system-row");

        await act.Should().ThrowAsync<AgentProfileNotFoundException>();
        actorPort.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task MutationAsync_RejectedAdmission_ShouldThrowTypedFailureWithoutAcceptedReceipt()
    {
        var actorPort = new RecordingActorPort { AcceptDispatch = false };
        var service = CreateService(
            new RecordingNamespaceQueryPort { OwnedResult = NamespaceEntry() },
            new RecordingManagementQueryPort { Result = ManagementSnapshot() },
            actorPort);

        var act = () => service.UpdateDraftAsync(
            Caller(), ProfileSlug, 14, UpdateRequest("Rejected"), "rejected");

        var exception = await act.Should().ThrowAsync<AgentProfileDispatchRejectedException>();
        exception.Which.Code.Should().Be("AGENT_PROFILE_DISPATCH_REJECTED");
        actorPort.UpdateCommands.Should().ContainSingle();
    }

    [Fact]
    public async Task ValidateAsync_ShouldPreserveCallerCancellation()
    {
        var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var resolver = SuccessResolver();
        var service = CreateService(
            new RecordingNamespaceQueryPort
            {
                OwnedResult = NamespaceEntry(),
                ObserveCancellation = false,
            },
            new RecordingManagementQueryPort
            {
                Result = ManagementSnapshot(),
                ObserveCancellation = false,
            },
            resolver: resolver);

        var act = () => service.ValidateAsync(Caller(), ProfileSlug, cancellation.Token);

        var exception = await act.Should().ThrowAsync<OperationCanceledException>();
        exception.Which.CancellationToken.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task UnavailableResolver_ShouldReturnTypedFailureWithoutFabricatedPackage()
    {
        var resolver = new UnavailableExactOrnnSkillResolver();

        var result = await resolver.ResolveAsync("token-alpha", ExactReference());

        result.IsSuccess.Should().BeFalse();
        result.Package.Should().BeNull();
        result.Failure.Should().NotBeNull();
        result.Failure!.Code.Should().Be("ORNN_DEPENDENCY_UNAVAILABLE");
    }

    private static AgentProfileCommandApplicationService CreateService(
        RecordingNamespaceQueryPort? namespacePort = null,
        RecordingManagementQueryPort? managementPort = null,
        IAgentProfileActorPort? actorPort = null,
        RecordingResolver? resolver = null)
    {
        namespacePort ??= new RecordingNamespaceQueryPort();
        managementPort ??= new RecordingManagementQueryPort();
        actorPort ??= new RecordingActorPort();
        resolver ??= SuccessResolver();
        var toolSetRegistry = new StaticToolSetRegistry([]);
        var sealer = new AgentProfileSkillSealer(resolver, toolSetRegistry);
        return new AgentProfileCommandApplicationService(
            namespacePort,
            managementPort,
            actorPort,
            new AgentProfileDraftValidator(resolver, toolSetRegistry),
            sealer,
            new AgentProfileOperationFactory());
    }

    private static AgentProfileCallerContext Caller() =>
        new(
            new AgentProfileUserOwnerIdentity
            {
                IdentityProvider = AgentProfilePolicies.NyxIdIdentityProvider,
                SubjectId = "subject-alpha",
            },
            ScopeId,
            OwnerHandle,
            "token-alpha");

    private static CreateAgentProfileRequest CreateRequest() =>
        new(
            ProfileSlug,
            OwnerHandle,
            "Profile Alpha",
            "Controls exact test behavior",
            "Follow the Profile procedure.",
            ToolPolicy(),
            RecoveryPolicy());

    private static UpdateAgentProfileDraftRequest UpdateRequest(string displayName) =>
        new(
            displayName,
            "Updated purpose",
            "Updated instructions",
            ToolPolicy(),
            RecoveryPolicy());

    private static AgentProfileToolPolicy ToolPolicy() =>
        new() { Mode = AgentProfileToolPolicyMode.InheritRouteMaximum };

    private static AgentProfileToolPolicy RecoveryPolicy() =>
        new()
        {
            Mode = AgentProfileToolPolicyMode.ExplicitAllowlist,
            ToolNames = { "recovery-tool" },
        };

    private static AgentProfileIdentity Identity() =>
        new()
        {
            ProfileId = ProfileId,
            Owner = new AgentProfileOwnerIdentity { User = Caller().Owner },
            OwningScopeId = ScopeId,
            Reference = new AgentProfileReference
            {
                OwnerHandle = OwnerHandle,
                ProfileSlug = ProfileSlug,
            },
        };

    private static AgentProfileNamespaceEntrySnapshot NamespaceEntry() =>
        new(
            9,
            "namespace-event-9",
            ProfileId,
            Identity().Reference,
            Identity().Owner,
            ScopeId,
            AgentProfileProvisioningStatus.Active,
            new AgentProfilePublishedSummary
            {
                Reference = Identity().Reference,
                DisplayName = "Published alpha",
                Purpose = "Published purpose",
                PublishedRevision = 3,
                SnapshotSha256 = Digest(0x33),
            });

    private static AgentProfileManagementSnapshot ManagementSnapshot(
        AgentProfileContent? draft = null,
        string lastOperationId = "last-operation",
        ByteString? publishedSourceDraftSha256 = null)
    {
        draft ??= Draft();
        return new AgentProfileManagementSnapshot(
            14,
            "profile-event-14",
            Identity(),
            draft,
            5,
            AgentProfileDeterminism.ComputeDraftSha256(draft),
            3,
            Digest(0x33),
            publishedSourceDraftSha256 ?? Digest(0x22),
            new AgentProfileMutationOutcome
            {
                Operation = new AgentProfileOperationFact
                {
                    OperationId = lastOperationId,
                    InputSha256 = Digest(0x11),
                    CommandId = "last-command",
                    CorrelationId = "last-correlation",
                },
                Status = AgentProfileMutationStatus.Applied,
                DraftRevision = 5,
                DraftSha256 = AgentProfileDeterminism.ComputeDraftSha256(draft),
                PublishedRevision = 3,
                PublishedSnapshotSha256 = Digest(0x33),
            });
    }

    private static AgentProfileContent Draft() =>
        new()
        {
            DisplayName = "Draft alpha",
            Purpose = "Draft purpose",
            Instructions = "draft instructions",
            ToolPolicy = ToolPolicy(),
            SkillBindings =
            {
                Binding(
                    "bind-alpha",
                    AgentProfileSkillActivationMode.Routed,
                    ExactReference()),
            },
        };

    private static AgentProfileContent DraftWithTwoRoutedBindings()
    {
        var draft = Draft();
        draft.SkillBindings.Add(Binding(
            "bind-beta",
            AgentProfileSkillActivationMode.Routed,
            ExactReference(2, "skill-beta")));
        return draft;
    }

    private static AgentProfileSkillBinding Binding(
        string bindingId,
        AgentProfileSkillActivationMode activationMode,
        ExactOrnnSkillReference reference)
    {
        var binding = new AgentProfileSkillBinding
        {
            BindingId = bindingId,
            ActivationMode = activationMode,
            Skill = reference,
        };
        if (activationMode is AgentProfileSkillActivationMode.Routed or
            AgentProfileSkillActivationMode.DefaultForUnmatchedTurn)
        {
            binding.RoutingPolicy = RoutingPolicy(bindingId);
        }
        return binding;
    }

    private static AgentProfileSkillRoutingPolicy RoutingPolicy(string intentId) =>
        new()
        {
            IntentId = intentId,
            RoutingDescription = $"Route requests for {intentId}.",
            TaskToolPolicy = RecoveryPolicy(),
            SideEffectClass = AgentProfileSkillSideEffectClass.ReadOnly,
            ExplicitTriggerAliases = { intentId },
        };

    private static ExactOrnnSkillReference ExactReference(
        int identity = 1,
        string name = "skill-alpha") =>
        new()
        {
            SkillGuid = $"00000000-0000-0000-0000-{identity:D12}",
            LiteralVersion = "1.4",
            ExpectedName = name,
            ExpectedPublisherId = "publisher-alpha",
        };

    private static ResolvedOrnnSkillPackage PackageFor(ExactOrnnSkillReference reference) =>
        new()
        {
            SkillGuid = reference.SkillGuid,
            LiteralVersion = reference.LiteralVersion,
            CanonicalName = reference.ExpectedName,
            PublisherId = reference.ExpectedPublisherId,
            UpstreamSkillHash = "upstream-hash-alpha",
            Description = "Safe descriptor",
            Instructions = "Sealed instructions",
            Arguments = "request",
            WhenToUse = "Use for exact tests",
            ModelInvocable = true,
            UserInvocable = true,
        };

    private static RecordingResolver SuccessResolver() =>
        new(reference => ExactOrnnSkillResolutionResult.Success(PackageFor(reference)));

    private static ByteString Digest(byte value) =>
        ByteString.CopyFrom(Enumerable.Repeat(value, 32).ToArray());

    public enum ManagementCommandKind
    {
        Validate,
        Publish,
        UpdateDraft,
    }

    public enum DraftDigestCorruption
    {
        MalformedLength,
        WrongContent,
    }

    private sealed class RecordingNamespaceQueryPort : IAgentProfileNamespaceQueryPort
    {
        public AgentProfileNamespaceEntrySnapshot? OwnedResult { get; init; }
        public bool ObserveCancellation { get; init; } = true;
        public List<(AgentProfileOwnerIdentity Owner, string OwningScopeId, string ProfileSlug)> OwnedCalls { get; } = [];

        public Task<AgentProfileNamespaceEntrySnapshot?> GetOwnedAsync(
            AgentProfileOwnerIdentity owner,
            string owningScopeId,
            string profileSlug,
            CancellationToken ct = default)
        {
            if (ObserveCancellation)
                ct.ThrowIfCancellationRequested();
            OwnedCalls.Add((owner.Clone(), owningScopeId, profileSlug));
            return Task.FromResult(OwnedResult?.DeepClone());
        }

        public Task<AgentProfileNamespaceEntrySnapshot?> GetByReferenceAsync(
            AgentProfileReference reference,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Management commands must not use discovery lookup.");
    }

    private sealed class RecordingManagementQueryPort : IAgentProfileManagementQueryPort
    {
        public AgentProfileManagementSnapshot? Result { get; init; }
        public bool ObserveCancellation { get; init; } = true;
        public List<string> ProfileIds { get; } = [];

        public Task<AgentProfileManagementSnapshot?> GetAsync(
            string profileId,
            CancellationToken ct = default)
        {
            if (ObserveCancellation)
                ct.ThrowIfCancellationRequested();
            ProfileIds.Add(profileId);
            return Task.FromResult(Result?.DeepClone());
        }
    }

    private sealed class RecordingActorPort : IAgentProfileActorPort
    {
        public bool AcceptDispatch { get; init; } = true;
        public List<string> ResolvedProfileIds { get; } = [];
        public List<CreateAgentProfileCommand> CreateCommands { get; } = [];
        public List<UpdateAgentProfileDraftCommand> UpdateCommands { get; } = [];
        public List<UpsertAgentProfileSkillBindingCommand> UpsertCommands { get; } = [];
        public List<RemoveAgentProfileSkillBindingCommand> RemoveCommands { get; } = [];
        public List<PublishAgentProfileCommand> PublishCommands { get; } = [];
        public int DispatchCount =>
            CreateCommands.Count + UpdateCommands.Count + UpsertCommands.Count +
            RemoveCommands.Count + PublishCommands.Count;

        public AgentProfileActorTargets ResolveCreateTargets(string profileId)
        {
            ResolvedProfileIds.Add(profileId);
            return new AgentProfileActorTargets(
                "namespace-actor",
                $"profile-actor:{profileId}");
        }

        public Task<DispatchAdmission> DispatchCreateAsync(
            CreateAgentProfileCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreateCommands.Add(command.Clone());
            return Task.FromResult(Admission(command.Operation, "namespace-actor"));
        }

        public Task<DispatchAdmission> DispatchUpdateDraftAsync(
            UpdateAgentProfileDraftCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UpdateCommands.Add(command.Clone());
            return Task.FromResult(Admission(command.Operation, $"profile-actor:{command.Identity.ProfileId}"));
        }

        public Task<DispatchAdmission> DispatchUpsertSkillBindingAsync(
            UpsertAgentProfileSkillBindingCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UpsertCommands.Add(command.Clone());
            return Task.FromResult(Admission(command.Operation, $"profile-actor:{command.Identity.ProfileId}"));
        }

        public Task<DispatchAdmission> DispatchRemoveSkillBindingAsync(
            RemoveAgentProfileSkillBindingCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RemoveCommands.Add(command.Clone());
            return Task.FromResult(Admission(command.Operation, $"profile-actor:{command.Identity.ProfileId}"));
        }

        public Task<DispatchAdmission> DispatchPublishAsync(
            PublishAgentProfileCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PublishCommands.Add(command.Clone());
            return Task.FromResult(Admission(command.Operation, $"profile-actor:{command.Identity.ProfileId}"));
        }

        private DispatchAdmission Admission(AgentProfileOperationFact operation, string actorId) =>
            new(
                AcceptDispatch,
                operation.CommandId,
                DateTimeOffset.Parse("2026-07-23T01:02:03Z"),
                actorId,
                operation.CorrelationId);
    }

    private sealed class RecordingResolver(
        Func<ExactOrnnSkillReference, ExactOrnnSkillResolutionResult> resolve)
        : IExactOrnnSkillResolver
    {
        public int Calls { get; private set; }

        public Task<ExactOrnnSkillResolutionResult> ResolveAsync(
            string nyxIdAccessToken,
            ExactOrnnSkillReference reference,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            nyxIdAccessToken.Should().Be("token-alpha");
            Calls++;
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
