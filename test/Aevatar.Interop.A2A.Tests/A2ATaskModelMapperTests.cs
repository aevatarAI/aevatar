using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Abstractions.Models;
using Aevatar.Interop.A2A.Application;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Interop.A2A.Tests;

// Refactor (iter30/cluster-031-a2a-actor-owned):
//   Old pattern: adapter tests only touched text DTO mapping through IA2ATaskStore behavior.
//   New principle: mapper tests assert protobuf-native A2A task payload branches before actor/readmodel persistence.
public class A2ATaskModelMapperTests
{
    [Fact]
    public void ToProto_WithDataPart_StoresProtobufValuesWithoutJsonStringPayload()
    {
        var message = new Message
        {
            Role = "user",
            Parts =
            [
                new DataPart
                {
                    Data = new()
                    {
                        ["name"] = "alice",
                        ["count"] = 3,
                        ["enabled"] = true,
                        ["nested"] = new Dictionary<string, object?> { ["value"] = "inside" },
                    },
                    Metadata = new() { ["source"] = "test" },
                },
            ],
        };

        var proto = A2ATaskModelMapper.ToProto(message);

        var part = proto.Parts.Should().ContainSingle().Subject;
        part.DataEntries.Should().HaveCount(4);
        part.DataEntries.Single(x => x.Key == "name").Value.StringValue.Should().Be("alice");
        part.DataEntries.Single(x => x.Key == "count").Value.NumberValue.Should().Be(3);
        part.DataEntries.Single(x => x.Key == "enabled").Value.BoolValue.Should().BeTrue();
        part.DataEntries.Single(x => x.Key == "nested").Value.StructValue.Fields["value"].StringValue.Should().Be("inside");
        part.Metadata["source"].Should().Be("test");
    }

    [Fact]
    public void ToDto_WithArtifactAndHistoryLength_MapsFileDataAndTrimsHistory()
    {
        var now = Timestamp.FromDateTime(DateTime.UtcNow);
        var state = new A2ATaskState
        {
            TaskId = "task-1",
            SessionId = "session-1",
            Status = A2ATaskModelMapper.BuildStatus(A2ATaskLifecycleState.Completed, now),
            UpdatedAt = now,
        };
        state.History.Add(new A2ATaskMessage
        {
            Role = "user",
            Parts = { new A2ATaskPart { Type = "text", Text = "old" } },
        });
        state.History.Add(new A2ATaskMessage
        {
            Role = "agent",
            Parts =
            {
                new A2ATaskPart
                {
                    Type = "data",
                    DataEntries =
                    {
                        new A2ATaskPartDataEntry
                        {
                            Key = "ok",
                            Value = Google.Protobuf.WellKnownTypes.Value.ForBool(true),
                        },
                        new A2ATaskPartDataEntry
                        {
                            Key = "score",
                            Value = Google.Protobuf.WellKnownTypes.Value.ForNumber(9.5),
                        },
                    },
                },
            },
        });
        state.Artifacts.Add(new A2ATaskArtifact
        {
            Name = "artifact",
            Index = 2,
            Parts =
            {
                new A2ATaskPart
                {
                    Type = "file",
                    FileName = "result.txt",
                    FileMimeType = "text/plain",
                    FileUri = "https://example.test/result.txt",
                },
            },
        });

        var dto = A2ATaskModelMapper.ToDto(state, historyLength: 1);

        dto.History.Should().ContainSingle();
        var dataPart = dto.History[0].Parts.Should().ContainSingle().Subject.Should().BeOfType<DataPart>().Subject;
        dataPart.Data["ok"].Should().Be(true);
        dataPart.Data["score"].Should().Be(9.5);
        var filePart = dto.Artifacts.Should().ContainSingle().Subject.Parts.Should().ContainSingle().Subject
            .Should().BeOfType<FilePart>().Subject;
        filePart.File.Name.Should().Be("result.txt");
        filePart.File.MimeType.Should().Be("text/plain");
        filePart.File.Uri.Should().Be("https://example.test/result.txt");
    }
}
