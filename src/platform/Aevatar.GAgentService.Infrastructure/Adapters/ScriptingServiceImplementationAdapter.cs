using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

public sealed class ScriptingServiceImplementationAdapter : IServiceImplementationAdapter
{
    private readonly IScriptDefinitionSnapshotPort _definitionSnapshotPort;

    public ScriptingServiceImplementationAdapter(IScriptDefinitionSnapshotPort definitionSnapshotPort)
    {
        _definitionSnapshotPort = definitionSnapshotPort ?? throw new ArgumentNullException(nameof(definitionSnapshotPort));
    }

    public ServiceImplementationKind ImplementationKind => ServiceImplementationKind.Scripting;

    public async Task<PreparedServiceRevisionArtifact> PrepareRevisionAsync(
        PrepareServiceRevisionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var spec = request.Spec?.ScriptingSpec
            ?? throw new InvalidOperationException("scripting implementation_spec is required.");
        if (string.IsNullOrWhiteSpace(spec.DefinitionActorId))
            throw new InvalidOperationException("scripting definition_actor_id is required.");

        var snapshot = await _definitionSnapshotPort.GetRequiredAsync(
            spec.DefinitionActorId,
            spec.Revision,
            ct);
        var endpoints = snapshot.RuntimeSemantics?.Messages
            .Where(x => x.Kind == ScriptMessageKind.Command)
            .Select(x =>
            {
                var endpointId = string.IsNullOrWhiteSpace(x.DescriptorFullName)
                    ? x.TypeUrl ?? string.Empty
                    : x.DescriptorFullName;
                return new ServiceEndpointDescriptor
                {
                    EndpointId = endpointId,
                    DisplayName = endpointId,
                    Kind = ServiceEndpointKind.Command,
                    RequestTypeUrl = x.TypeUrl ?? string.Empty,
                    ResponseTypeUrl = string.Empty,
                    Description = $"Scripting command endpoint for {endpointId}.",
                };
            })
            .ToArray()
            ?? [];
        if (endpoints.Length == 0)
            throw new InvalidOperationException($"Script '{snapshot.ScriptId}' revision '{snapshot.Revision}' does not declare command endpoints.");

        return new PreparedServiceRevisionArtifact
        {
            Identity = request.Spec.Identity.Clone(),
            RevisionId = request.Spec.RevisionId,
            ImplementationKind = ServiceImplementationKind.Scripting,
            Endpoints = { endpoints },
            ProtocolDescriptorSet = snapshot.ProtocolDescriptorSet,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                ScriptingPlan = new ScriptingServiceDeploymentPlan
                {
                    ScriptId = snapshot.ScriptId,
                    Revision = snapshot.Revision,
                    DefinitionActorId = spec.DefinitionActorId,
                    SourceHash = snapshot.SourceHash,
                    PackageSpec = ToServicePackage(snapshot.ScriptPackage),
                },
            },
        };
    }

    private static ServiceSourcePackageSpec ToServicePackage(ScriptPackageSpec packageSpec)
    {
        var result = new ServiceSourcePackageSpec
        {
            EntryBehaviorTypeName = packageSpec.EntryBehaviorTypeName ?? string.Empty,
            EntrySourcePath = packageSpec.EntrySourcePath ?? string.Empty,
        };
        result.CsharpSources.Add(packageSpec.CsharpSources.Select(x => new ServicePackageFile
        {
            Path = x.Path ?? string.Empty,
            Content = x.Content ?? string.Empty,
        }));
        result.ProtoFiles.Add(packageSpec.ProtoFiles.Select(x => new ServicePackageFile
        {
            Path = x.Path ?? string.Empty,
            Content = x.Content ?? string.Empty,
        }));
        return result;
    }
}
