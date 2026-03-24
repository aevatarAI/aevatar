using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ContentPartProtoMapperTests
{
    [Fact]
    public void ToProto_ShouldMapAllFieldsForImagePart()
    {
        var source = new ContentPart
        {
            Kind = ContentPartKind.Image,
            DataBase64 = "iVBORw0KGgo=",
            MediaType = "image/png",
            Name = "photo",
            Uri = "https://example.com/photo.png",
            Text = null,
        };

        var proto = ContentPartProtoMapper.ToProto(source);

        proto.Kind.Should().Be(ChatContentPartKind.Image);
        proto.DataBase64.Should().Be("iVBORw0KGgo=");
        proto.MediaType.Should().Be("image/png");
        proto.Name.Should().Be("photo");
        proto.Uri.Should().Be("https://example.com/photo.png");
        proto.Text.Should().BeEmpty();
    }

    [Fact]
    public void FromProto_ShouldMapAllFieldsForTextPart()
    {
        var proto = new ChatContentPart
        {
            Kind = ChatContentPartKind.Text,
            Text = "hello world",
        };

        var result = ContentPartProtoMapper.FromProto(proto);

        result.Kind.Should().Be(ContentPartKind.Text);
        result.Text.Should().Be("hello world");
        result.DataBase64.Should().BeNull();
        result.MediaType.Should().BeNull();
        result.Uri.Should().BeNull();
        result.Name.Should().BeNull();
    }

    [Fact]
    public void RoundTrip_ShouldPreserveAllFields()
    {
        var original = new ContentPart
        {
            Kind = ContentPartKind.Audio,
            DataBase64 = "AAAA",
            MediaType = "audio/wav",
            Name = "clip.wav",
            Uri = "https://example.com/clip.wav",
            Text = "transcription",
        };

        var roundTripped = ContentPartProtoMapper.FromProto(ContentPartProtoMapper.ToProto(original));

        roundTripped.Kind.Should().Be(original.Kind);
        roundTripped.DataBase64.Should().Be(original.DataBase64);
        roundTripped.MediaType.Should().Be(original.MediaType);
        roundTripped.Name.Should().Be(original.Name);
        roundTripped.Uri.Should().Be(original.Uri);
        roundTripped.Text.Should().Be(original.Text);
    }

    [Fact]
    public void ToProto_ShouldConvertNullFieldsToEmptyStrings()
    {
        var source = new ContentPart
        {
            Kind = ContentPartKind.Video,
            Text = null,
            DataBase64 = null,
            MediaType = null,
            Uri = null,
            Name = null,
        };

        var proto = ContentPartProtoMapper.ToProto(source);

        proto.Text.Should().BeEmpty();
        proto.DataBase64.Should().BeEmpty();
        proto.MediaType.Should().BeEmpty();
        proto.Uri.Should().BeEmpty();
        proto.Name.Should().BeEmpty();
    }

    [Fact]
    public void FromProto_ShouldConvertEmptyStringsToNull()
    {
        var proto = new ChatContentPart
        {
            Kind = ChatContentPartKind.Image,
            Text = "",
            DataBase64 = "",
            MediaType = "",
            Uri = "",
            Name = "",
        };

        var result = ContentPartProtoMapper.FromProto(proto);

        result.Text.Should().BeNull();
        result.DataBase64.Should().BeNull();
        result.MediaType.Should().BeNull();
        result.Uri.Should().BeNull();
        result.Name.Should().BeNull();
    }

    [Fact]
    public void FromProto_ShouldConvertWhitespaceOnlyFieldsToNull()
    {
        var proto = new ChatContentPart
        {
            Kind = ChatContentPartKind.Audio,
            Text = "   ",
            DataBase64 = " \t ",
            MediaType = "  ",
            Uri = " ",
            Name = "\t",
        };

        var result = ContentPartProtoMapper.FromProto(proto);

        result.Text.Should().BeNull();
        result.DataBase64.Should().BeNull();
        result.MediaType.Should().BeNull();
        result.Uri.Should().BeNull();
        result.Name.Should().BeNull();
    }

    [Fact]
    public void ToProto_ShouldMapUnspecifiedKind()
    {
        var source = new ContentPart { Kind = ContentPartKind.Unspecified };
        var proto = ContentPartProtoMapper.ToProto(source);
        proto.Kind.Should().Be(ChatContentPartKind.Unspecified);
    }

    [Fact]
    public void FromProto_ShouldMapUnspecifiedKind()
    {
        var proto = new ChatContentPart { Kind = ChatContentPartKind.Unspecified };
        var result = ContentPartProtoMapper.FromProto(proto);
        result.Kind.Should().Be(ContentPartKind.Unspecified);
    }

    [Theory]
    [InlineData(ContentPartKind.Text, ChatContentPartKind.Text)]
    [InlineData(ContentPartKind.Image, ChatContentPartKind.Image)]
    [InlineData(ContentPartKind.Audio, ChatContentPartKind.Audio)]
    [InlineData(ContentPartKind.Video, ChatContentPartKind.Video)]
    [InlineData(ContentPartKind.Pdf, ChatContentPartKind.Pdf)]
    [InlineData(ContentPartKind.Unspecified, ChatContentPartKind.Unspecified)]
    public void KindMapping_ShouldBeSymmetric(ContentPartKind domainKind, ChatContentPartKind protoKind)
    {
        var source = new ContentPart { Kind = domainKind };
        ContentPartProtoMapper.ToProto(source).Kind.Should().Be(protoKind);

        var proto = new ChatContentPart { Kind = protoKind };
        ContentPartProtoMapper.FromProto(proto).Kind.Should().Be(domainKind);
    }

    [Fact]
    public void ToProto_ShouldThrowOnNullSource()
    {
        var act = () => ContentPartProtoMapper.ToProto(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromProto_ShouldThrowOnNullSource()
    {
        var act = () => ContentPartProtoMapper.FromProto(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToProtoList_ShouldHandleNullInput()
    {
        var result = ContentPartProtoMapper.ToProtoList(null);
        result.Should().BeEmpty();
    }

    [Fact]
    public void FromProtoList_ShouldHandleNullInput()
    {
        var result = ContentPartProtoMapper.FromProtoList(null);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ToProtoList_ShouldHandleEmptyInput()
    {
        var result = ContentPartProtoMapper.ToProtoList([]);
        result.Should().BeEmpty();
    }

    [Fact]
    public void FromProtoList_ShouldHandleEmptyInput()
    {
        var result = ContentPartProtoMapper.FromProtoList([]);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ToProtoList_ShouldMapMultipleParts()
    {
        var parts = new[]
        {
            ContentPart.TextPart("hello"),
            ContentPart.ImagePart("AAAA", "image/png"),
            ContentPart.AudioUriPart("https://example.com/clip.wav"),
        };

        var result = ContentPartProtoMapper.ToProtoList(parts);

        result.Should().HaveCount(3);
        result[0].Kind.Should().Be(ChatContentPartKind.Text);
        result[1].Kind.Should().Be(ChatContentPartKind.Image);
        result[2].Kind.Should().Be(ChatContentPartKind.Audio);
    }

    [Fact]
    public void FromProtoList_ShouldMapMultipleParts()
    {
        var parts = new[]
        {
            new ChatContentPart { Kind = ChatContentPartKind.Text, Text = "hello" },
            new ChatContentPart { Kind = ChatContentPartKind.Video, Uri = "https://example.com/v.mp4" },
        };

        var result = ContentPartProtoMapper.FromProtoList(parts);

        result.Should().HaveCount(2);
        result[0].Kind.Should().Be(ContentPartKind.Text);
        result[1].Kind.Should().Be(ContentPartKind.Video);
    }
}
