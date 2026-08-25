using System.Runtime.CompilerServices;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed class NyxIdConformanceManifestTests
{
    private static readonly string ContractRoot = Path.Combine(
        FindRepositoryRoot(),
        "docs",
        "contracts",
        "nyxid-assistant-conformance",
        "v1");

    [Fact]
    public void TransitionRegistries_ShouldDegradePerActionAcrossServedCompositions()
    {
        var legacy = NyxIdAssistantActionRegistry.Load(
            File.ReadAllText(Path.Combine(ContractRoot, "registry-v4.json")));
        var draft = NyxIdAssistantActionRegistry.Load(
            File.ReadAllText(Path.Combine(ContractRoot, "registry-v5.json")));
        var leastScope = NyxIdAssistantActionRegistry.Load(
            File.ReadAllText(Path.Combine(ContractRoot, "registry-v6.json")));
        var keyRotation = NyxIdAssistantActionRegistry.Load(
            File.ReadAllText(Path.Combine(ContractRoot, "registry-v7.json")));
        var target = NyxIdAssistantActionRegistry.Load(
            File.ReadAllText(Path.Combine(ContractRoot, "registry-v8.json")));

        legacy.RegistryRevision.Should().Be("nyxid-assistant-actions.v4");
        draft.RegistryRevision.Should().Be("nyxid-assistant-actions.v5");
        leastScope.RegistryRevision.Should().Be("nyxid-assistant-actions.v6");
        keyRotation.RegistryRevision.Should().Be("nyxid-assistant-actions.v7");
        target.RegistryRevision.Should().Be("nyxid-assistant-actions.v8");

        // v4 served only service.connect; absent actions are simply unavailable.
        legacy.TryGetDefinition("service.connect", out _).Should().BeTrue();
        legacy.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
        legacy.TryGetDefinition("key.create", out _).Should().BeFalse();
        legacy.TryGetDefinition("key.rotate", out _).Should().BeFalse();
        legacy.SkippedActions.Should().BeEmpty();

        // v5 served the draft key.create schema, which no longer matches the
        // pinned least-scope contract: that one action degrades while the
        // valid descriptors in the same composition stay enabled.
        draft.TryGetDefinition("service.connect", out _).Should().BeTrue();
        draft.TryGetDefinition("key.rotate", out _).Should().BeTrue();
        draft.TryGetDefinition("key.create", out _).Should().BeFalse();
        draft.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
        draft.SkippedActions.Should().ContainSingle(skip =>
            skip.WireAction == "key.create" &&
            skip.Code == "NYXID_ACTION_REGISTRY_INVALID");

        leastScope.TryGetDefinition("service.connect", out _).Should().BeTrue();
        leastScope.TryGetDefinition("key.create", out _).Should().BeTrue();
        leastScope.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
        leastScope.TryGetDefinition("key.rotate", out _).Should().BeFalse();
        leastScope.SkippedActions.Should().BeEmpty();
        foreach (var registry in new[] { keyRotation, target })
        {
            registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
            registry.TryGetDefinition("key.create", out _).Should().BeTrue();
            registry.TryGetDefinition("key.rotate", out _).Should().BeTrue();
            registry.TryGetDefinition("service.reauthorize", out _).Should().BeFalse();
            registry.SkippedActions.Should().BeEmpty();
        }
    }

    [Fact]
    public void CoverageManifest_ShouldHaveExactlyOneCompleteOutcomeRowPerGeneratedLeaf()
    {
        using var leavesDocument = Load("cli-leaves.json");
        using var coverageDocument = Load("coverage-manifest.json");
        var leaves = leavesDocument.RootElement.GetProperty("leaves")
            .EnumerateArray()
            .Select(static row => row.GetProperty("path").GetString()!)
            .ToArray();
        var rows = coverageDocument.RootElement.GetProperty("rows")
            .EnumerateArray()
            .ToArray();

        leavesDocument.RootElement.GetProperty("leaf_count").GetInt32().Should().Be(leaves.Length);
        coverageDocument.RootElement.GetProperty("leaf_count").GetInt32().Should().Be(rows.Length);
        rows.Select(static row => row.GetProperty("cli_path").GetString())
            .Should().BeEquivalentTo(leaves, options => options.WithStrictOrdering());
        rows.Select(static row => row.GetProperty("cli_path").GetString())
            .Should().OnlyHaveUniqueItems();

        foreach (var row in rows)
        {
            foreach (var property in new[]
                     {
                         "operation_id",
                         "operation_class",
                         "outcome_class",
                         "mechanism",
                         "availability",
                         "availability_predicate",
                         "approval_authority",
                         "evidence_type",
                         "owning_issue",
                     })
            {
                row.GetProperty(property).GetString().Should().NotBeNullOrWhiteSpace(
                    $"{row.GetProperty("cli_path").GetString()} must name {property}");
            }
        }
    }

    [Fact]
    public async Task ShippedRows_ShouldResolveToTheirCompleteRuntimeArtifacts()
    {
        using var coverageDocument = Load("coverage-manifest.json");
        var rows = coverageDocument.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyxid.example.test" };
        var client = new NyxIdApiClient(options, new HttpClient());
        var mountedTools = (await new NyxIdAssistantToolSource(options, client).DiscoverToolsAsync())
            .Select(static tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        var registries = new[] { "registry-v7.json", "registry-v8.json" }
            .Select(fileName => NyxIdAssistantActionRegistry.Load(
                File.ReadAllText(Path.Combine(ContractRoot, fileName))))
            .ToArray();

        var shippedReads = rows.Where(static row =>
            row.GetProperty("operation_class").GetString() == "R" &&
            row.GetProperty("status").GetString() == "shipped");
        foreach (var row in shippedReads)
        {
            var tool = row.GetProperty("mounted_tool").GetString();
            mountedTools.Should().Contain(tool,
                $"{row.GetProperty("cli_path").GetString()} claims a mounted Class-R route");
        }

        var shippedActions = rows.Where(static row =>
            row.GetProperty("operation_class").GetString() == "A" &&
            row.GetProperty("status").GetString() == "shipped");
        foreach (var row in shippedActions)
        {
            var operation = row.GetProperty("operation_id").GetString()!;
            foreach (var registry in registries)
                registry.TryGetDefinition(operation, out _).Should().BeTrue();
            var artifacts = row.GetProperty("artifacts").EnumerateArray()
                .Select(static value => value.GetString()!)
                .ToArray();
            artifacts.Should().HaveCount(4).And.OnlyHaveUniqueItems();
            artifacts.Should().OnlyContain(relativePath =>
                File.Exists(Path.Combine(FindRepositoryRoot(), relativePath)));
            row.GetProperty("evidence_type").GetString()
                .Should().Be("typed_postcondition_read_model");
        }
    }

    [Fact]
    public async Task UtteranceFixtures_ShouldRouteDeterministicallyAndPreserveHonestAvailability()
    {
        using var coverageDocument = Load("coverage-manifest.json");
        var routedRows = coverageDocument.RootElement.GetProperty("rows")
            .EnumerateArray()
            .Where(static row => row.GetProperty("operation_class").GetString() is "A" or "L")
            .ToArray();

        foreach (var row in routedRows)
        {
            var operation = row.GetProperty("operation_id").GetString()!;
            var utterance = row.GetProperty("utterances")[0].GetString()!;
            var provider = new ScriptedRoutingProvider(operation);
            var classifier = new StreamingAgentProfileTurnClassifier(
                new StubProviderFactory(provider));
            var result = await classifier.ClassifyAsync(
                new AgentProfileTurnClassificationRequest(
                    utterance,
                    [
                        new AgentProfileTurnClassificationCandidate(
                            operation,
                            row.GetProperty("description").GetString()!,
                            AgentProfileSideEffectClass.ExternalHandoff),
                        new AgentProfileTurnClassificationCandidate(
                            "unrelated.decoy",
                            "An unrelated operation.",
                            AgentProfileSideEffectClass.ReadOnly),
                    ],
                    TimeSpan.FromSeconds(1)));

            result.Should().Be(AgentProfileTurnClassificationResult.Matched(operation));
            if (row.GetProperty("availability").GetString() == "recognized_but_unavailable")
            {
                row.GetProperty("outcome_class").GetString()
                    .Should().BeOneOf("honest_decline", "honest_cannot_check");
                row.GetProperty("evidence_type").GetString().Should().Be("none");
            }
            if (row.GetProperty("operation_class").GetString() == "L")
            {
                row.GetProperty("handoff_command").GetString()
                    .Should().Be($"nyxid {row.GetProperty("cli_path").GetString()}");
                row.GetProperty("evidence_type").GetString()
                    .Should().Be("no_aevatar_execution_claim");
            }
        }
    }

    [Theory]
    [InlineData(
        "node daemon start",
        "L",
        "local_handoff",
        "copyable_cli_command",
        "no_aevatar_execution_claim",
        "nyxid node daemon start")]
    [InlineData(
        "billing usage",
        "X",
        "honest_decline",
        "no_tool_exposed",
        "no_effect_receipt",
        null)]
    [InlineData(
        "channel-event push",
        "X",
        "honest_decline",
        "no_tool_exposed",
        "no_effect_receipt",
        null)]
    public void RepresentativeHonestyRows_ShouldPinCompleteOutcomeContracts(
        string cliPath,
        string operationClass,
        string outcomeClass,
        string mechanism,
        string evidenceType,
        string? handoffCommand)
    {
        using var coverageDocument = Load("coverage-manifest.json");
        var row = coverageDocument.RootElement.GetProperty("rows")
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("cli_path").GetString() == cliPath);

        row.GetProperty("operation_class").GetString().Should().Be(operationClass);
        row.GetProperty("outcome_class").GetString().Should().Be(outcomeClass);
        row.GetProperty("mechanism").GetString().Should().Be(mechanism);
        row.GetProperty("evidence_type").GetString().Should().Be(evidenceType);
        row.GetProperty("approval_authority").GetString().Should().NotBeNullOrWhiteSpace();

        if (handoffCommand is not null)
        {
            row.GetProperty("handoff_command").GetString().Should().Be(handoffCommand);
            row.GetProperty("utterances").EnumerateArray().Should().NotBeEmpty();
        }
        else
        {
            row.TryGetProperty("handoff_command", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task ScriptedModel_ShouldFailClosedForUnknownOrToolCallingRoutes()
    {
        var candidates = new[]
        {
            new AgentProfileTurnClassificationCandidate(
                "key.rotate",
                "Rotate one key through a browser-owned action.",
                AgentProfileSideEffectClass.ExternalHandoff),
        };
        var unknown = new StreamingAgentProfileTurnClassifier(
            new StubProviderFactory(new RawScriptedProvider(
                [new LLMStreamChunk { DeltaContent = "{\"status\":\"matched\",\"intent_id\":\"key.delete\"}" }])));
        var toolCalling = new StreamingAgentProfileTurnClassifier(
            new StubProviderFactory(new RawScriptedProvider(
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call-alpha",
                        Name = "nyxid_api_keys",
                        ArgumentsJson = "{}",
                    },
                },
            ])));

        var unknownResult = await unknown.ClassifyAsync(new AgentProfileTurnClassificationRequest(
            "rotate my key", candidates, TimeSpan.FromSeconds(1)));
        var toolResult = await toolCalling.ClassifyAsync(new AgentProfileTurnClassificationRequest(
            "rotate my key", candidates, TimeSpan.FromSeconds(1)));

        unknownResult.Should().Be(AgentProfileTurnClassificationResult.Failed("unknown_intent"));
        toolResult.Should().Be(AgentProfileTurnClassificationResult.Failed("tool_call_not_allowed"));
    }

    [Fact]
    public void TolerantReaderCorpus_ShouldIgnoreExtensionsAndDegradePerAction()
    {
        using var document = Load("tolerant-reader-fixtures.json");
        foreach (var fixture in document.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            var payload = fixture.TryGetProperty("raw_payload", out var raw)
                ? raw.GetString()!
                : fixture.GetProperty("payload").GetRawText();
            var expected = fixture.GetProperty("expected").GetString();
            var action = () => NyxIdAssistantActionRegistry.Load(payload);

            if (expected == "accepted")
            {
                var registry = action.Should().NotThrow().Subject;
                registry.TryGetDefinition("service.connect", out _).Should().BeTrue();
                registry.SkippedActions.Should().BeEmpty();
                if (fixture.GetProperty("id").GetString() == "unknown-verb")
                    registry.TryGetDefinition("workflow.launch", out _).Should().BeFalse();
                continue;
            }

            if (expected == "degraded")
            {
                var registry = action.Should().NotThrow().Subject;
                var skippedAction = fixture.GetProperty("skipped_action").GetString()!;
                var skipCode = fixture.GetProperty("error_code").GetString();
                registry.TryGetDefinition(skippedAction, out _).Should().BeFalse();
                registry.SkippedActions.Should().ContainSingle(skip =>
                    skip.WireAction == skippedAction && skip.Code == skipCode);
                registry.TryGetDefinition("key.rotate", out _).Should().BeTrue(
                    $"{fixture.GetProperty("id").GetString()} must keep valid actions enabled");
                continue;
            }

            var exception = Record.Exception(action);
            exception.Should().NotBeNull();
            ErrorCode(exception!).Should().Be(fixture.GetProperty("error_code").GetString());
        }
    }

    [Fact]
    public void AdversarialCorpus_ShouldKeepIdentityDistinctAndForbidEffectClaims()
    {
        using var document = Load("adversarial-fixtures.json");
        var fixtures = document.RootElement.GetProperty("fixtures").EnumerateArray().ToArray();

        fixtures.Should().OnlyContain(static fixture =>
            !fixture.GetProperty("effect_receipt_allowed").GetBoolean());
        fixtures.Select(static fixture => fixture.GetProperty("category").GetString())
            .Should().Contain([
                "indirect_prompt_injection",
                "identity_confusion",
                "command_ordering",
                "leakage",
                "tolerant_reader",
                "honesty",
            ]);
        var identity = fixtures.Single(static fixture =>
            fixture.GetProperty("category").GetString() == "identity_confusion");
        new[]
            {
                identity.GetProperty("member_id").GetString(),
                identity.GetProperty("workflow_id").GetString(),
                identity.GetProperty("published_service_id").GetString(),
            }
            .Should().Equal("m-alpha", "wf-alpha", "svc-alpha")
            .And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void WireReader_ShouldIgnoreUnknownFieldsAndFailClosedForUnknownActionEnum()
    {
        var known = new NyxIdAssistantActionRequestWirePayload
        {
            SchemaVersion = 4,
            Action = "service.connect",
            Params = new NyxIdAssistantActionWireParams
            {
                CatalogService = new NyxIdCatalogServiceConnectParams
                {
                    ServiceSlug = "github",
                },
            },
        };
        var bytes = known.ToByteArray().Concat(new byte[] { 0xA0, 0x06, 0x01 }).ToArray();

        var parsed = NyxIdAssistantActionRequestWirePayload.Parser.ParseFrom(bytes);

        parsed.Action.Should().Be("service.connect");
        parsed.Params.CatalogService.ServiceSlug.Should().Be("github");

        var frames = NyxIdChatConversationAguiFrameBuilder.BuildActionRequested(
            "conversation-alpha",
            "turn-alpha",
            new NyxIdChatActionRequestedEvent
            {
                Request = new NyxIdChatActionRequestState
                {
                    SchemaVersion = 4,
                    Action = (NyxIdAssistantActionKind)999,
                    Params = new NyxIdAssistantActionParams(),
                },
                Task = new NyxIdChatTaskState(),
                OriginTurn = new NyxIdChatTurnState(),
            },
            1);
        frames.Should().BeEmpty();

        var unknownType = new Any
        {
            TypeUrl = "type.googleapis.com/future.nyxid.ActionRequest",
            Value = ByteString.CopyFromUtf8("future"),
        };
        unknownType.Is(NyxIdAssistantActionRequestWirePayload.Descriptor).Should().BeFalse();
    }

    private static JsonDocument Load(string fileName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractRoot, fileName)));

    private static string ErrorCode(Exception exception) => exception switch
    {
        NyxIdAssistantActionRegistryException registry => registry.Code,
        NyxIdActionSecretPolicyException secret => secret.Code,
        _ => exception.GetType().Name,
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

        throw new DirectoryNotFoundException("Could not locate the Aevatar repository root.");
    }

    private sealed class StubProviderFactory(ILLMProvider provider) : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => provider;
        public ILLMProvider GetDefault() => provider;
        public IReadOnlyList<string> GetAvailableProviders() => [provider.Name];
    }

    private sealed class ScriptedRoutingProvider(string operation) : ILLMProvider
    {
        public string Name => "nyxid-conformance-scripted-router";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk
            {
                DeltaContent = JsonSerializer.Serialize(new
                {
                    status = "matched",
                    intent_id = operation,
                }),
            };
            await Task.CompletedTask;
        }
    }

    private sealed class RawScriptedProvider(IReadOnlyList<LLMStreamChunk> chunks) : ILLMProvider
    {
        public string Name => "nyxid-conformance-malicious-router";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
            }
            await Task.CompletedTask;
        }
    }
}
