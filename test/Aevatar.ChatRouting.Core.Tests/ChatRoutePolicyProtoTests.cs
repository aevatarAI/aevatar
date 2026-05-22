using Aevatar.ChatRouting.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.ChatRouting.Core.Tests;

public sealed class ChatRoutePolicyProtoTests
{
    [Fact]
    public void ForwardToModel_ShouldRoundTripToolSetRefAndToolChoiceHint()
    {
        var action = new ChatRouteAction
        {
            ForwardToModel = new ForwardToModel
            {
                ModelName = "chrono-llm/gpt-5.5",
                ToolSetRef = new ChatRouteToolSetRef { Name = "workspace.default" },
                ToolChoiceHint = new ChatRouteToolChoiceHint
                {
                    ToolName = "aevatar_invoke_gagent",
                    PrefilledArguments = new Struct
                    {
                        Fields =
                        {
                            ["actor_id"] = Google.Protobuf.WellKnownTypes.Value.ForString("actor-1"),
                            ["wait"] = Google.Protobuf.WellKnownTypes.Value.ForString("stream"),
                        },
                    },
                },
            },
        };

        var parsed = ChatRouteAction.Parser.ParseFrom(action.ToByteArray());

        parsed.Should().Be(action);
        parsed.ActionCase.Should().Be(ChatRouteAction.ActionOneofCase.ForwardToModel);
        parsed.ForwardToModel.ToolSetRef.Name.Should().Be("workspace.default");
        parsed.ForwardToModel.ToolChoiceHint.ToolName.Should().Be("aevatar_invoke_gagent");
        parsed.ForwardToModel.ToolChoiceHint.PrefilledArguments.Fields["actor_id"].StringValue
            .Should()
            .Be("actor-1");
    }
}
