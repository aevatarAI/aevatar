namespace Aevatar.Scripting.Core.Schema;

public sealed class DefaultScriptReadModelSchemaActivationPolicy : IScriptReadModelSchemaActivationPolicy
{
    private readonly HashSet<ScriptReadModelStoreKind> _supportedKinds;

    public DefaultScriptReadModelSchemaActivationPolicy(
        IEnumerable<ScriptReadModelStoreKind>? supportedKinds = null)
    {
        var configuredKinds = supportedKinds?
            .Distinct()
            .ToArray();
        if (configuredKinds == null || configuredKinds.Length == 0)
        {
            configuredKinds = [ScriptReadModelStoreKind.Document, ScriptReadModelStoreKind.Graph];
        }

        _supportedKinds = new HashSet<ScriptReadModelStoreKind>(configuredKinds);
    }

    public ScriptReadModelSchemaActivationResult ValidateActivation(
        ScriptReadModelSchemaActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requiredKinds = new List<ScriptReadModelStoreKind>(2);
        if (request.RequiresDocumentStore)
            requiredKinds.Add(ScriptReadModelStoreKind.Document);
        if (request.RequiresGraphStore)
            requiredKinds.Add(ScriptReadModelStoreKind.Graph);

        if (requiredKinds.Count == 0)
        {
            return new ScriptReadModelSchemaActivationResult(
                IsActivated: true,
                ValidatedStoreKinds: Array.Empty<ScriptReadModelStoreKind>(),
                MissingStoreKinds: Array.Empty<ScriptReadModelStoreKind>(),
                FailureReason: string.Empty);
        }

        var validated = requiredKinds
            .Where(_supportedKinds.Contains)
            .Distinct()
            .ToArray();
        var missing = requiredKinds
            .Where(kind => !_supportedKinds.Contains(kind))
            .Distinct()
            .ToArray();

        if (missing.Length == 0)
        {
            return new ScriptReadModelSchemaActivationResult(
                IsActivated: true,
                ValidatedStoreKinds: validated,
                MissingStoreKinds: Array.Empty<ScriptReadModelStoreKind>(),
                FailureReason: string.Empty);
        }

        return new ScriptReadModelSchemaActivationResult(
            IsActivated: false,
            ValidatedStoreKinds: validated,
            MissingStoreKinds: missing,
            FailureReason: "Missing required read-model store kinds: " + string.Join(", ", missing));
    }
}
