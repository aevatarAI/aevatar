namespace Aevatar.Scripting.Core.Application;

public sealed record RunScriptCommand(
    string RunId,
    string InputJson,
    string ScriptRevision);
