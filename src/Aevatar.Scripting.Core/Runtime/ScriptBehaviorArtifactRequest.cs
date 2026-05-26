using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;

namespace Aevatar.Scripting.Core.Runtime;

public sealed record ScriptBehaviorArtifactRequest
{
    // Refactor (iter42/cluster-044-scripting-source-package-json-shadow):
    //   Old pattern: Scripting persists and republishes source_text as a compatibility shadow of ScriptPackageSpec; multi-file packages can be encoded as JSON text and reparsed from persisted source.
    //   New principle: ScriptPackageSpec is the sole internal source-package contract for commands/state/events/readmodels; source_text is only an external one-file adapter field at Host/Application boundary.
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
            ? ScriptPackageModel.ComputePackageHash(Package)
            : SourceHash;

    public ScriptBehaviorCompilationRequest ToCompilationRequest() =>
        new(
            ScriptId,
            Revision,
            Package,
            SourceHash);
}
