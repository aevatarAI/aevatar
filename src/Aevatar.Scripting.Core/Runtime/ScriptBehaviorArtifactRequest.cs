using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;

namespace Aevatar.Scripting.Core.Runtime;

public sealed record ScriptBehaviorArtifactRequest
{
    public ScriptBehaviorArtifactRequest(
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

    public ScriptBehaviorArtifactRequest(
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

    public string ScriptId { get; }

    public string Revision { get; }

    public ScriptSourcePackage Package { get; }

    public string SourceHash { get; }

    public string ResolvedPackageHash =>
        string.IsNullOrWhiteSpace(SourceHash)
            ? ScriptSourcePackageSerializer.ComputeHash(Package)
            : SourceHash;

    public ScriptBehaviorCompilationRequest ToCompilationRequest() =>
        new(
            ScriptId,
            Revision,
            Package,
            SourceHash);
}
