using System.Reflection;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class ToolCallLoopMediaContentCoverageTests
{
    [Fact]
    public void ComposeFinalCallId_WhenRequestIdMissing_ShouldReturnNull()
    {
        InvokeComposeFinalCallId(null).Should().BeNull();
        InvokeComposeFinalCallId(string.Empty).Should().BeNull();
        InvokeComposeFinalCallId("   ").Should().BeNull();
    }

    [Fact]
    public void BuildToolResultMessage_WhenPayloadIsInvalidJson_ShouldFallbackToPlainToolMessage()
    {
        var message = InvokeBuildToolResultMessage("tc-invalid", "{oops");

        message.Role.Should().Be("tool");
        message.ToolCallId.Should().Be("tc-invalid");
        message.Content.Should().Be("{oops");
        message.ContentParts.Should().BeNull();
    }

    [Fact]
    public void BuildToolResultMessage_WhenPayloadIsNotObject_ShouldFallbackToPlainToolMessage()
    {
        var message = InvokeBuildToolResultMessage("tc-array", "[1,2,3]");

        message.Role.Should().Be("tool");
        message.ToolCallId.Should().Be("tc-array");
        message.Content.Should().Be("[1,2,3]");
        message.ContentParts.Should().BeNull();
    }

    [Fact]
    public void BuildToolResultMessage_WhenNestedAudioPayloadHasNoMediaType_ShouldUseAudioDefaults()
    {
        var message = InvokeBuildToolResultMessage(
            "tc-audio",
            """{"audio":{"base64":123}}""");

        message.Content.Should().Be("[tool audio output]");
        message.ContentParts.Should().HaveCount(2);
        message.ContentParts![0].Kind.Should().Be(ContentPartKind.Text);
        message.ContentParts[0].Text.Should().Be("[tool audio output]");
        message.ContentParts[1].Kind.Should().Be(ContentPartKind.Audio);
        message.ContentParts[1].DataBase64.Should().Be("123");
        message.ContentParts[1].MediaType.Should().Be("audio/wav");
    }

    [Fact]
    public void BuildToolResultMessage_WhenNestedVideoPayloadUsesDataUri_ShouldNormalizeMediaTypeAndContent()
    {
        var message = InvokeBuildToolResultMessage(
            "tc-video",
            """{"video":{"data":"data:video/webm;base64,dmk="},"message":"clip ready"}""");

        message.Content.Should().Be("clip ready");
        message.ContentParts.Should().HaveCount(2);
        message.ContentParts![0].Kind.Should().Be(ContentPartKind.Text);
        message.ContentParts[1].Kind.Should().Be(ContentPartKind.Video);
        message.ContentParts[1].DataBase64.Should().Be("dmk=");
        message.ContentParts[1].MediaType.Should().Be("video/webm");
    }

    [Fact]
    public void BuildToolResultMessage_WhenNestedImagePayloadUsesLegacyAliases_ShouldPreserveNestedMediaType()
    {
        var message = InvokeBuildToolResultMessage(
            "tc-image",
            """{"image":{"imageBase64":"aW1n","mimeType":"image/webp"},"summary":"snap"}""");

        message.Content.Should().Be("snap");
        message.ContentParts.Should().HaveCount(2);
        message.ContentParts![0].Text.Should().Be("snap");
        message.ContentParts[1].Kind.Should().Be(ContentPartKind.Image);
        message.ContentParts[1].DataBase64.Should().Be("aW1n");
        message.ContentParts[1].MediaType.Should().Be("image/webp");
    }

    [Fact]
    public void BuildToolResultMessage_WhenMediaPayloadMissingBase64_ShouldFallbackToPlainToolMessage()
    {
        var payload = """{"image":{"mimeType":"image/png"},"text":"no-bytes"}""";

        var message = InvokeBuildToolResultMessage("tc-missing", payload);

        message.Content.Should().Be(payload);
        message.ContentParts.Should().BeNull();
    }

    private static string? InvokeComposeFinalCallId(string? requestId)
    {
        var method = typeof(ToolCallLoop).GetMethod("ComposeFinalCallId", BindingFlags.Static | BindingFlags.NonPublic);
        return (string?)method!.Invoke(null, [requestId]);
    }

    private static ChatMessage InvokeBuildToolResultMessage(string callId, string toolResult)
    {
        var method = typeof(ToolCallLoop).GetMethod("BuildToolResultMessage", BindingFlags.Static | BindingFlags.NonPublic);
        return (ChatMessage)method!.Invoke(null, [callId, toolResult])!;
    }
}
