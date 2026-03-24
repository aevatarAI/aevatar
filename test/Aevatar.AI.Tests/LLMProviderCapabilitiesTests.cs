using Aevatar.AI.Abstractions.LLMProviders;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class LLMProviderCapabilitiesTests
{
    [Fact]
    public void TextOnly_ShouldSupportTextInputAndOutput()
    {
        var caps = LLMProviderCapabilities.TextOnly;

        caps.SupportsInput(ContentPartKind.Text).Should().BeTrue();
        caps.SupportsOutput(ContentPartKind.Text).Should().BeTrue();
    }

    [Fact]
    public void TextOnly_ShouldRejectImageInput()
    {
        var caps = LLMProviderCapabilities.TextOnly;

        caps.SupportsInput(ContentPartKind.Image).Should().BeFalse();
        caps.SupportsInput(ContentPartKind.Audio).Should().BeFalse();
        caps.SupportsInput(ContentPartKind.Video).Should().BeFalse();
    }

    [Fact]
    public void SupportsInput_ShouldAcceptUnspecifiedKind()
    {
        var caps = LLMProviderCapabilities.TextOnly;
        caps.SupportsInput(ContentPartKind.Unspecified).Should().BeTrue();
    }

    [Fact]
    public void SupportsOutput_ShouldAcceptUnspecifiedKind()
    {
        var caps = LLMProviderCapabilities.TextOnly;
        caps.SupportsOutput(ContentPartKind.Unspecified).Should().BeTrue();
    }

    [Fact]
    public void SupportsRequest_ShouldAcceptTextOnlyRequest()
    {
        var caps = LLMProviderCapabilities.TextOnly;
        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hello")],
        };

        caps.SupportsRequest(request).Should().BeTrue();
    }

    [Fact]
    public void SupportsRequest_ShouldRejectRequestWithUnsupportedModality()
    {
        var caps = LLMProviderCapabilities.TextOnly;
        var request = new LLMRequest
        {
            Messages =
            [
                ChatMessage.User(
                [
                    ContentPart.TextPart("describe this image"),
                    ContentPart.ImagePart("AAAA", "image/png"),
                ]),
            ],
        };

        caps.SupportsRequest(request).Should().BeFalse();
    }

    [Fact]
    public void SupportsRequest_ShouldAcceptMultimodalRequestWhenCapable()
    {
        var caps = new LLMProviderCapabilities
        {
            SupportedInputModalities = new HashSet<ContentPartKind>
            {
                ContentPartKind.Text,
                ContentPartKind.Image,
                ContentPartKind.Audio,
            },
        };
        var request = new LLMRequest
        {
            Messages =
            [
                ChatMessage.User(
                [
                    ContentPart.TextPart("describe"),
                    ContentPart.ImagePart("AAAA", "image/png"),
                    ContentPart.AudioPart("BBBB", "audio/wav"),
                ]),
            ],
        };

        caps.SupportsRequest(request).Should().BeTrue();
    }

    [Fact]
    public void SupportsRequest_ShouldRejectWhenOneModalityUnsupported()
    {
        var caps = new LLMProviderCapabilities
        {
            SupportedInputModalities = new HashSet<ContentPartKind>
            {
                ContentPartKind.Text,
                ContentPartKind.Image,
            },
        };
        var request = new LLMRequest
        {
            Messages =
            [
                ChatMessage.User(
                [
                    ContentPart.TextPart("describe"),
                    ContentPart.ImagePart("AAAA", "image/png"),
                    ContentPart.VideoPart("CCCC", "video/mp4"),
                ]),
            ],
        };

        caps.SupportsRequest(request).Should().BeFalse();
    }

    [Fact]
    public void SupportsRequest_ShouldThrowOnNullRequest()
    {
        var caps = LLMProviderCapabilities.TextOnly;
        var act = () => caps.SupportsRequest(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SupportsRequest_ShouldAcceptEmptyMessages()
    {
        var caps = LLMProviderCapabilities.TextOnly;
        var request = new LLMRequest { Messages = [] };

        caps.SupportsRequest(request).Should().BeTrue();
    }

    [Fact]
    public void SupportsRequest_ShouldSkipNullContentParts()
    {
        var caps = LLMProviderCapabilities.TextOnly;
        var request = new LLMRequest
        {
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = "hello",
                    ContentParts = null,
                },
            ],
        };

        caps.SupportsRequest(request).Should().BeTrue();
    }

    [Fact]
    public void Merge_ShouldCombineInputModalities()
    {
        var primary = new LLMProviderCapabilities
        {
            SupportedInputModalities = new HashSet<ContentPartKind> { ContentPartKind.Text },
            SupportedOutputModalities = new HashSet<ContentPartKind> { ContentPartKind.Text },
            SupportsStreaming = true,
            SupportsToolCalls = false,
        };
        var secondary = new LLMProviderCapabilities
        {
            SupportedInputModalities = new HashSet<ContentPartKind> { ContentPartKind.Image, ContentPartKind.Audio },
            SupportedOutputModalities = new HashSet<ContentPartKind> { ContentPartKind.Image },
            SupportsStreaming = false,
            SupportsToolCalls = true,
        };

        var merged = LLMProviderCapabilities.Merge(primary, secondary);

        merged.SupportedInputModalities.Should().Contain(ContentPartKind.Text);
        merged.SupportedInputModalities.Should().Contain(ContentPartKind.Image);
        merged.SupportedInputModalities.Should().Contain(ContentPartKind.Audio);
        merged.SupportedOutputModalities.Should().Contain(ContentPartKind.Text);
        merged.SupportedOutputModalities.Should().Contain(ContentPartKind.Image);
        merged.SupportsStreaming.Should().BeTrue();
        merged.SupportsToolCalls.Should().BeTrue();
    }

    [Fact]
    public void Merge_ShouldReturnSecondaryWhenPrimaryNull()
    {
        var secondary = new LLMProviderCapabilities
        {
            SupportedInputModalities = new HashSet<ContentPartKind> { ContentPartKind.Image },
        };

        var merged = LLMProviderCapabilities.Merge(null, secondary);
        merged.Should().Be(secondary);
    }

    [Fact]
    public void Merge_ShouldReturnPrimaryWhenSecondaryNull()
    {
        var primary = new LLMProviderCapabilities
        {
            SupportedInputModalities = new HashSet<ContentPartKind> { ContentPartKind.Text },
        };

        var merged = LLMProviderCapabilities.Merge(primary, null);
        merged.Should().Be(primary);
    }

    [Fact]
    public void Merge_ShouldReturnTextOnlyWhenBothNull()
    {
        var merged = LLMProviderCapabilities.Merge(null, null);
        merged.Should().Be(LLMProviderCapabilities.TextOnly);
    }

    [Fact]
    public void Merge_ShouldCombineReasoningDeltas()
    {
        var primary = new LLMProviderCapabilities { SupportsReasoningDeltas = false };
        var secondary = new LLMProviderCapabilities { SupportsReasoningDeltas = true };

        var merged = LLMProviderCapabilities.Merge(primary, secondary);
        merged.SupportsReasoningDeltas.Should().BeTrue();
    }
}
