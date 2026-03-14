using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Core.Compilation;

public sealed record ScriptBehaviorCompilationRequest
{
    public ScriptBehaviorCompilationRequest(
        string ScriptId,
        string Revision,
        ScriptSourcePackage Package,
        string? SourceHash = null)
    {
        this.ScriptId = ScriptId ?? string.Empty;
        this.Revision = Revision ?? string.Empty;
        this.Package = (Package ?? throw new ArgumentNullException(nameof(Package))).Normalize();
        this.SourceHash = SourceHash ?? string.Empty;
    }

    public ScriptBehaviorCompilationRequest(
        string ScriptId,
        string Revision,
        ScriptPackageSpec Package,
        string? SourceHash = null)
        : this(
            ScriptId,
            Revision,
            ScriptPackageModel.ToSourcePackage(Package),
            SourceHash)
    {
    }

    public ScriptBehaviorCompilationRequest(
        string ScriptId,
        string Revision,
        string Source)
        : this(
            ScriptId,
            Revision,
            ScriptSourcePackage.SingleSource(Source),
            SourceHash: string.Empty)
    {
    }

    public string ScriptId { get; }

    public string Revision { get; }

    public ScriptSourcePackage Package { get; }

    public string SourceHash { get; }

    public string ResolvedPackageHash =>
        string.IsNullOrWhiteSpace(SourceHash)
            ? ScriptSourcePackageSerializer.ComputeHash(Package)
            : SourceHash;

    public bool HasProtoFiles => Package.ProtoFiles.Count > 0;

    public string SourceText => Package.CSharpSources.Count == 1 && Package.ProtoFiles.Count == 0
        ? Package.CSharpSources[0].Content
        : ScriptSourcePackageSerializer.Serialize(Package);

    public static ScriptBehaviorCompilationRequest FromPersistedSource(
        string scriptId,
        string revision,
        string sourceText,
        string? sourceHash = null)
    {
        return new ScriptBehaviorCompilationRequest(
            scriptId,
            revision,
            ScriptSourcePackageSerializer.DeserializeOrWrapCSharp(sourceText),
            sourceHash);
    }
}
