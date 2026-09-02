using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using WorkflowChatInputPartKind = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowChatHistoryCreateRecoveryModelsTests
{
    [Fact]
    public void WorkflowChatHistoryCreateRecoveryIds_ShouldNormalizeInputsAndKeepTupleBoundaries()
    {
        var normalized = WorkflowChatHistoryCreateRecoveryIds.FromScopeAndCommandId(
            "scope-alpha",
            "command-alpha");

        var padded = WorkflowChatHistoryCreateRecoveryIds.FromScopeAndCommandId(
            " scope-alpha ",
            " command-alpha ");

        normalized.Should().Be(padded);
        normalized.Should().StartWith("chat-history-create:");
        normalized.Should().HaveLength("chat-history-create:".Length + 64);
        WorkflowChatHistoryCreateRecoveryIds.FromScopeAndCommandId("a", "bc")
            .Should().NotBe(WorkflowChatHistoryCreateRecoveryIds.FromScopeAndCommandId("ab", "c"));
    }

    [Fact]
    public void WorkflowChatHistoryCreateRecovery_ShouldCarryCommittedCreateFacts()
    {
        var updatedAt = DateTimeOffset.Parse("2026-07-21T01:02:03Z");

        var recovery = new WorkflowChatHistoryCreateRecovery(
            WorkflowChatHistoryCreateRecoveryStatus.AppendCommitted,
            "scope-alpha",
            "command-alpha",
            "conversation-alpha",
            "turn-alpha",
            "workflow-alpha",
            "workflow-command-alpha",
            "workflow-correlation-alpha",
            "fingerprint-alpha",
            42,
            updatedAt);

        recovery.Status.Should().Be(WorkflowChatHistoryCreateRecoveryStatus.AppendCommitted);
        recovery.ScopeId.Should().Be("scope-alpha");
        recovery.CommandId.Should().Be("command-alpha");
        recovery.ConversationId.Should().Be("conversation-alpha");
        recovery.TurnId.Should().Be("turn-alpha");
        recovery.WorkflowActorId.Should().Be("workflow-alpha");
        recovery.WorkflowCommandId.Should().Be("workflow-command-alpha");
        recovery.WorkflowCorrelationId.Should().Be("workflow-correlation-alpha");
        recovery.RequestFingerprint.Should().Be("fingerprint-alpha");
        recovery.StateVersion.Should().Be(42);
        recovery.UpdatedAt.Should().Be(updatedAt);
    }

    [Fact]
    public void WorkflowChatCreateRequestFingerprint_ShouldNormalizeWhitespaceAndMetadataOrder()
    {
        var first = RichRequest(
            prompt: " deploy report ",
            scopeId: " scope-alpha ",
            sessionId: " session-alpha ",
            source: WorkflowChatSource.DefinitionActor(" actor-alpha ", " direct "),
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [" zeta "] = " value-z ",
                [" "] = "ignored",
                [" alpha "] = " value-a ",
            });
        var second = RichRequest(
            prompt: "deploy report",
            scopeId: "scope-alpha",
            sessionId: "session-alpha",
            source: WorkflowChatSource.DefinitionActor("actor-alpha", "direct"),
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["alpha"] = "value-a",
                ["zeta"] = "value-z",
            });

        WorkflowChatCreateRequestFingerprint.Compute(first)
            .Should().Be(WorkflowChatCreateRequestFingerprint.Compute(second));
    }

    [Fact]
    public void WorkflowChatCreateRequestFingerprint_ShouldIncludeSourceVariantInFingerprint()
    {
        var fingerprints = new[]
        {
            WorkflowChatCreateRequestFingerprint.Compute(
                MinimalRequest(WorkflowChatSource.Direct())),
            WorkflowChatCreateRequestFingerprint.Compute(
                MinimalRequest(WorkflowChatSource.CatalogWorkflow("direct"))),
            WorkflowChatCreateRequestFingerprint.Compute(
                MinimalRequest(WorkflowChatSource.DefinitionActor("actor-alpha", "direct"))),
            WorkflowChatCreateRequestFingerprint.Compute(
                MinimalRequest(WorkflowChatSource.InlineYamlBundle(
                    "entry",
                    [
                        new WorkflowChatInlineYamlDocument("entry", "name: entry"),
                        new WorkflowChatInlineYamlDocument("helper", "name: helper"),
                    ],
                    "actor-inline"))),
        };

        fingerprints.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void WorkflowChatCreateRequestFingerprint_ShouldIncludeInputFileFactsAndLlmControl()
    {
        var baseline = RichRequest();
        var changedFile = RichRequest(fileSha256: "sha256-beta");
        var changedModel = RichRequest(modelOverride: "gpt-beta");

        var baselineFingerprint = WorkflowChatCreateRequestFingerprint.Compute(baseline);

        WorkflowChatCreateRequestFingerprint.Compute(changedFile)
            .Should().NotBe(baselineFingerprint);
        WorkflowChatCreateRequestFingerprint.Compute(changedModel)
            .Should().NotBe(baselineFingerprint);
    }

    [Fact]
    public void WorkflowChatCreateRequestFingerprint_ShouldTreatBlankMetadataAsAbsent()
    {
        var blankMetadata = MinimalRequest(
            WorkflowChatSource.Direct(),
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [" "] = "ignored",
            });
        var absentMetadata = MinimalRequest(
            WorkflowChatSource.Direct(),
            metadata: null);

        WorkflowChatCreateRequestFingerprint.Compute(blankMetadata)
            .Should().Be(WorkflowChatCreateRequestFingerprint.Compute(absentMetadata));
    }

    [Fact]
    public void WorkflowChatCreateRequestFingerprint_ShouldRejectNullRequest()
    {
        Action act = () => WorkflowChatCreateRequestFingerprint.Compute(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static WorkflowChatRunRequest MinimalRequest(
        WorkflowChatSource source,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(
            "deploy report",
            source,
            ExternalCapabilityExecutionMode.Interactive,
            ScopeId: "scope-alpha",
            ChatConversation: WorkflowChatConversationIntent.Create(),
            Metadata: metadata);

    private static WorkflowChatRunRequest RichRequest(
        string prompt = "deploy report",
        string scopeId = "scope-alpha",
        string sessionId = "session-alpha",
        WorkflowChatSource? source = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string fileSha256 = "sha256-alpha",
        string modelOverride = "gpt-alpha") =>
        new(
            prompt,
            source ?? WorkflowChatSource.DefinitionActor("actor-alpha", "direct"),
            ExternalCapabilityExecutionMode.Interactive,
            SessionId: sessionId,
            InputParts:
            [
                new WorkflowChatInputPart
                {
                    Kind = WorkflowChatInputPartKind.Text,
                    Text = "Hello",
                    Name = "prompt.txt",
                    MediaType = "text/plain",
                },
                new WorkflowChatInputPart
                {
                    Kind = WorkflowChatInputPartKind.File,
                    DataBase64 = "ZmFrZQ==",
                    MediaType = "application/pdf",
                    Uri = "file://artifact-alpha",
                    Name = "report.pdf",
                    FileRef = new FileArtifactRef
                    {
                        ArtifactId = "artifact-alpha",
                        CreatedAtUnixMs = 1_725_000_000_000,
                        ExpiresAtUnixMs = 1_725_086_400_000,
                        FileId = "file-alpha",
                        FileName = "report.pdf",
                        MediaType = "application/pdf",
                        OwnerRunId = "run-alpha",
                        OwnerScopeId = "scope-alpha",
                        Sha256 = fileSha256,
                        SizeBytes = 1024,
                        SourceKind = FileArtifactSourceKind.ChatInput,
                        SourceMessageId = "message-alpha",
                        SourceResourceKey = "resource-alpha",
                    },
                },
            ],
            Metadata: metadata ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["alpha"] = "value-alpha",
            },
            ScopeId: scopeId,
            LlmControl: new WorkflowLlmControl(
                ModelOverride: modelOverride,
                MaxToolRoundsOverride: 4,
                UserMemoryPrompt: "remember the deployment",
                RoutePreference: "quality",
                SenderNyxIdAccessToken: "token-alpha"),
            ChatConversation: WorkflowChatConversationIntent.Continue("conversation-alpha"));
}
