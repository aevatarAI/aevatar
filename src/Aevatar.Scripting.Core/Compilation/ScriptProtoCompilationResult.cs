using Google.Protobuf;

namespace Aevatar.Scripting.Core.Compilation;

public sealed record ScriptProtoCompilationResult(
    bool IsSuccess,
    IReadOnlyList<ScriptSourceFile> GeneratedSources,
    ByteString DescriptorSet,
    IReadOnlyList<string> Diagnostics)
{
    public static ScriptProtoCompilationResult Empty { get; } = new(
        true,
        Array.Empty<ScriptSourceFile>(),
        ByteString.Empty,
        Array.Empty<string>());
}
