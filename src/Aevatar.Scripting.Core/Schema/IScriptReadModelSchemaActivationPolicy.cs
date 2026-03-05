namespace Aevatar.Scripting.Core.Schema;

public interface IScriptReadModelSchemaActivationPolicy
{
    ScriptReadModelSchemaActivationResult ValidateActivation(ScriptReadModelSchemaActivationRequest request);
}
