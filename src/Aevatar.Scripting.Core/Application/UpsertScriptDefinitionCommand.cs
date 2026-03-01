namespace Aevatar.Scripting.Core.Application;

public sealed record UpsertScriptDefinitionCommand(
    string ScriptId,
    string ScriptRevision,
    string SourceText,
    string SourceHash);
