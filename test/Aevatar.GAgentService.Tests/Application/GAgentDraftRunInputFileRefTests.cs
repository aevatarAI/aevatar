using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class GAgentDraftRunInputFileRefTests
{
    [Fact]
    public void EnvelopeFactory_ShouldPreserveInputPartFileRef()
    {
        var factory = new GAgentDraftRunCommandEnvelopeFactory();

        var envelope = factory.CreateEnvelope(
            new GAgentDraftRunCommand(
                ScopeId: "scope-a",
                AgentKind: "RoleGAgent",
                Prompt: "hello",
                InputParts:
                [
                    new GAgentDraftRunInputPart
                    {
                        Kind = GAgentDraftRunInputPartKind.Text,
                        Text = "see attachment",
                        FileRef = new ChatFileRef
                        {
                            FileId = "file-static-1",
                            ArtifactId = "artifact-static-1",
                            SourceKind = ChatFileSourceKind.FormUpload,
                            SourceMessageId = "om_static_1",
                            SourceResourceKey = "resource_static_1",
                            FileName = "notes.txt",
                            MediaType = "text/plain",
                            SizeBytes = 42,
                            Sha256 = "sha-static-1",
                            CreatedAtUnixMs = 1710000000000,
                            ExpiresAtUnixMs = 1710003600000,
                            OwnerRunId = "run-static-1",
                            OwnerScopeId = "scope-a",
                        },
                    },
                ]),
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>()));

        var request = envelope.Payload.Unpack<ChatRequestEvent>();
        request.CommandAttemptId.Should().Be("cmd-1");
        var inputPart = request.InputParts.Should()
            .ContainSingle()
            .Which;
        inputPart.Kind.Should().Be(ChatContentPartKind.Text);
        inputPart.FileRef.Should().NotBeNull();
        inputPart.FileRef.FileId.Should().Be("file-static-1");
        inputPart.FileRef.ArtifactId.Should().Be("artifact-static-1");
        inputPart.FileRef.SourceKind.Should().Be(ChatFileSourceKind.FormUpload);
        inputPart.FileRef.SourceMessageId.Should().Be("om_static_1");
        inputPart.FileRef.SourceResourceKey.Should().Be("resource_static_1");
        inputPart.FileRef.FileName.Should().Be("notes.txt");
        inputPart.FileRef.MediaType.Should().Be("text/plain");
        inputPart.FileRef.SizeBytes.Should().Be(42);
        inputPart.FileRef.Sha256.Should().Be("sha-static-1");
        inputPart.FileRef.CreatedAtUnixMs.Should().Be(1710000000000);
        inputPart.FileRef.ExpiresAtUnixMs.Should().Be(1710003600000);
        inputPart.FileRef.OwnerRunId.Should().Be("run-static-1");
        inputPart.FileRef.OwnerScopeId.Should().Be("scope-a");
    }
}
