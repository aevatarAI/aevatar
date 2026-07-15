using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.AI.Abstractions;

public static class ContentPartProtoMapper
{
    public static ChatContentPart ToProto(ContentPart source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var target = new ChatContentPart
        {
            Kind = ToProtoKind(source.Kind),
            Text = source.Text ?? string.Empty,
            DataBase64 = source.DataBase64 ?? string.Empty,
            MediaType = source.MediaType ?? string.Empty,
            Uri = source.Uri ?? string.Empty,
            Name = source.Name ?? string.Empty,
        };
        if (ToProto(source.FileRef) is { } fileRef)
            target.FileRef = fileRef;
        return target;
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
            FileRef = FromProto(source.FileRef),
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
            _ => ChatContentPartKind.Unspecified,
        };

    private static ContentPartKind FromProtoKind(ChatContentPartKind kind) =>
        kind switch
        {
            ChatContentPartKind.Text => ContentPartKind.Text,
            ChatContentPartKind.Image => ContentPartKind.Image,
            ChatContentPartKind.Audio => ContentPartKind.Audio,
            ChatContentPartKind.Video => ContentPartKind.Video,
            _ => ContentPartKind.Unspecified,
        };

    private static ChatFileRef? ToProto(Aevatar.AI.Abstractions.LLMProviders.ChatFileRef? source) =>
        source is null
            ? null
            : new ChatFileRef
            {
                FileId = source.FileId ?? string.Empty,
                ArtifactId = source.ArtifactId ?? string.Empty,
                SourceKind = ToProtoFileSourceKind(source.SourceKind),
                SourceMessageId = source.SourceMessageId ?? string.Empty,
                SourceResourceKey = source.SourceResourceKey ?? string.Empty,
                FileName = source.FileName ?? string.Empty,
                MediaType = source.MediaType ?? string.Empty,
                SizeBytes = source.SizeBytes,
                Sha256 = source.Sha256 ?? string.Empty,
                CreatedAtUnixMs = source.CreatedAtUnixMs,
                ExpiresAtUnixMs = source.ExpiresAtUnixMs,
                OwnerRunId = source.OwnerRunId ?? string.Empty,
                OwnerScopeId = source.OwnerScopeId ?? string.Empty,
            };

    private static Aevatar.AI.Abstractions.LLMProviders.ChatFileRef? FromProto(ChatFileRef? source)
    {
        if (source is null || !HasFileRefIdentity(source))
            return null;

        return new Aevatar.AI.Abstractions.LLMProviders.ChatFileRef
        {
            FileId = Normalize(source.FileId),
            ArtifactId = Normalize(source.ArtifactId),
            SourceKind = FromProtoFileSourceKind(source.SourceKind),
            SourceMessageId = Normalize(source.SourceMessageId),
            SourceResourceKey = Normalize(source.SourceResourceKey),
            FileName = Normalize(source.FileName),
            MediaType = Normalize(source.MediaType),
            SizeBytes = source.SizeBytes,
            Sha256 = Normalize(source.Sha256),
            CreatedAtUnixMs = source.CreatedAtUnixMs,
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
            OwnerRunId = Normalize(source.OwnerRunId),
            OwnerScopeId = Normalize(source.OwnerScopeId),
        };
    }

    private static bool HasFileRefIdentity(ChatFileRef source) =>
        !string.IsNullOrWhiteSpace(source.FileId) ||
        !string.IsNullOrWhiteSpace(source.ArtifactId);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static ChatFileSourceKind ToProtoFileSourceKind(
        Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind kind) =>
        kind switch
        {
            Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind.ChatInput => ChatFileSourceKind.ChatInput,
            Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind.FormUpload => ChatFileSourceKind.FormUpload,
            Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind.ConnectedServiceResource => ChatFileSourceKind.ConnectedServiceResource,
            Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind.ExternalResource => ChatFileSourceKind.ExternalResource,
            Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind.Generated => ChatFileSourceKind.Generated,
            _ => ChatFileSourceKind.Unspecified,
        };

    private static Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind FromProtoFileSourceKind(
        ChatFileSourceKind kind) =>
        kind switch
        {
            ChatFileSourceKind.ChatInput => Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind.ChatInput,
            ChatFileSourceKind.FormUpload => Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind.FormUpload,
            ChatFileSourceKind.ConnectedServiceResource => Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind.ConnectedServiceResource,
            ChatFileSourceKind.ExternalResource => Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind.ExternalResource,
            ChatFileSourceKind.Generated => Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind.Generated,
            _ => Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind.Unspecified,
        };
}
