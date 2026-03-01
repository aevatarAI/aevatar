using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Core;

public interface IScriptCatalogSnapshotSource
{
    ScriptCatalogEntrySnapshot? GetEntry(string scriptId);
}
