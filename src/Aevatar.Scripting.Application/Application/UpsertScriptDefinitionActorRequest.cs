namespace Aevatar.Scripting.Application;

public sealed record UpsertScriptDefinitionActorRequest(
    string ScriptId,
    string ScriptRevision,
    string SourceText,
    string SourceHash,
    string RequestId = "",
    string ReplyStreamId = "");
