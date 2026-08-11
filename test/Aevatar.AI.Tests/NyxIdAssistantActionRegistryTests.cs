using System.Collections.Frozen;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdAssistantActionRegistryTests
{
    private const string LegacyRevision = "nyxid-assistant-actions.v4";
    private const string TransitionRevision = "nyxid-assistant-actions.v5";
    private const string LeastScopeRevision = "nyxid-assistant-actions.v6";
    private const string SupportedRevision = "nyxid-assistant-actions.v7";

    [Fact]
    public void RevisionContractSnapshots_ShouldPinExactDescriptorFingerprints()
    {
        var expected = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            StringComparer.Ordinal)
        {
            [LegacyRevision] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service.connect"] =
                    "20a4dc5fe13a30882a1f84085ace2d04a93081829ecb31e3c6a0f2bff94ec0a3",
            },
            [TransitionRevision] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service.connect"] =
                    "20a4dc5fe13a30882a1f84085ace2d04a93081829ecb31e3c6a0f2bff94ec0a3",
                ["service.reauthorize"] =
                    "b6a16985e083b1fa71ab99f0fcede9ae69415d9e71f7e078789bcaeadb8ff0b8",
                ["key.create"] =
                    "d5db2d5b1e34db1b8c727271f745c47c575947f027da9685bb76096f545c7975",
                ["key.rotate"] =
                    "e65c6d81a00bf980ad3ac63bb44f6cbe901da73f6d825a4545aacf0108cc4643",
            },
            [LeastScopeRevision] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service.connect"] =
                    "20a4dc5fe13a30882a1f84085ace2d04a93081829ecb31e3c6a0f2bff94ec0a3",
                ["key.create"] =
                    "ce94e23543aad2417260f25a07eac15369c007d14d77963daaed7b5730e98e07",
            },
            [SupportedRevision] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service.connect"] =
                    "20a4dc5fe13a30882a1f84085ace2d04a93081829ecb31e3c6a0f2bff94ec0a3",
                ["key.create"] =
                    "ce94e23543aad2417260f25a07eac15369c007d14d77963daaed7b5730e98e07",
                ["key.rotate"] =
                    "e65c6d81a00bf980ad3ac63bb44f6cbe901da73f6d825a4545aacf0108cc4643",
            },
        };

        foreach (var (revision, expectedDescriptors) in expected)
        {
            var snapshot = NyxIdAssistantActionRegistry.GetRevisionContractSnapshot(revision);

            snapshot.SchemaVersion.Should().Be(4);
            snapshot.UnknownDescriptorPolicy.Should()
                .Be(NyxIdAssistantActionUnknownDescriptorPolicy.Ignore);
            snapshot.Actions.Keys.Should().BeEquivalentTo(expectedDescriptors.Keys);
            snapshot.Actions.Should().OnlyContain(pair =>
                expectedDescriptors[pair.Key] == pair.Value.DescriptorFingerprint);
        }

        NyxIdAssistantActionRegistry.GetRevisionContractSnapshot(TransitionRevision)
            .Actions["key.create"].ParamsSchemaVariant.Should()
            .Be(NyxIdAssistantActionParamsSchemaVariant.RelaxedKeyCreate);
        foreach (var revision in new[] { LeastScopeRevision, SupportedRevision })
        {
            NyxIdAssistantActionRegistry.GetRevisionContractSnapshot(revision)
                .Actions["key.create"].ParamsSchemaVariant.Should()
                .Be(NyxIdAssistantActionParamsSchemaVariant.LeastScopeKeyCreate);
        }
    }

    [Theory]
    [InlineData("registry-v4.json", LegacyRevision)]
    [InlineData("registry-v5.json", TransitionRevision)]
    [InlineData("registry-v6.json", LeastScopeRevision)]
    [InlineData("registry-v7.json", SupportedRevision)]
    public void Load_ShouldAcceptExactRevisionFixture(
        string fixtureName,
        string expectedRevision)
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            ReadRegistryFixture(fixtureName));

        registry.SchemaVersion.Should().Be(4);
        registry.RegistryRevision.Should().Be(expectedRevision);
        registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("registry-v4.json", "service.connect")]
    [InlineData("registry-v5.json", "service.connect")]
    [InlineData("registry-v5.json", "service.reauthorize")]
    [InlineData("registry-v5.json", "key.create")]
    [InlineData("registry-v5.json", "key.rotate")]
    [InlineData("registry-v6.json", "service.connect")]
    [InlineData("registry-v6.json", "key.create")]
    [InlineData("registry-v7.json", "service.connect")]
    [InlineData("registry-v7.json", "key.create")]
    [InlineData("registry-v7.json", "key.rotate")]
    public void Load_ShouldRejectFixtureMissingAnyPinnedDescriptor(
        string fixtureName,
        string missingAction)
    {
        var root = JsonNode.Parse(ReadRegistryFixture(fixtureName))!.AsObject();
        var actions = root["actions"]!.AsArray();
        var descriptor = actions.Single(node =>
            node!["action"]!.GetValue<string>() == missingAction);
        actions.Remove(descriptor);

        Action load = () => NyxIdAssistantActionRegistry.Load(root.ToJsonString());

        load.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");
    }

    [Theory]
    [InlineData("action")]
    [InlineData("risk")]
    [InlineData("tier")]
    public void Load_ShouldRejectNonCanonicalPinnedDescriptorStrings(string fieldName)
    {
        var root = JsonNode.Parse(ReadRegistryFixture("registry-v4.json"))!
            .AsObject();
        var descriptor = root["actions"]!.AsArray().Single()!.AsObject();
        var value = descriptor[fieldName]!.GetValue<string>();
        descriptor[fieldName] = $" {value} ";

        Action load = () => NyxIdAssistantActionRegistry.Load(root.ToJsonString());

        load.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");
    }

    [Fact]
    public void Load_ShouldIgnoreRevisionForeignDescriptorInV7Fixture()
    {
        var target = JsonNode.Parse(ReadRegistryFixture("registry-v7.json"))!
            .AsObject();
        var transition = JsonNode.Parse(ReadRegistryFixture("registry-v5.json"))!
            .AsObject();
        var reauthorize = transition["actions"]!.AsArray().Single(node =>
            node!["action"]!.GetValue<string>() == "service.reauthorize");
        target["actions"]!.AsArray().Add(reauthorize!.DeepClone());

        var registry = NyxIdAssistantActionRegistry.Load(target.ToJsonString());

        registry.RegistryRevision.Should().Be(SupportedRevision);
        registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
        registry.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
        registry.CapabilityReadiness.Should().NotContainKey("service.reauthorize");
    }

    [Theory]
    [InlineData((int)NyxIdAssistantActionCapabilityKind.RequestProducer)]
    [InlineData((int)NyxIdAssistantActionCapabilityKind.AdmissionParser)]
    [InlineData((int)NyxIdAssistantActionCapabilityKind.AGUIMapper)]
    [InlineData((int)NyxIdAssistantActionCapabilityKind.SafeResourcePredicate)]
    [InlineData((int)NyxIdAssistantActionCapabilityKind.PostconditionVerifier)]
    [InlineData((int)NyxIdAssistantActionCapabilityKind.EvidencePredicate)]
    [InlineData((int)NyxIdAssistantActionCapabilityKind.AuthorityResolver)]
    [InlineData((int)NyxIdAssistantActionCapabilityKind.RetryGenerationPolicy)]
    public void ExecutableComposition_ShouldRequireEveryCapabilityRegistration(
        int missingCapabilityValue)
    {
        var missingCapability =
            (NyxIdAssistantActionCapabilityKind)missingCapabilityValue;
        var complete = NyxIdAssistantActionCapabilityRegistrations.Current;
        var ready = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation(),
            complete);
        ready.TryGetDefinition("service.connect", out _).Should().BeTrue();

        var incomplete = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation(),
            complete.Without(
                NyxIdAssistantActionKind.ServiceConnect,
                missingCapability));

        incomplete.TryGetDefinition("service.connect", out _).Should().BeFalse();
        Action validate = () => incomplete.ValidateRequest(
            "service.connect",
            """{"catalogService":{"serviceSlug":"api-github"}}""");
        validate.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
        Action resolve = () => incomplete.ResolveCatalogServiceConnect("api-github");
        resolve.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
    }

    [Fact]
    public void ExecutableComposition_ShouldRejectWrongParserParamsCaseRegistration()
    {
        var incompatible = NyxIdAssistantActionCapabilityRegistrations.Current
            .With(new NyxIdAssistantActionAdmissionParserRegistration(
                NyxIdAssistantActionKind.ServiceConnect,
                new[] { NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate }
                    .ToFrozenSet(),
                new[] { NyxIdAssistantActionParamsSchemaVariant.ServiceConnect }
                    .ToFrozenSet(),
                NyxIdAssistantActionRegistry.ParseServiceConnect,
                new[]
                {
                    KeyValuePair.Create(
                        NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate,
                        "{\"name\":\"agent-alpha\",\"platform\":\"codex\",\"allowedServiceIds\":[\"us-github-alpha\"]}"),
                }.ToFrozenDictionary()));

        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation(),
            incompatible);

        registry.TryGetDefinition("service.connect", out _).Should().BeFalse();
        Action validate = () => registry.ValidateRequest(
            "service.connect",
            """{"catalogService":{"serviceSlug":"api-github"}}""");
        validate.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
    }

    [Fact]
    public void ExecutableComposition_ShouldRejectWrongSafeResourceRegistration()
    {
        var incompatible = NyxIdAssistantActionCapabilityRegistrations.Current
            .With(new NyxIdAssistantActionSafeResourcePredicateRegistration(
                NyxIdAssistantActionKind.ServiceConnect,
                NyxIdChatSafeResourceRef.ResourceOneofCase.Key,
                NyxIdChatBrowserActions.ResourceMatchesAction));

        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation(),
            incompatible);

        registry.TryGetDefinition("service.connect", out _).Should().BeFalse();
        Action resolve = () => registry.ResolveCatalogServiceConnect("api-github");
        resolve.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
    }

    [Fact]
    public void ExecutableComposition_ShouldRejectUnrelatedImplementationWhenSemanticFieldsMatch()
    {
        var unrelated = NyxIdAssistantActionCapabilityRegistrations.Current
            .With(new NyxIdAssistantActionSafeResourcePredicateRegistration(
                NyxIdAssistantActionKind.ServiceConnect,
                NyxIdChatSafeResourceRef.ResourceOneofCase.UserService,
                static (_, _, _) => false));

        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation(),
            unrelated);

        registry.TryGetDefinition("service.connect", out _).Should().BeFalse();
        registry.CapabilityReadiness["service.connect"].MissingCapabilities.Should()
            .Contain(NyxIdAssistantActionCapabilityKind.SafeResourcePredicate);
    }

    [Fact]
    public void ExecutableComposition_ShouldRejectUnrelatedPostconditionVerifierCallable()
    {
        var unrelated = NyxIdAssistantActionCapabilityRegistrations.Current
            .With(new NyxIdAssistantActionPostconditionVerifierRegistration(
                NyxIdAssistantActionKind.ServiceConnect,
                NyxIdAssistantActionEvidenceStrategy.UserServiceCurrentState,
                NyxIdActionPostconditionPort.VerifyKeyCreatePostconditionAsync,
                ServiceConnectPostconditionProbe()));

        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation(),
            unrelated);

        registry.CapabilityReadiness["service.connect"].MissingCapabilities.Should()
            .Equal(NyxIdAssistantActionCapabilityKind.PostconditionVerifier);
    }

    [Fact]
    public void ExecutableComposition_ShouldRejectUnrelatedEvidencePredicateCallable()
    {
        var unrelated = NyxIdAssistantActionCapabilityRegistrations.Current
            .With(new NyxIdAssistantActionEvidencePredicateRegistration<
                NyxIdServiceConnectEvidenceExpectation,
                NyxIdAuthorizationServiceEvidence>(
                NyxIdAssistantActionKind.ServiceConnect,
                NyxIdAssistantActionEvidenceStrategy.UserServiceCurrentState,
                static (_, _) => false,
                [
                    new(
                        new NyxIdServiceConnectEvidenceExpectation(
                            ServiceConnectPostconditionProbe().Params,
                            null),
                        new NyxIdAuthorizationServiceEvidence
                        {
                            UserServiceId = "us-probe",
                            ServiceSlug = "api-probe",
                            Access = NyxIdAuthorizationAccess.Permitted,
                        },
                        new NyxIdAuthorizationServiceEvidence
                        {
                            UserServiceId = "us-probe",
                            ServiceSlug = "api-other",
                            Access = NyxIdAuthorizationAccess.Permitted,
                        }),
                ]));

        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation(),
            unrelated);

        registry.CapabilityReadiness["service.connect"].MissingCapabilities.Should()
            .Equal(NyxIdAssistantActionCapabilityKind.EvidencePredicate);
    }

    [Fact]
    public void ExecutableComposition_ShouldRejectProducerActionWireActionMismatch()
    {
        NyxIdAssistantActionSemanticContracts.TryGet(
                NyxIdAssistantActionKind.ServiceConnect,
                out var semantic)
            .Should().BeTrue();
        var mismatched = semantic with { WireAction = "key.create" };
        var descriptor = NyxIdAssistantActionRegistry
            .GetRevisionContractSnapshot(LegacyRevision)
            .Actions["service.connect"];

        var missing = NyxIdAssistantActionCapabilityRegistrations.Current
            .MissingCapabilities(mismatched, descriptor);

        missing.Should().Contain(NyxIdAssistantActionCapabilityKind.RequestProducer);
    }

    [Fact]
    public void ExecutableComposition_ShouldRejectParserThatReturnsWrongParamsCase()
    {
        var incompatible = NyxIdAssistantActionCapabilityRegistrations.Current
            .With(new NyxIdAssistantActionAdmissionParserRegistration(
                NyxIdAssistantActionKind.ServiceConnect,
                new[]
                {
                    NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect,
                    NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect,
                }.ToFrozenSet(),
                new[] { NyxIdAssistantActionParamsSchemaVariant.ServiceConnect }
                    .ToFrozenSet(),
                static _ => new NyxIdAssistantActionParams
                {
                    KeyCreate = new NyxIdKeyCreateParams
                    {
                        Name = "agent-alpha",
                        Platform = "codex",
                        AllowedServiceIds = { "us-github-alpha" },
                    },
                },
                new[]
                {
                    KeyValuePair.Create(
                        NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect,
                        "{\"catalogService\":{\"serviceSlug\":\"api-github\"}}"),
                    KeyValuePair.Create(
                        NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect,
                        "{\"customService\":{\"name\":\"Internal API\",\"endpointUrl\":\"https://api.internal.example.com\",\"authMethod\":\"none\"}}"),
                }.ToFrozenDictionary()));

        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation(),
            incompatible);

        registry.TryGetDefinition("service.connect", out _).Should().BeFalse();
        registry.CapabilityReadiness["service.connect"].MissingCapabilities.Should()
            .Contain(NyxIdAssistantActionCapabilityKind.AdmissionParser);
    }

    [Fact]
    public void Load_ShouldPinSchemaVersionAndRevision()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation());

        registry.SchemaVersion.Should().Be(4);
        registry.RegistryRevision.Should().Be(SupportedRevision);

        var legacy = NyxIdAssistantActionRegistry.Load(
            RegistryJson(revision: LegacyRevision));
        legacy.RegistryRevision.Should().Be(LegacyRevision);
        legacy.TryGetDefinition("service.connect", out _).Should().BeTrue();

        Action act = () => NyxIdAssistantActionRegistry.Load(
            RegistryJson(schemaVersion: 3));
        act.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_SCHEMA_UNSUPPORTED");

        act = () => NyxIdAssistantActionRegistry.Load(
            RegistryJson(revision: "nyxid-assistant-actions.future"));
        act.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_REVISION_UNSUPPORTED");

        act = () => NyxIdAssistantActionRegistry.Load(
            RegistryJson(revision: SupportedRevision));
        act.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");
    }

    [Fact]
    public void Load_ShouldIgnoreUnknownActionWhenExecutableActionsArePresent()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithUnknownAction());

        registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
        registry.TryGetDefinition("workflow.launch", out _).Should().BeFalse();
        Action validate = () => registry.ValidateRequest("workflow.launch", "{}");
        validate.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
        NyxIdAssistantActionRegistry.IsActionExecutable(
                "nyxid-assistant-actions.future",
                NyxIdAssistantActionKind.ServiceConnect)
            .Should().BeFalse();
    }

    [Fact]
    public void Load_ShouldPinWaveOneSchemasAndKeepUnimplementedActionsClosed()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions());

        registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
        registry.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
        registry.TryGetDefinition("key.create", out _).Should().BeFalse();
        registry.TryGetDefinition("key.rotate", out _).Should().BeFalse();
        registry.CapabilityReadiness["key.create"].MissingCapabilities.Should()
            .Contain(NyxIdAssistantActionCapabilityKind.AdmissionParser);

        var legacy = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions(revision: LegacyRevision));
        legacy.TryGetDefinition("service.connect", out _).Should().BeTrue();
        legacy.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
        legacy.TryGetDefinition("key.create", out _).Should().BeFalse();
        legacy.TryGetDefinition("key.rotate", out _).Should().BeFalse();

        Action staleReauthorizeSchema = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions(
                serviceReauthorizeSchema: StaleServiceReauthorizeSchema));
        staleReauthorizeSchema.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");

        Action relaxedKeyCreateSchema = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions(
                keyCreateSchema: RelaxedKeyCreateSchema));
        relaxedKeyCreateSchema.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");

        Action callerRememberPolicy = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions(keyCreateRememberEligible: true));
        callerRememberPolicy.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");

        Action rememberedReauthorization = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions(serviceReauthorizeRememberEligible: true));
        rememberedReauthorization.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");
    }

    [Fact]
    public void Load_ShouldKeepLeastScopeKeyCreateClosedWithoutCompleteCapabilities()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithLeastScopeKeyCreate());

        registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
        registry.TryGetDefinition("key.create", out _).Should().BeFalse();
        registry.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
        registry.TryGetDefinition("key.rotate", out _).Should().BeFalse();
        NyxIdAssistantActionRegistry.IsActionExecutable(
                LeastScopeRevision,
                NyxIdAssistantActionKind.KeyCreate)
            .Should().BeFalse();
        NyxIdAssistantActionRegistry.IsActionExecutable(
                TransitionRevision,
                NyxIdAssistantActionKind.KeyCreate)
            .Should().BeFalse();
        Action resolve = () => registry.ResolveKeyCreate(
            new NyxIdKeyCreateActionRequirement
            {
                Name = "agent-alpha",
                Platform = "codex",
                AllowedServiceIds = { "us-github-alpha" },
            });
        resolve.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");

        Action staleSchema = () => NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithLeastScopeKeyCreate(KeyCreateSchema));
        staleSchema.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_REGISTRY_INVALID");
    }

    [Fact]
    public void Load_ShouldKeepKeyRotationClosedWithoutCompleteCapabilities()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithKeyRotation());

        registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
        registry.TryGetDefinition("key.create", out _).Should().BeFalse();
        registry.TryGetDefinition("key.rotate", out _).Should().BeFalse();
        registry.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
        NyxIdAssistantActionRegistry.IsActionExecutable(
                SupportedRevision,
                NyxIdAssistantActionKind.KeyRotate)
            .Should().BeFalse();
        NyxIdAssistantActionRegistry.IsActionExecutable(
                LeastScopeRevision,
                NyxIdAssistantActionKind.KeyRotate)
            .Should().BeFalse();
        Action resolveCreate = () => registry.ResolveKeyCreate(
            new NyxIdKeyCreateActionRequirement
            {
                Name = "agent-alpha",
                Platform = "codex",
                AllowedServiceIds = { "us-github-alpha" },
            });
        resolveCreate.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
        Action resolveRotate = () => registry.ResolveKeyRotate(
            new NyxIdKeyRotateActionRequirement { KeyId = "key-alpha" });
        resolveRotate.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
    }

    [Fact]
    public void Load_ShouldRejectUnsupportedTierForKnownAction()
    {
        Action v2 = () => NyxIdAssistantActionRegistry.Load(
            RegistryJson(tier: "v2"));
        v2.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_TIER_UNSUPPORTED");
    }

    [Fact]
    public void ValidateRequest_ShouldKeepCatalogAndCustomServiceConnectDistinct()
    {
        var registry = NyxIdAssistantActionRegistry.Load(RegistryJson());

        var catalog = registry.ValidateRequest(
            "service.connect",
            """{"catalogService":{"serviceSlug":"api-github","requestedScopes":["repo"]}}""");
        var custom = registry.ValidateRequest(
            "service.connect",
            """{"customService":{"name":"Internal API","endpointUrl":"https://api.internal.example.com","authMethod":"bearer","authKeyName":"X-Api-Key"}}""");

        catalog.Params.ParamsCase.Should().Be(
            NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect);
        catalog.Params.CatalogServiceConnect.ServiceSlug.Should().Be("api-github");
        catalog.Params.CatalogServiceConnect.RequestedScopes.Should().Equal("repo");
        custom.Params.ParamsCase.Should().Be(
            NyxIdAssistantActionParams.ParamsOneofCase.CustomServiceConnect);
        custom.Params.CustomServiceConnect.EndpointUrl.Should().Be(
            "https://api.internal.example.com/");
        catalog.Definition.AdvisoryRisk.Should().Be(NyxIdAssistantActionRisk.Grant);
        catalog.Definition.RememberEligible.Should().BeTrue();
    }

    [Fact]
    public void ValidateRequest_ShouldRejectUndeclaredFieldsAndVariantAmbiguity()
    {
        var registry = NyxIdAssistantActionRegistry.Load(RegistryJson());

        Action undeclared = () => registry.ValidateRequest(
            "service.connect",
            """{"catalogService":{"serviceSlug":"api-github","displayLabel":"GitHub"}}""");
        undeclared.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");

        Action ambiguous = () => registry.ValidateRequest(
            "service.connect",
            """{"catalogService":{"serviceSlug":"api-github"},"customService":{"name":"Internal API","endpointUrl":"https://api.internal.example.com","authMethod":"none"}}""");
        ambiguous.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");
    }

    [Fact]
    public void ValidateRequest_ShouldRejectCallerOwnedRiskOrRememberPolicy()
    {
        var registry = NyxIdAssistantActionRegistry.Load(RegistryJson());

        Action risk = () => registry.ValidateRequest(
            "service.connect",
            """{"catalogService":{"serviceSlug":"api-github"}}""",
            callerRisk: "low");
        risk.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_POLICY_CALLER_OWNED");

        Action remember = () => registry.ValidateRequest(
            "service.connect",
            """{"catalogService":{"serviceSlug":"api-github"}}""",
            callerRememberEligible: false);
        remember.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_POLICY_CALLER_OWNED");
    }

    [Fact]
    public void RegistrySnapshot_ShouldNotExposeHotReloadOrDeviceUserCodeAction()
    {
        var registry = NyxIdAssistantActionRegistry.Load(RegistryJson());

        registry.TryGetDefinition("device.approve.user_code", out _).Should().BeFalse();
        registry.TryGetDefinition("device.approve", out _).Should().BeFalse();
        typeof(NyxIdAssistantActionRegistry).GetMethods()
            .Select(static method => method.Name)
            .Should().NotContain(name =>
                name.Contains("Reload", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Refresh", StringComparison.OrdinalIgnoreCase));
        typeof(NyxIdAssistantActionRegistry)
            .GetFields(System.Reflection.BindingFlags.NonPublic |
                       System.Reflection.BindingFlags.Static |
                       System.Reflection.BindingFlags.Instance)
            .Select(static field => field.Name)
            .Should().NotContain("ExecutableActionsByRevision");
    }

    [Theory]
    [InlineData("token")]
    [InlineData("access_token")]
    [InlineData("authorization")]
    [InlineData("cookie")]
    [InlineData("secret")]
    [InlineData("clientSecret")]
    [InlineData("password")]
    [InlineData("user_code")]
    [InlineData("deviceCode")]
    [InlineData("raw_upstream_body")]
    public void SecretPolicy_ShouldRejectForbiddenFieldNames(string fieldName)
    {
        Action act = () => NyxIdActionSecretPolicy.ValidateParamsJson(
            "{\"catalogService\":{\"serviceSlug\":\"api-github\",\"" +
            fieldName +
            "\":\"secret-alpha\"}}");

        act.Should().Throw<NyxIdActionSecretPolicyException>()
            .Which.Code.Should().Be("NYXID_ACTION_SECRET_FIELD_FORBIDDEN");
    }

    [Theory]
    [InlineData("https://user:password@example.com/path")]
    [InlineData("https://example.com/path?token=secret-alpha")]
    [InlineData("https://example.com/path#secret-alpha")]
    [InlineData("ftp://example.com/path")]
    [InlineData("/relative/path")]
    public void SecretPolicy_ShouldRejectUnsafeUrls(string value)
    {
        Action act = () => NyxIdActionSecretPolicy.NormalizeSafeUrl(value);

        act.Should().Throw<NyxIdActionSecretPolicyException>()
            .Which.Code.Should().Be("NYXID_ACTION_URL_UNSAFE");
    }

    [Fact]
    public void ValidateRequest_ShouldRejectManifestOnlyActionWithoutProducerAndPostconditionPolicy()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithManifestOnlyAction());

        Action act = () => registry.ValidateRequest(
            "developer_app.create",
            """{"name":"My App","redirectUris":["https://app.example.com/cb"]}""");

        act.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
        registry.TryGetDefinition("developer_app.create", out _).Should().BeFalse();
    }

    [Fact]
    public void ParseServiceReauthorize_ShouldRequireExactUserServiceIdentity()
    {
        using var valid = JsonDocument.Parse(
            """{"userServiceId":"us-github-alpha","requestedScopes":["repo","read:org"]}""");

        var parsed = NyxIdAssistantActionRegistry.ParseServiceReauthorize(valid.RootElement);

        parsed.ParamsCase.Should().Be(
            NyxIdAssistantActionParams.ParamsOneofCase.ServiceReauthorize);
        parsed.ServiceReauthorize.UserServiceId.Should().Be("us-github-alpha");
        parsed.ServiceReauthorize.RequestedScopes.Should().Equal("repo", "read:org");

        using var obsolete = JsonDocument.Parse(
            """{"keyId":"key-alpha","requestedScopes":["repo"]}""");
        Action parseObsolete = () =>
            NyxIdAssistantActionRegistry.ParseServiceReauthorize(obsolete.RootElement);
        parseObsolete.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");
    }

    [Fact]
    public void ParseKeyCreate_ShouldRequireAtLeastOneExactAllowedServiceIdentity()
    {
        using var valid = JsonDocument.Parse(
            """{"name":"agent-alpha","platform":"codex","allowedServiceIds":["us-github-alpha"]}""");

        var parsed = NyxIdAssistantActionRegistry.ParseKeyCreate(valid.RootElement);

        parsed.ParamsCase.Should().Be(NyxIdAssistantActionParams.ParamsOneofCase.KeyCreate);
        parsed.KeyCreate.Name.Should().Be("agent-alpha");
        parsed.KeyCreate.Platform.Should().Be("codex");
        parsed.KeyCreate.AllowedServiceIds.Should().Equal("us-github-alpha");

        using var missingServices = JsonDocument.Parse(
            """{"name":"agent-alpha","platform":"codex"}""");
        Action parseMissingServices = () =>
            NyxIdAssistantActionRegistry.ParseKeyCreate(missingServices.RootElement);
        parseMissingServices.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");

        using var allServices = JsonDocument.Parse(
            """{"name":"agent-alpha","platform":"codex","allowedServiceIds":[]}""");
        Action parseAllServices = () =>
            NyxIdAssistantActionRegistry.ParseKeyCreate(allServices.RootElement);
        parseAllServices.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");

        using var duplicateServices = JsonDocument.Parse(
            """{"name":"agent-alpha","platform":"codex","allowedServiceIds":["us-github-alpha","us-github-alpha"]}""");
        Action parseDuplicates = () =>
            NyxIdAssistantActionRegistry.ParseKeyCreate(duplicateServices.RootElement);
        parseDuplicates.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");

        using var nonCanonicalService = JsonDocument.Parse(
            """{"name":"agent-alpha","platform":"codex","allowedServiceIds":[" us-github-alpha"]}""");
        Action parseNonCanonical = () =>
            NyxIdAssistantActionRegistry.ParseKeyCreate(nonCanonicalService.RootElement);
        parseNonCanonical.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");

        var overLimitJson = JsonSerializer.Serialize(new
        {
            name = "agent-alpha",
            platform = "codex",
            allowedServiceIds = Enumerable.Range(0, 65).Select(index => $"us-{index}"),
        });
        using var overLimitServices = JsonDocument.Parse(overLimitJson);
        Action parseOverLimit = () =>
            NyxIdAssistantActionRegistry.ParseKeyCreate(overLimitServices.RootElement);
        parseOverLimit.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_PARAMS_INVALID");
    }

    [Fact]
    public void ServiceReauthorize_ShouldRemainFailClosedAtExecutableGate()
    {
        var registry = NyxIdAssistantActionRegistry.Load(
            RegistryJsonWithWaveOneActions());

        registry.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
        Action validate = () => registry.ValidateRequest(
            "service.reauthorize",
            """{"userServiceId":"us-github-alpha","requestedScopes":["repo"]}""");
        validate.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
    }

    [Fact]
    public void StartupSnapshot_ShouldInitializeExactlyOnceAndFailBeforeInitialization()
    {
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();

        Action beforeStartup = () => snapshot.GetRequired();
        beforeStartup.Should().Throw<InvalidOperationException>()
            .WithMessage("*startup snapshot*");

        var registry = NyxIdAssistantActionRegistry.Load(RegistryJson());
        snapshot.Initialize(registry);

        snapshot.GetRequired().Should().BeSameAs(registry);
        Action replacement = () => snapshot.Initialize(
            NyxIdAssistantActionRegistry.Load(RegistryJson()));
        replacement.Should().Throw<InvalidOperationException>()
            .WithMessage("*already initialized*");
    }

    [Fact]
    public async Task StartupService_ShouldFetchAndValidateRegistryOnce()
    {
        foreach (var (payload, revision) in new[]
                 {
                     (RegistryJson(), LegacyRevision),
                     (RegistryJsonWithWaveOneActions(), TransitionRevision),
                     (RegistryJsonWithLeastScopeKeyCreate(), LeastScopeRevision),
                     (RegistryJsonWithKeyRotation(), SupportedRevision),
                 })
        {
            var source = new RecordingRegistrySource(payload);
            var snapshot = new NyxIdAssistantActionRegistrySnapshot();
            var service = new NyxIdAssistantActionRegistryStartupService(
                source,
                snapshot,
                new NyxIdAssistantActionsOptions
                {
                    Enabled = true,
                });

            await service.StartAsync(CancellationToken.None);

            source.FetchCount.Should().Be(1);
            snapshot.GetRequired().SchemaVersion.Should().Be(4);
            snapshot.GetRequired().RegistryRevision.Should().Be(revision);
            var readiness = snapshot.GetReadinessRequired();
            readiness.Status.Should().Be(
                revision == LegacyRevision
                    ? NyxIdAssistantActionRegistryReadinessStatus.Ready
                    : NyxIdAssistantActionRegistryReadinessStatus.Partial);
            readiness.RegistryRevision.Should().Be(revision);
            readiness.Actions["service.connect"].Executable.Should().BeTrue();
            if (revision is LeastScopeRevision or SupportedRevision)
            {
                readiness.Actions["key.create"].MissingCapabilities.Should().BeEquivalentTo([
                    NyxIdAssistantActionCapabilityKind.AuthorityResolver,
                    NyxIdAssistantActionCapabilityKind.RetryGenerationPolicy,
                ]);
            }
            if (revision == SupportedRevision)
            {
                readiness.Actions["key.rotate"].MissingCapabilities.Should().BeEquivalentTo([
                    NyxIdAssistantActionCapabilityKind.AuthorityResolver,
                    NyxIdAssistantActionCapabilityKind.RetryGenerationPolicy,
                ]);
            }
            await service.StopAsync(CancellationToken.None);
            source.FetchCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task StartupService_WhenFetchFailsAndOptional_ShouldPublishUnavailableRegistry()
    {
        var source = new ThrowingRegistrySource(
            new HttpRequestException("registry unavailable"));
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();
        var service = new NyxIdAssistantActionRegistryStartupService(
            source,
            snapshot,
            new NyxIdAssistantActionsOptions
            {
                Enabled = true,
                Required = false,
            });

        await service.StartAsync(CancellationToken.None);

        snapshot.GetRequired().TryGetDefinition("service.connect", out _)
            .Should().BeFalse();
        var readiness = snapshot.GetReadinessRequired();
        readiness.Status.Should().Be(
            NyxIdAssistantActionRegistryReadinessStatus.Unavailable);
        readiness.Actions.Should().BeEmpty();
        readiness.FailureCode.Should().Be("NYXID_ACTION_REGISTRY_UNAVAILABLE");
    }

    [Fact]
    public async Task StartupService_WhenRegistryIsInvalidAndOptional_ShouldPublishUnavailableRegistry()
    {
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();
        var service = new NyxIdAssistantActionRegistryStartupService(
            new RecordingRegistrySource("{}"),
            snapshot,
            new NyxIdAssistantActionsOptions
            {
                Enabled = true,
                Required = false,
            });

        await service.StartAsync(CancellationToken.None);

        snapshot.GetRequired().TryGetDefinition("service.connect", out _)
            .Should().BeFalse();
        var readiness = snapshot.GetReadinessRequired();
        readiness.Status.Should().Be(
            NyxIdAssistantActionRegistryReadinessStatus.Unavailable);
        readiness.FailureCode.Should().Be("NYXID_ACTION_REGISTRY_INVALID");
    }

    [Fact]
    public async Task StartupService_WhenRegistryViolatesSecretPolicyAndOptional_ShouldPreserveFailureCode()
    {
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();
        var service = new NyxIdAssistantActionRegistryStartupService(
            new RecordingRegistrySource(RegistryJsonWithForbiddenSchemaField()),
            snapshot,
            new NyxIdAssistantActionsOptions
            {
                Enabled = true,
                Required = false,
            });

        await service.StartAsync(CancellationToken.None);

        var readiness = snapshot.GetReadinessRequired();
        readiness.Status.Should().Be(
            NyxIdAssistantActionRegistryReadinessStatus.Unavailable);
        readiness.FailureCode.Should().Be(NyxIdActionSecretPolicy.ForbiddenFieldCode);
    }

    [Fact]
    public async Task StartupService_WhenRegistryViolatesSecretPolicyAndRequired_ShouldPreserveFailureCodeAndFailStartup()
    {
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();
        var service = new NyxIdAssistantActionRegistryStartupService(
            new RecordingRegistrySource(RegistryJsonWithForbiddenSchemaField()),
            snapshot,
            new NyxIdAssistantActionsOptions
            {
                Enabled = true,
                Required = true,
            });

        Func<Task> start = () => service.StartAsync(CancellationToken.None);

        (await start.Should().ThrowAsync<NyxIdActionSecretPolicyException>())
            .Which.Code.Should().Be(NyxIdActionSecretPolicy.ForbiddenFieldCode);
        snapshot.GetReadinessRequired().FailureCode.Should()
            .Be(NyxIdActionSecretPolicy.ForbiddenFieldCode);
    }

    [Fact]
    public async Task StartupService_WhenFetchFailsAndRequired_ShouldFailStartup()
    {
        var failure = new HttpRequestException("registry unavailable");
        var snapshot = new NyxIdAssistantActionRegistrySnapshot();
        var service = new NyxIdAssistantActionRegistryStartupService(
            new ThrowingRegistrySource(failure),
            snapshot,
            new NyxIdAssistantActionsOptions
            {
                Enabled = true,
                Required = true,
            });

        Func<Task> start = () => service.StartAsync(CancellationToken.None);

        (await start.Should().ThrowAsync<HttpRequestException>())
            .Which.Should().BeSameAs(failure);
        snapshot.GetReadinessRequired().Status.Should().Be(
            NyxIdAssistantActionRegistryReadinessStatus.Unavailable);
    }

    [Fact]
    public async Task HttpSource_ShouldFetchCanonicalPublicRouteWithoutCredentials()
    {
        var handler = new RecordingHandler(RegistryJsonWithWaveOneActions());
        var client = new HttpClient(handler);
        var source = new NyxIdAssistantActionRegistryHttpSource(
            new StubHttpClientFactory(client),
            new NyxIdToolOptions { BaseUrl = "https://nyxid.example.test/" });

        var json = await source.FetchAsync(CancellationToken.None);

        json.Should().Contain(TransitionRevision);
        handler.Requests.Should().ContainSingle();
        handler.Requests.Single().Method.Should().Be(HttpMethod.Get);
        handler.Requests.Single().RequestUri.Should().Be(
            new Uri("https://nyxid.example.test/api/v1/assistant/actions"));
        handler.Requests.Single().Headers.Authorization.Should().BeNull();
    }

    private static string RegistryJson(
        int schemaVersion = 4,
        string revision = LegacyRevision,
        string action = "service.connect",
        string tier = "v1",
        string paramsSchema = ServiceConnectSchema,
        string risk = "grant",
        bool rememberEligible = true) => $$"""
        {
          "schema_version": {{schemaVersion}},
          "revision": "{{revision}}",
          "actions": [
            {
              "action": "{{action}}",
              "description": "Complete the browser-owned NyxID journey.",
              "params_schema": {{paramsSchema}},
              "risk": "{{risk}}",
              "tier": "{{tier}}",
              "remember_eligible": {{rememberEligible.ToString().ToLowerInvariant()}}
            }
          ]
        }
        """;

    private static string RegistryJsonWithForbiddenSchemaField() =>
        RegistryJson(
            paramsSchema: ServiceConnectSchema.Replace(
                "\"serviceSlug\": {\"type\": \"string\"},",
                "\"serviceSlug\": {\"type\": \"string\"}, \"password\": {\"type\": \"string\"},",
                StringComparison.Ordinal));

    private const string ServiceConnectSchema = """
        {
          "oneOf": [
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["catalogService"],
              "properties": {
                "catalogService": {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["serviceSlug"],
                  "properties": {
                    "serviceSlug": {"type": "string"},
                    "requestedScopes": {"type": "array", "items": {"type": "string"}},
                    "viaNodeId": {"type": "string"},
                    "targetOrgId": {"type": "string"}
                  }
                }
              }
            },
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["customService"],
              "properties": {
                "customService": {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["name", "endpointUrl", "authMethod"],
                  "properties": {
                    "name": {"type": "string"},
                    "endpointUrl": {"type": "string"},
                    "authMethod": {"type": "string"},
                    "authKeyName": {"type": "string"},
                    "viaNodeId": {"type": "string"},
                    "targetOrgId": {"type": "string"}
                  }
                }
              }
            }
          ]
        }
        """;

    private const string DeveloperAppSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "redirectUris"],
          "properties": {
            "name": {"type": "string"},
            "redirectUris": {"type": "array", "items": {"type": "string"}}
          }
        }
        """;

    private const string ServiceReauthorizeSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["userServiceId", "requestedScopes"],
          "properties": {
            "userServiceId": {"type": "string"},
            "requestedScopes": {"type": "array", "items": {"type": "string"}}
          }
        }
        """;

    private const string StaleServiceReauthorizeSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["userServiceId"],
          "properties": {
            "userServiceId": {"type": "string"},
            "requestedScopes": {"type": "array", "items": {"type": "string"}}
          }
        }
        """;

    private const string KeyCreateSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "platform", "allowedServiceIds"],
          "properties": {
            "name": {"type": "string"},
            "platform": {"type": "string"},
            "allowedServiceIds": {"type": "array", "items": {"type": "string"}}
          }
        }
        """;

    private const string LeastScopeKeyCreateSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "platform", "allowedServiceIds"],
          "properties": {
            "name": {"type": "string"},
            "platform": {"type": "string"},
            "allowedServiceIds": {
              "type": "array",
              "minItems": 1,
              "maxItems": 64,
              "uniqueItems": true,
              "items": {"type": "string"}
            }
          }
        }
        """;

    private const string RelaxedKeyCreateSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "platform"],
          "properties": {
            "name": {"type": "string"},
            "platform": {"type": "string"},
            "allowedServiceIds": {"type": "array", "items": {"type": "string"}}
          }
        }
        """;

    private const string KeyRotateSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["keyId"],
          "properties": {
            "keyId": {"type": "string"}
          }
        }
        """;

    private static string RegistryJsonWithWaveOneActions(
        string serviceReauthorizeSchema = ServiceReauthorizeSchema,
        string keyCreateSchema = KeyCreateSchema,
        bool keyCreateRememberEligible = false,
        bool serviceReauthorizeRememberEligible = false,
        string revision = TransitionRevision) => $$"""
        {
          "schema_version": 4,
          "revision": "{{revision}}",
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a service.",
              "params_schema": {{ServiceConnectSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
            },
            {
              "action": "service.reauthorize",
              "description": "Reauthorize a connected service.",
              "params_schema": {{serviceReauthorizeSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": {{serviceReauthorizeRememberEligible.ToString().ToLowerInvariant()}}
            },
            {
              "action": "key.create",
              "description": "Create a scoped API key.",
              "params_schema": {{keyCreateSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": {{keyCreateRememberEligible.ToString().ToLowerInvariant()}}
            },
            {
              "action": "key.rotate",
              "description": "Rotate an API key.",
              "params_schema": {{KeyRotateSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            }
          ]
        }
        """;

    private static string RegistryJsonWithLeastScopeKeyCreate(
        string keyCreateSchema = LeastScopeKeyCreateSchema) => $$"""
        {
          "schema_version": 4,
          "revision": "{{LeastScopeRevision}}",
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a service.",
              "params_schema": {{ServiceConnectSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
            },
            {
              "action": "key.create",
              "description": "Create a least-scope API key.",
              "params_schema": {{keyCreateSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            }
          ]
        }
        """;

    private static string RegistryJsonWithKeyRotation() => $$"""
        {
          "schema_version": 4,
          "revision": "{{SupportedRevision}}",
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a service.",
              "params_schema": {{ServiceConnectSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
            },
            {
              "action": "key.create",
              "description": "Create a least-scope API key.",
              "params_schema": {{LeastScopeKeyCreateSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            },
            {
              "action": "key.rotate",
              "description": "Rotate an API key.",
              "params_schema": {{KeyRotateSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            }
          ]
        }
        """;

    private static string RegistryJsonWithManifestOnlyAction() => $$"""
        {
          "schema_version": 4,
          "revision": "{{LegacyRevision}}",
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a service.",
              "params_schema": {{ServiceConnectSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
            },
            {
              "action": "developer_app.create",
              "description": "Create a developer app.",
              "params_schema": {{DeveloperAppSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
            }
          ]
        }
        """;

    private static string RegistryJsonWithUnknownAction() => $$"""
        {
          "schema_version": 4,
          "revision": "{{LegacyRevision}}",
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a service.",
              "params_schema": {{ServiceConnectSchema}},
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
            },
            {
              "action": "workflow.launch",
              "description": "Launch a workflow.",
              "params_schema": {"type": "object"},
              "risk": "execute",
              "tier": "v2",
              "remember_eligible": false
            }
          ]
        }
        """;

    private sealed class RecordingRegistrySource(string json)
        : INyxIdAssistantActionRegistrySource
    {
        public int FetchCount { get; private set; }

        public Task<string> FetchAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            FetchCount++;
            return Task.FromResult(json);
        }
    }

    private static string ReadRegistryFixture(string fixtureName) =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "contracts",
            "nyxid-assistant-conformance",
            "v1",
            fixtureName));

    private static NyxIdChatActionPostconditionInput ServiceConnectPostconditionProbe() => new()
    {
        ScopeId = "scope-probe",
        OwnerSubject = "owner-probe",
        OriginTurnId = "turn-probe",
        ActionRequestId = "action-request-probe",
        Action = NyxIdAssistantActionKind.ServiceConnect,
        ReportedDisposition = NyxIdChatActionDisposition.Completed,
        Params = new NyxIdAssistantActionParams
        {
            CatalogServiceConnect = new NyxIdCatalogServiceConnectParams
            {
                ServiceSlug = "api-probe",
            },
        },
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Aevatar repository root.");
    }

    private sealed class ThrowingRegistrySource(Exception exception)
        : INyxIdAssistantActionRegistrySource
    {
        public Task<string> FetchAsync(CancellationToken ct) =>
            Task.FromException<string>(exception);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody),
            });
        }
    }
}
