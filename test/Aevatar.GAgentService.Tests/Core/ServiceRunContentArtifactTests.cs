using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceRunContentArtifactTests
{
    [Fact]
    public async Task AttachResultArtifacts_ShouldBeActorOwnedCasAndRecoverAfterRestart()
    {
        var store = new InMemoryEventStore();
        var original = CreateAgent(store);
        await original.ActivateAsync();
        await original.HandleRegisterAsync(new RegisterServiceRunRequested { Record = BuildRecord() });
        var reference = BuildReference("artifact-1", "artifact-1-revision-1", 'a');

        var command = new AttachServiceRunResultArtifactsRequested
        {
            RunId = "run-1",
            ExpectedStateVersion = 1,
            ResultArtifacts = { reference },
        };
        await original.HandleAttachResultArtifactsAsync(command);
        await original.HandleAttachResultArtifactsAsync(command.Clone());

        original.State.Record!.ResultArtifacts.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(reference);
        original.State.LastAppliedEventVersion.Should().Be(2);

        var recovered = CreateAgent(store);
        await recovered.ActivateAsync();
        recovered.State.Record!.ResultArtifacts.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(reference);

        var stale = () => recovered.HandleAttachResultArtifactsAsync(new AttachServiceRunResultArtifactsRequested
        {
            RunId = "run-1",
            ExpectedStateVersion = 1,
            ResultArtifacts = { BuildReference("artifact-2", "artifact-2-revision-1", 'b') },
        });
        await stale.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*state version is 2, not 1*");
    }

    [Fact]
    public async Task AttachResultArtifacts_ShouldRejectConflictingRevisionIdentity()
    {
        var actor = CreateAgent(new InMemoryEventStore());
        await actor.ActivateAsync();
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = BuildRecord() });
        await actor.HandleAttachResultArtifactsAsync(new AttachServiceRunResultArtifactsRequested
        {
            RunId = "run-1",
            ExpectedStateVersion = 1,
            ResultArtifacts = { BuildReference("artifact-1", "revision-1", 'a') },
        });

        var act = () => actor.HandleAttachResultArtifactsAsync(new AttachServiceRunResultArtifactsRequested
        {
            RunId = "run-1",
            ExpectedStateVersion = 2,
            ResultArtifacts = { BuildReference("artifact-1", "revision-1", 'b') },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*artifact-1*revision-1*conflicting content hash*");
        actor.State.Record!.ResultArtifacts.Should().ContainSingle()
            .Which.ContentHash.Should().Be(new string('a', 64));
    }

    [Fact]
    public async Task AttachResultArtifacts_ShouldRejectConflictingRevisionMediaType()
    {
        var actor = CreateAgent(new InMemoryEventStore());
        await actor.ActivateAsync();
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = BuildRecord() });
        var reference = BuildReference("artifact-1", "revision-1", 'a');
        await actor.HandleAttachResultArtifactsAsync(new AttachServiceRunResultArtifactsRequested
        {
            RunId = "run-1",
            ExpectedStateVersion = 1,
            ResultArtifacts = { reference },
        });
        var conflicting = reference.Clone();
        conflicting.MediaType = "application/json";

        var act = () => actor.HandleAttachResultArtifactsAsync(new AttachServiceRunResultArtifactsRequested
        {
            RunId = "run-1",
            ExpectedStateVersion = 2,
            ResultArtifacts = { conflicting },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*artifact-1*revision-1*conflicting media type*");
    }

    [Fact]
    public async Task AttachResultArtifacts_ShouldValidateReferenceFieldsBeforePersisting()
    {
        var actor = CreateAgent(new InMemoryEventStore());
        await actor.ActivateAsync();
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = BuildRecord() });

        async Task AssertRejectedAsync(ContentArtifactReference reference, string expectedMessage)
        {
            var act = () => actor.HandleAttachResultArtifactsAsync(new AttachServiceRunResultArtifactsRequested
            {
                RunId = "run-1",
                ExpectedStateVersion = 1,
                ResultArtifacts = { reference },
            });
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(expectedMessage);
        }

        var missingArtifact = BuildReference(string.Empty, "revision-1", 'a');
        await AssertRejectedAsync(missingArtifact, "*artifact_id is required*");
        var missingRevision = BuildReference("artifact-1", string.Empty, 'a');
        await AssertRejectedAsync(missingRevision, "*revision_id is required*");
        var missingMediaType = BuildReference("artifact-1", "revision-1", 'a');
        missingMediaType.MediaType = string.Empty;
        await AssertRejectedAsync(missingMediaType, "*media_type is required*");
        var shortHash = BuildReference("artifact-1", "revision-1", 'a');
        shortHash.ContentHash = "abc";
        await AssertRejectedAsync(shortHash, "*content_hash must be a SHA-256 hex digest*");
        var invalidHex = BuildReference("artifact-1", "revision-1", 'z');
        await AssertRejectedAsync(invalidHex, "*content_hash must be a SHA-256 hex digest*");

        actor.State.LastAppliedEventVersion.Should().Be(1);
        actor.State.Record!.ResultArtifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task AttachResultArtifacts_ShouldRejectUnregisteredOrMismatchedRunIdentity()
    {
        var actor = CreateAgent(new InMemoryEventStore());
        await actor.ActivateAsync();
        var reference = BuildReference("artifact-1", "revision-1", 'a');
        var unregistered = () => actor.HandleAttachResultArtifactsAsync(new AttachServiceRunResultArtifactsRequested
        {
            RunId = "run-1",
            ExpectedStateVersion = 0,
            ResultArtifacts = { reference },
        });
        await unregistered.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no registered run; result artifact attachment rejected*");

        await actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = BuildRecord() });
        var mismatched = () => actor.HandleAttachResultArtifactsAsync(new AttachServiceRunResultArtifactsRequested
        {
            RunId = "run-other",
            ExpectedStateVersion = 1,
            ResultArtifacts = { reference },
        });
        await mismatched.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot apply result artifact attachment for run 'run-other'*");
    }

    [Fact]
    public async Task TerminalStatus_ShouldPreserveTypedResultReferencesWithoutEmbeddingContent()
    {
        var actor = CreateAgent(new InMemoryEventStore());
        await actor.ActivateAsync();
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = BuildRecord() });
        var reference = BuildReference("artifact-1", "revision-1", 'a');

        await actor.HandleUpdateStatusAsync(new UpdateServiceRunStatusRequested
        {
            RunId = "run-1",
            Status = ServiceRunStatus.Completed,
            LastOutput = "done",
            ResultArtifacts = { reference },
        });

        actor.State.Record!.ResultArtifacts.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(reference);
        actor.State.Record.ToString().Should().NotContain("inline_content");
    }

    private static ServiceRunGAgent CreateAgent(InMemoryEventStore store) =>
        GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            store,
            "service-run:tenant-1:svc-1:run-1",
            static () => new ServiceRunGAgent());

    private static ServiceRunRecord BuildRecord() =>
        new()
        {
            ScopeId = "tenant-1",
            ServiceId = "svc-1",
            ServiceKey = "tenant-1:svc-1",
            RunId = "run-1",
            CommandId = "cmd-run-1",
            CorrelationId = "corr-run-1",
            EndpointId = "run",
            ImplementationKind = ServiceImplementationKind.Static,
            TargetActorId = "target-run-1",
            RevisionId = "r1",
            DeploymentId = "dep-1",
            Status = ServiceRunStatus.Unspecified,
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-20T00:00:00Z")),
        };

    private static ContentArtifactReference BuildReference(
        string artifactId,
        string revisionId,
        char hashCharacter) =>
        new()
        {
            ArtifactId = artifactId,
            RevisionId = revisionId,
            ContentHash = new string(hashCharacter, 64),
            MediaType = "text/markdown",
        };
}
