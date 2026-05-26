using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Studio.Application.Scripts.Contracts;
using Aevatar.Studio.Infrastructure.ActorBacked;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ScriptNativeDocumentRuntimeActivityQueryPortTests
{
    [Fact]
    public async Task GetAsync_ShouldMapNativeAppScriptReadModelFields()
    {
        var updatedAt = DateTimeOffset.Parse("2026-03-27T00:00:00Z");
        var document = new ScriptNativeDocumentReadModel
        {
            Id = "runtime-1",
            ScriptId = "script-1",
            DefinitionActorId = "definition-1",
            Revision = "rev-1",
            StateVersion = 7,
            LastEventId = "event-7",
            UpdatedAt = updatedAt,
            Fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [AppScriptProtocol.InputField] = "hello",
                [AppScriptProtocol.OutputField] = "HELLO",
                [AppScriptProtocol.StatusField] = "ok",
                [AppScriptProtocol.LastCommandIdField] = "cmd-1",
                [AppScriptProtocol.NotesField] = new[] { "trimmed", "uppercased" },
            },
        };
        var reader = new RecordingNativeDocumentReader(document);
        var port = new ScriptNativeDocumentRuntimeActivityQueryPort(reader);

        var snapshot = await port.GetAsync("runtime-1");

        snapshot.Should().NotBeNull();
        snapshot!.ActorId.Should().Be("runtime-1");
        snapshot.ScriptId.Should().Be("script-1");
        snapshot.DefinitionActorId.Should().Be("definition-1");
        snapshot.Revision.Should().Be("rev-1");
        snapshot.Input.Should().Be("hello");
        snapshot.Output.Should().Be("HELLO");
        snapshot.Status.Should().Be("ok");
        snapshot.LastCommandId.Should().Be("cmd-1");
        snapshot.Notes.Should().Equal("trimmed", "uppercased");
        snapshot.StateVersion.Should().Be(7);
        snapshot.LastEventId.Should().Be("event-7");
        snapshot.UpdatedAt.Should().Be(updatedAt);
        reader.LastKey.Should().Be("runtime-1");
    }

    [Fact]
    public async Task GetAsync_WhenNativeDocumentMissing_ShouldReturnNull()
    {
        var port = new ScriptNativeDocumentRuntimeActivityQueryPort(new RecordingNativeDocumentReader(null));

        var snapshot = await port.GetAsync("runtime-1");

        snapshot.Should().BeNull();
    }

    private sealed class RecordingNativeDocumentReader : IProjectionDocumentReader<ScriptNativeDocumentReadModel, string>
    {
        private readonly ScriptNativeDocumentReadModel? _document;

        public RecordingNativeDocumentReader(ScriptNativeDocumentReadModel? document)
        {
            _document = document;
        }

        public string? LastKey { get; private set; }

        public Task<ScriptNativeDocumentReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            LastKey = key;
            return Task.FromResult(_document);
        }

        public Task<ProjectionDocumentQueryResult<ScriptNativeDocumentReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            return Task.FromResult(_document == null
                ? ProjectionDocumentQueryResult<ScriptNativeDocumentReadModel>.Empty
                : new ProjectionDocumentQueryResult<ScriptNativeDocumentReadModel>
                {
                    Items = [_document],
                });
        }
    }
}
