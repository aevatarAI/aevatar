using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.AI.Abstractions;

public static class ContentPartProtoMapper
{
    public static ChatContentPart ToProto(ContentPart source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new ChatContentPart
        {
            Kind = ToProtoKind(source.Kind),
            Text = source.Text ?? string.Empty,
            DataBase64 = source.DataBase64 ?? string.Empty,
            MediaType = source.MediaType ?? string.Empty,
            Uri = source.Uri ?? string.Empty,
            Name = source.Name ?? string.Empty,
        };
    }

    public static ContentPart FromProto(ChatContentPart source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new ContentPart
        {
            Kind = FromProtoKind(source.Kind),
            Text = string.IsNullOrWhiteSpace(source.Text) ? null : source.Text,
            DataBase64 = string.IsNullOrWhiteSpace(source.DataBase64) ? null : source.DataBase64,
            MediaType = string.IsNullOrWhiteSpace(source.MediaType) ? null : source.MediaType,
            Uri = string.IsNullOrWhiteSpace(source.Uri) ? null : source.Uri,
            Name = string.IsNullOrWhiteSpace(source.Name) ? null : source.Name,
        };
    }

    public static IReadOnlyList<ChatContentPart> ToProtoList(IEnumerable<ContentPart>? source) =>
        source?.Select(ToProto).ToArray() ?? [];

    public static IReadOnlyList<ContentPart> FromProtoList(IEnumerable<ChatContentPart>? source) =>
        source?.Select(FromProto).ToArray() ?? [];

    private static ChatContentPartKind ToProtoKind(ContentPartKind kind) =>
        kind switch
        {
            ContentPartKind.Text => ChatContentPartKind.Text,
            ContentPartKind.Image => ChatContentPartKind.Image,
            ContentPartKind.Audio => ChatContentPartKind.Audio,
            ContentPartKind.Video => ChatContentPartKind.Video,
            ContentPartKind.Pdf => ChatContentPartKind.Pdf,
            _ => ChatContentPartKind.Unspecified,
        };

    private static ContentPartKind FromProtoKind(ChatContentPartKind kind) =>
        kind switch
        {
            ChatContentPartKind.Text => ContentPartKind.Text,
            ChatContentPartKind.Image => ContentPartKind.Image,
            ChatContentPartKind.Audio => ContentPartKind.Audio,
            ChatContentPartKind.Video => ContentPartKind.Video,
            ChatContentPartKind.Pdf => ContentPartKind.Pdf,
            _ => ContentPartKind.Unspecified,
        };
}
