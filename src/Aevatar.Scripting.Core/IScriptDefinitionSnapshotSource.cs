using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Core;

public interface IScriptDefinitionSnapshotSource
{
    ScriptDefinitionSnapshot GetSnapshot();
}
